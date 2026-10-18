using Azure.AI.OpenAI;
using Azure.Identity;
using Concierge.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using OpenAI;
using OpenAI.Responses;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;

namespace Concierge.WorkflowServer.AI;

#pragma warning disable OPENAI001 // The Responses SDK and its MEAI adapter are experimental.

public static class AIProviderServiceCollectionExtensions
{
    /// <summary>
    /// Registers the chat client and embedding generator for the configured Foundry or Ollama provider,
    /// validating <c>AI:Chat</c> and <c>AI:Embeddings</c> at startup. Both clients are wrapped with the
    /// configured per-request timeout and concurrency limit.
    /// </summary>
    public static IServiceCollection AddConciergeAIProviders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<AIProviderOptionsValidator>();
        services.AddSingleton<IValidateOptions<ChatProviderOptions>>(
            provider => provider.GetRequiredService<AIProviderOptionsValidator>());
        services.AddSingleton<IValidateOptions<EmbeddingProviderOptions>>(
            provider => provider.GetRequiredService<AIProviderOptionsValidator>());

        services.AddOptions<ChatProviderOptions>()
            .Bind(configuration.GetSection(ChatProviderOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<EmbeddingProviderOptions>()
            .Bind(configuration.GetSection(EmbeddingProviderOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IChatClient>(services =>
        {
            var options = services.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
            var inner = CreateChatClient(options);
            return new BoundedChatClient(
                inner,
                TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                options.MaxConcurrentRequests);
        });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(services =>
        {
            var options = services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value;
            var inner = CreateEmbeddingGenerator(options);
            return new BoundedEmbeddingGenerator(
                inner,
                TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                options.MaxConcurrentRequests);
        });

        return services;
    }

    internal static IChatClient CreateChatClient(
        ChatProviderOptions options,
        PipelineTransport? transport = null)
    {
        var clientOptions = new ResponsesClientOptions
        {
            Endpoint = AIProviderEndpoints.NormalizeResponsesEndpoint(options.Provider, options.Endpoint),
            // The outer bounded client owns the request deadline.
            NetworkTimeout = Timeout.InfiniteTimeSpan,
        };
        if (transport is not null)
        {
            clientOptions.Transport = transport;
        }

        var isOllama = IsOllama(options.Provider);
        var responseClient = isOllama || !string.IsNullOrEmpty(options.ApiKey)
            ? new ResponsesClient(
                new ApiKeyCredential(isOllama ? "ollama" : options.ApiKey!),
                clientOptions)
            : new ResponsesClient(
                new BearerTokenPolicy(
                    new DefaultAzureCredential(),
                    "https://ai.azure.com/.default"),
                clientOptions);

        var effort = string.Equals(options.ReasoningEffort, "none", StringComparison.OrdinalIgnoreCase)
            ? ReasoningEffort.None
            : ReasoningEffort.Low;

        return new ChatClientBuilder(responseClient.AsIChatClient(options.Model))
            .ConfigureOptions(chatOptions =>
            {
                // The Temporal stay workflow provides complete history and owns conversation state.
                if (chatOptions.ConversationId is not null
                    || chatOptions.ContinuationToken is not null
                    || chatOptions.AllowBackgroundResponses == true
                    || chatOptions.RawRepresentationFactory is not null)
                {
                    throw new ArgumentException(
                        "The concierge Responses client accepts stateless chat options only.");
                }

                chatOptions.Reasoning ??= new ReasoningOptions { Effort = effort };
                // Foundry reasoning requests reject temperature, including zero; Ollama accepts it.
                if (!isOllama)
                {
                    chatOptions.Temperature = null;
                }
                chatOptions.RawRepresentationFactory =
                    _ => new CreateResponseOptions { StoredOutputEnabled = false };
            })
            // Inside the options step so spans carry the effective options; sensitive data stays off.
            .UseOpenTelemetry()
            .Build();
    }

#pragma warning restore OPENAI001

    /// <summary>
    /// Ollama <c>keep_alive</c> sent on every embedding request so the host never unloads the
    /// embedding model (its default is five idle minutes, costing ~3 s on the next retrieval).
    /// OllamaSharp maps <see cref="EmbeddingGenerationOptions.AdditionalProperties"/>["keep_alive"]
    /// (string only) to <c>EmbedRequest.KeepAlive</c>. Ollama parses strings with Go's
    /// <c>time.ParseDuration</c>, so a unit is required: <c>"-1"</c> is rejected with HTTP 400,
    /// while any negative duration such as <c>"-1m"</c> means "keep loaded indefinitely".
    /// </summary>
    internal const string OllamaEmbeddingKeepAlive = "-1m";

    internal static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        EmbeddingProviderOptions options,
        HttpMessageHandler? ollamaHandler = null)
    {
        var isOllama = IsOllama(options.Provider);
        IEmbeddingGenerator<string, Embedding<float>> provider = isOllama
            ? CreateOllamaClient(options, ollamaHandler)
            : CreateAzureClient(options).GetEmbeddingClient(options.Model).AsIEmbeddingGenerator(options.Dimensions);

        var builder = new EmbeddingGeneratorBuilder<string, Embedding<float>>(provider);
        if (isOllama)
        {
            // Applied to a clone of the caller's options; an explicit caller value wins.
            builder.ConfigureOptions(embeddingOptions =>
            {
                embeddingOptions.AdditionalProperties ??= [];
                embeddingOptions.AdditionalProperties.TryAdd("keep_alive", OllamaEmbeddingKeepAlive);
            });
        }

        // Sensitive data stays off: spans and metrics carry model and usage, never input text.
        return builder
            .UseOpenTelemetry()
            .Build();
    }

    private static AzureOpenAIClient CreateAzureClient(AIProviderOptions options)
    {
        var clientOptions = new AzureOpenAIClientOptions();
        var endpoint = new Uri(options.Endpoint);

        return !string.IsNullOrEmpty(options.ApiKey)
            ? new AzureOpenAIClient(
                endpoint,
                new ApiKeyCredential(options.ApiKey),
                clientOptions)
            : new AzureOpenAIClient(
                endpoint,
                new DefaultAzureCredential(),
                clientOptions);
    }

    private static OllamaApiClient CreateOllamaClient(
        AIProviderOptions options,
        HttpMessageHandler? handler = null)
    {
        // Embeddings retain the existing Ollama API; only chat moves to Responses.
        // The outer bounded generator owns the deadline.
        var httpClient = new HttpClient(handler ?? new HttpClientHandler())
        {
            BaseAddress = new Uri(options.Endpoint),
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return new OllamaApiClient(httpClient, options.Model);
    }

    private static bool IsOllama(string provider) =>
        StringComparer.OrdinalIgnoreCase.Equals(provider, AIProviderNames.Ollama);
}

internal sealed class BoundedChatClient(
    IChatClient inner,
    TimeSpan timeout,
    int maxConcurrency) : DelegatingChatClient(inner)
{
    private readonly SemaphoreSlim gate = new(maxConcurrency, maxConcurrency);

    /// <inheritdoc/>
    /// <remarks>Waits for a concurrency slot and applies the configured timeout to the wait and the call.</remarks>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = PrepareEndChatFunction(options);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await ConciergeAITelemetry.WaitForSlotAsync(gate, "chat", timeoutSource.Token);
        try
        {
            return await InnerClient.GetResponseAsync(messages, options, timeoutSource.Token);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>Holds a concurrency slot for the whole stream; the timeout covers the entire enumeration.</remarks>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        options = PrepareEndChatFunction(options);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await ConciergeAITelemetry.WaitForSlotAsync(gate, "chat", timeoutSource.Token);
        try
        {
            await foreach (var update in InnerClient
                .GetStreamingResponseAsync(messages, options, timeoutSource.Token)
                .WithCancellation(timeoutSource.Token))
            {
                yield return update;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Restores the voice-only function declaration after Temporal's durable activity serializer
    /// carried its marker in AdditionalProperties. The function is exposed to the model only; the
    /// activity does not execute it, so the workflow can return the client-side call to Deepgram.
    /// </summary>
    internal static ChatOptions? PrepareEndChatFunction(ChatOptions? options)
    {
        if (options?.AdditionalProperties is not { } properties
            || !properties.TryGetValue(ConciergeEndChat.ModelOptionKey, out var enabled)
            || !IsEndChatFunctionMarker(enabled))
        {
            return options;
        }

        properties.Remove(ConciergeEndChat.ModelOptionKey);
        options.Tools =
        [
            AIFunctionFactory.Create(
                (Func<Task>)(static () => Task.CompletedTask),
                new AIFunctionFactoryOptions
                {
                    Name = ConciergeEndChat.FunctionName,
                    Description = "End the guest's chat only when they clearly and explicitly ask to end the conversation.",
                }),
        ];
        return options;
    }

    private static bool IsEndChatFunctionMarker(object? marker) =>
        marker switch
        {
            string value => StringComparer.Ordinal.Equals(value, "true"),
            JsonElement { ValueKind: JsonValueKind.String } value =>
                StringComparer.Ordinal.Equals(value.GetString(), "true"),
            _ => false,
        };
}

internal sealed class BoundedEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner,
    TimeSpan timeout,
    int maxConcurrency) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly SemaphoreSlim gate = new(maxConcurrency, maxConcurrency);

    /// <inheritdoc/>
    /// <remarks>Waits for a concurrency slot and applies the configured timeout to the wait and the call.</remarks>
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await ConciergeAITelemetry.WaitForSlotAsync(gate, "embeddings", timeoutSource.Token);
        try
        {
            return await inner.GenerateAsync(values, options, timeoutSource.Token);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);

    /// <summary>
    /// Disposes the concurrency gate and the wrapped generator.
    /// </summary>
    public void Dispose()
    {
        gate.Dispose();
        inner.Dispose();
    }
}
