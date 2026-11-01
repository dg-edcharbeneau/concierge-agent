using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.ClientModel.Primitives;

namespace Concierge.Core.Tests;

public sealed class AIProviderRegistrationTests
{
    [Theory]
    [InlineData(AIProviderNames.Foundry, AIProviderNames.Foundry)]
    [InlineData(AIProviderNames.Foundry, AIProviderNames.Ollama)]
    [InlineData(AIProviderNames.Ollama, AIProviderNames.Foundry)]
    [InlineData(AIProviderNames.Ollama, AIProviderNames.Ollama)]
    public void ChatAndEmbeddingProvidersAreIndependentlySelectable(
        string chatProvider,
        string embeddingProvider)
    {
        using var services = CreateServices(chatProvider, embeddingProvider);

        Assert.NotNull(services.GetRequiredService<IChatClient>());
        Assert.NotNull(
            services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
        Assert.Equal(
            chatProvider,
            services.GetRequiredService<IOptions<ChatProviderOptions>>().Value.Provider);
        Assert.Equal(
            embeddingProvider,
            services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value.Provider);
    }

    [Fact]
    public void MalformedSettingsReportEveryActionableFailure()
    {
        var values = ValidSettings(AIProviderNames.Foundry, AIProviderNames.Foundry);
        values["AI:Chat:Provider"] = "Unknown";
        values["AI:Chat:Endpoint"] = "not-a-uri";
        values["AI:Chat:Model"] = "";
        values["AI:Chat:RequestTimeoutSeconds"] = "0";
        values["AI:Chat:MaxConcurrentRequests"] = "0";
        using var services = CreateServices(values);

        var error = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IChatClient>());

        Assert.Contains("AI:Chat:Provider", error.Message, StringComparison.Ordinal);
        Assert.Contains("AI:Chat:Endpoint", error.Message, StringComparison.Ordinal);
        Assert.Contains("AI:Chat:Model", error.Message, StringComparison.Ordinal);
        Assert.Contains("AI:Chat:RequestTimeoutSeconds", error.Message, StringComparison.Ordinal);
        Assert.Contains("AI:Chat:MaxConcurrentRequests", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundryRejectsHttpEndpoint()
    {
        var values = ValidSettings(AIProviderNames.Foundry, AIProviderNames.Foundry);
        values["AI:Chat:Endpoint"] = "http://concierge.openai.azure.com/";
        using var services = CreateServices(values);

        var error = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IChatClient>());

        Assert.Contains("AI:Chat:Endpoint", error.Message, StringComparison.Ordinal);
        Assert.Contains("HTTPS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatRejectsUnsupportedResponsesEndpointPaths()
    {
        var values = ValidSettings(AIProviderNames.Ollama, AIProviderNames.Ollama);
        values["AI:Chat:Endpoint"] = "http://127.0.0.1:11434/api/chat";
        using var services = CreateServices(values);

        var error = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IChatClient>());

        Assert.Contains("AI:Chat:Endpoint", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatRejectsUnrecognizedReasoningEffort()
    {
        var values = ValidSettings(AIProviderNames.Ollama, AIProviderNames.Ollama);
        values["AI:Chat:ReasoningEffort"] = "medium";
        using var services = CreateServices(values);

        var error = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IChatClient>());

        Assert.Contains("AI:Chat:ReasoningEffort", error.Message, StringComparison.Ordinal);
        Assert.Contains("'low' or 'none'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidEmbeddingDimensionsDoNotDiscloseConfiguredApiKey()
    {
        var secret = NewTestApiKey();
        var values = ValidSettings(AIProviderNames.Foundry, AIProviderNames.Foundry);
        values["AI:Embeddings:ApiKey"] = secret;
        values["AI:Embeddings:Dimensions"] = "0";
        using var services = CreateServices(values);

        var error = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<
                IEmbeddingGenerator<string, Embedding<float>>>());

        Assert.Contains("AI:Embeddings:Dimensions", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FoundryWithoutApiKeyBuildsClientWithoutAcquiringCredentials()
    {
        var values = ValidSettings(AIProviderNames.Foundry, AIProviderNames.Foundry);
        using var services = CreateServices(values);

        var client = services.GetRequiredService<IChatClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public void FoundryWithApiKeyBuildsClientWithoutNetworkRequest()
    {
        var values = ValidSettings(AIProviderNames.Foundry, AIProviderNames.Foundry);
        values["AI:Chat:ApiKey"] = NewTestApiKey();
        values["AI:Embeddings:ApiKey"] = NewTestApiKey();
        using var services = CreateServices(values);

        Assert.NotNull(services.GetRequiredService<IChatClient>());
        Assert.NotNull(
            services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void OllamaIgnoresConfiguredApiKeys()
    {
        var values = ValidSettings(AIProviderNames.Ollama, AIProviderNames.Ollama);
        values["AI:Chat:ApiKey"] = NewTestApiKey();
        values["AI:Embeddings:ApiKey"] = NewTestApiKey();
        using var services = CreateServices(values);

        Assert.NotNull(services.GetRequiredService<IChatClient>());
        Assert.NotNull(
            services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>());
    }

    [Fact]
    public void EmbeddingDimensionsMustMatchTheMigratedSchema()
    {
        var values = ValidSettings(AIProviderNames.Foundry, AIProviderNames.Foundry);
        values["AI:Embeddings:Dimensions"] = "1024";
        using var services = CreateServices(values);

        var error = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<
                IEmbeddingGenerator<string, Embedding<float>>>());

        Assert.Contains("must be 768", error.Message, StringComparison.Ordinal);
        Assert.Contains("migration 0002", error.Message, StringComparison.Ordinal);
        Assert.Contains("re-ingest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatContextWindowDefaultsTo32KAndCoversWorstCaseBudget()
    {
        using var services = CreateServices(AIProviderNames.Ollama, AIProviderNames.Ollama);

        var options = services.GetRequiredService<IOptions<ChatProviderOptions>>().Value;

        Assert.Equal(32_768, options.ContextWindowTokens);
        Assert.Equal(
            ConciergeStayWorkflow.ActionWorkingContextBudgetTokens,
            ConciergeStayWorkflow.WorstCaseContextTokens);
        Assert.Equal("low", options.ReasoningEffort);
    }

    [Fact]
    public void ConcurrencyHasNoArbitraryUpperLimit()
    {
        // RequestTimeoutSeconds is capped by its activity allowance (ModelCallBoundTests).
        var values = ValidSettings(AIProviderNames.Ollama, AIProviderNames.Ollama);
        values["AI:Chat:MaxConcurrentRequests"] = "65";
        using var services = CreateServices(values);

        Assert.NotNull(services.GetRequiredService<IChatClient>());
    }

    [Theory]
    [InlineData("11999")]
    [InlineData("0")]
    [InlineData("-1")]
    public void ContextWindowSmallerThanWorstCaseBudgetFailsWithActionableMessage(string window)
    {
        var values = ValidSettings(AIProviderNames.Ollama, AIProviderNames.Ollama);
        values["AI:Chat:ContextWindowTokens"] = window;
        using var services = CreateServices(values);

        var error = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IChatClient>());

        Assert.Contains("AI:Chat:ContextWindowTokens", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            ConciergeStayWorkflow.WorstCaseContextTokens.ToString(CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PromptEstimateCountsInstructionsSeparatelyFromFixedAllowance()
    {
        var instructions = Encoding.UTF8.GetByteCount(ConciergeStayWorkflow.ConciergeInstructions);
        ChatMessage[] messages = [new(ChatRole.System, ConciergeStayWorkflow.ConciergeInstructions)];

        Assert.Equal(
            ConciergeStayWorkflow.FixedPromptAllowanceTokens + instructions + 8,
            ConciergeStayWorkflow.EstimatePromptTokens(messages));
    }

    [Fact]
    public void Utf8ByteLimitRejectsMultiByteTextThatPassesACharacterCount()
    {
        var ascii = new string('a', ConciergeTurnLimits.MaxUserMessageUtf8Bytes);
        var multiByte = new string('\u20AC', 700);

        Assert.True(ConciergeTurnLimits.IsUserMessageWithinLimit(ascii));
        Assert.False(ConciergeTurnLimits.IsUserMessageWithinLimit(ascii + "a"));
        Assert.True(multiByte.Length < ConciergeTurnLimits.MaxUserMessageUtf8Bytes);
        Assert.False(ConciergeTurnLimits.IsUserMessageWithinLimit(multiByte));
    }

    [Theory]
    [InlineData(AIProviderNames.Ollama, "low", null)]
    [InlineData(AIProviderNames.Ollama, "low", 0f)]
    [InlineData(AIProviderNames.Ollama, "none", null)]
    [InlineData(AIProviderNames.Ollama, "none", 0f)]
    [InlineData(AIProviderNames.Foundry, "low", null)]
    [InlineData(AIProviderNames.Foundry, "low", 0f)]
    [InlineData(AIProviderNames.Foundry, "none", null)]
    [InlineData(AIProviderNames.Foundry, "none", 0f)]
    public async Task ChatResponsesRequestsUseProviderContractAndReasoningSetting(
        string provider,
        string reasoningEffort,
        float? temperature)
    {
        var values = ValidSettings(provider, AIProviderNames.Ollama);
        values["AI:Chat:Endpoint"] = provider == AIProviderNames.Ollama
            ? "http://127.0.0.1:11434"
            : "https://concierge.openai.azure.com";
        values["AI:Chat:ContextWindowTokens"] = "40000";
        values["AI:Chat:ReasoningEffort"] = reasoningEffort;
        values["AI:Chat:Model"] = provider == AIProviderNames.Foundry ? "gpt-5-mini" : "chat-model";
        var apiKey = NewTestApiKey();
        values["AI:Chat:ApiKey"] = apiKey;
        using var services = CreateServices(values);
        var options = services.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
        var handler = new ResponsesApiHandler();
        using var httpClient = new HttpClient(handler);
        var transport = new HttpClientPipelineTransport(httpClient);
        var client = AIProviderServiceCollectionExtensions.CreateChatClient(options, transport);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")],
            new ChatOptions
            {
                MaxOutputTokens = ConciergeStayWorkflow.AnswerMaxOutputTokens,
                Temperature = temperature,
                AllowMultipleToolCalls = false,
                Tools = ConciergeToolDefinitions.CreateModelTools(false).Cast<AITool>().ToList(),
            });
        using var request = JsonDocument.Parse(handler.RequestBody!);

        Assert.Equal(
            provider == AIProviderNames.Ollama ? "/v1/responses" : "/openai/v1/responses",
            handler.RequestPath);
        Assert.Equal(
            "Bearer " + (provider == AIProviderNames.Ollama ? "ollama" : apiKey),
            handler.Authorization);
        Assert.Equal(options.Model, request.RootElement.GetProperty("model").GetString());
        if (provider == AIProviderNames.Ollama && temperature.HasValue)
        {
            Assert.Equal(temperature.Value, request.RootElement.GetProperty("temperature").GetSingle());
        }
        else
        {
            Assert.False(request.RootElement.TryGetProperty("temperature", out _));
        }
        Assert.False(request.RootElement.GetProperty("store").GetBoolean());
        Assert.False(request.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(request.RootElement.TryGetProperty("num_ctx", out _));
        Assert.Equal(
            reasoningEffort,
            request.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(
            ConciergeStayWorkflow.AnswerMaxOutputTokens,
            request.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("Partial answer", response.Text);
        Assert.Equal(
            40_000,
            options.ContextWindowTokens);
    }

    [Fact]
    public async Task OllamaEmbeddingRequestsKeepTheModelLoaded()
    {
        using var services = CreateServices(AIProviderNames.Ollama, AIProviderNames.Ollama);
        var options = services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value;
        var handler = new OllamaEmbedHandler();
        using var generator = AIProviderServiceCollectionExtensions.CreateEmbeddingGenerator(options, handler);

        var result = await generator.GenerateAsync(["first question"]);
        using var request = JsonDocument.Parse(handler.RequestBody!);

        Assert.Equal("/api/embed", handler.RequestPath);
        Assert.Equal("embedding-model", request.RootElement.GetProperty("model").GetString());
        // Unitless "-1" is rejected by Ollama (Go time.ParseDuration); a negative duration never unloads.
        Assert.Equal("-1m", request.RootElement.GetProperty("keep_alive").GetString());
        Assert.Single(result);
    }

    [Fact]
    public async Task OllamaEmbeddingKeepAliveDoesNotOverrideCallerValue()
    {
        using var services = CreateServices(AIProviderNames.Ollama, AIProviderNames.Ollama);
        var options = services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value;
        var handler = new OllamaEmbedHandler();
        using var generator = AIProviderServiceCollectionExtensions.CreateEmbeddingGenerator(options, handler);
        var callerOptions = new EmbeddingGenerationOptions
        {
            AdditionalProperties = new() { ["keep_alive"] = "10m" },
        };

        await generator.GenerateAsync(["question"], callerOptions);
        using var request = JsonDocument.Parse(handler.RequestBody!);

        Assert.Equal("10m", request.RootElement.GetProperty("keep_alive").GetString());
        // The caller's options instance is cloned, not mutated.
        Assert.Single(callerOptions.AdditionalProperties);
    }

    [Theory]
    [InlineData(AIProviderNames.Ollama, 1)]
    [InlineData(AIProviderNames.Foundry, 0)]
    public async Task EmbeddingWarmupRunsOnlyForOllamaAndIsNonFatal(string provider, int expectedCalls)
    {
        using var services = CreateServices(AIProviderNames.Ollama, provider);
        var generator = new FailingEmbeddingGenerator();
        using var warmup = new OllamaEmbeddingWarmup(
            generator,
            services.GetRequiredService<IOptions<EmbeddingProviderOptions>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OllamaEmbeddingWarmup>.Instance);

        await warmup.StartAsync(CancellationToken.None);
        await warmup.ExecuteTask!;

        Assert.Equal(expectedCalls, generator.Calls);
        Assert.True(warmup.ExecuteTask.IsCompletedSuccessfully);
    }

    private sealed class FailingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new HttpRequestException("Ollama is not running.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class OllamaEmbedHandler : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri!.AbsolutePath;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"model":"embedding-model","embeddings":[[0.1,0.2,0.3]],"prompt_eval_count":2}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class ResponsesApiHandler : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        public string? RequestBody { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri!.AbsolutePath;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"id":"resp_test","object":"response","created_at":1791936000,"status":"completed",
                     "model":"chat-model","output":[{"id":"msg_test","type":"message","status":"completed",
                     "role":"assistant","content":[{"type":"output_text","text":"Partial answer","annotations":[]}]}],
                     "usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private static ServiceProvider CreateServices(
        string chatProvider,
        string embeddingProvider) =>
        CreateServices(ValidSettings(chatProvider, embeddingProvider));

    private static ServiceProvider CreateServices(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConciergeAIProviders(configuration);
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static Dictionary<string, string?> ValidSettings(
        string chatProvider,
        string embeddingProvider) => new()
        {
            ["AI:Chat:Provider"] = chatProvider,
            ["AI:Chat:Endpoint"] = Endpoint(chatProvider),
            ["AI:Chat:Model"] = "chat-model",
            ["AI:Chat:RequestTimeoutSeconds"] = "18",
            ["AI:Chat:MaxConcurrentRequests"] = "2",
            ["AI:Embeddings:Provider"] = embeddingProvider,
            ["AI:Embeddings:Endpoint"] = Endpoint(embeddingProvider),
            ["AI:Embeddings:Model"] = "embedding-model",
            ["AI:Embeddings:Dimensions"] = "768",
            ["AI:Embeddings:RequestTimeoutSeconds"] = "8",
            ["AI:Embeddings:MaxConcurrentRequests"] = "2",
        };

    private static string Endpoint(string provider) =>
        provider == AIProviderNames.Foundry
            ? "https://concierge.openai.azure.com/"
            : "http://127.0.0.1:11434/";

    private static string NewTestApiKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}

public sealed class EmbeddingModelIdentityTests
{
    [Fact]
    public void MatchingIdentityIsAccepted()
    {
        var configured = new EmbeddingModelIdentity("Ollama", "embeddinggemma:latest", 768);
        var indexed = new EmbeddingModelIdentity("ollama", "embeddinggemma:latest", 768);

        Assert.True(configured.IsCompatibleWith(indexed));
        configured.EnsureCompatibleWith(indexed);
    }

    [Theory]
    [InlineData("Foundry", "embeddinggemma:latest", 768)]
    [InlineData("Ollama", "different-model", 768)]
    [InlineData("Ollama", "embeddinggemma:latest", 1024)]
    public void MismatchedIdentityExplicitlyRefusesVectorSearch(
        string provider,
        string model,
        int dimensions)
    {
        var configured = new EmbeddingModelIdentity("Ollama", "embeddinggemma:latest", 768);
        var indexed = new EmbeddingModelIdentity(provider, model, dimensions);

        Assert.False(configured.IsCompatibleWith(indexed));
        var error = Assert.Throws<InvalidOperationException>(
            () => configured.EnsureCompatibleWith(indexed));

        Assert.Contains("Refusing vector search", error.Message, StringComparison.Ordinal);
        Assert.Contains("re-ingest", error.Message, StringComparison.Ordinal);
    }
}
