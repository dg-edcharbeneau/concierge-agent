using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using System.ClientModel;

namespace Concierge.WorkflowServer.AI;

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

    private static IChatClient CreateChatClient(ChatProviderOptions options)
    {
        if (IsOllama(options.Provider))
        {
            // Pin num_ctx on the worker side so Ollama never falls back to a smaller default
            // window. OllamaSharp maps the "num_ctx" additional property to options.num_ctx;
            // ConfigureOptions clones the per-call options deserialized by the activity.
            return new ChatClientBuilder(CreateOllamaClient(options))
                .ConfigureOptions(chatOptions =>
                    (chatOptions.AdditionalProperties ??= [])[OllamaOption.NumCtx.Name] =
                        options.ContextWindowTokens)
                .Build();
        }

        var client = CreateAzureClient(options);
        return client.GetChatClient(options.Model).AsIChatClient();
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        EmbeddingProviderOptions options)
    {
        if (IsOllama(options.Provider))
        {
            return CreateOllamaClient(options);
        }

        var client = CreateAzureClient(options);
        return client.GetEmbeddingClient(options.Model).AsIEmbeddingGenerator(options.Dimensions);
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

    private static OllamaApiClient CreateOllamaClient(AIProviderOptions options)
    {
        // The outer MEAI decorators own the configured deadline. Disable HttpClient's
        // unrelated 100-second default so the same bound applies to both providers.
        var httpClient = new HttpClient
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
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await gate.WaitAsync(timeoutSource.Token);
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
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        await gate.WaitAsync(timeoutSource.Token);
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
        await gate.WaitAsync(timeoutSource.Token);
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
