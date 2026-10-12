using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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

        var window = services.GetRequiredService<IOptions<ChatProviderOptions>>().Value
            .ContextWindowTokens;

        Assert.Equal(32_768, window);
        Assert.InRange(ConciergeStayWorkflow.WorstCaseContextTokens, 30_000, window);
    }

    [Fact]
    public void TimeoutAndConcurrencyHaveNoArbitraryUpperLimit()
    {
        var values = ValidSettings(AIProviderNames.Ollama, AIProviderNames.Ollama);
        values["AI:Chat:RequestTimeoutSeconds"] = "601";
        values["AI:Chat:MaxConcurrentRequests"] = "65";
        using var services = CreateServices(values);

        Assert.NotNull(services.GetRequiredService<IChatClient>());
    }

    [Theory]
    [InlineData("8192")]
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
    public void FixedPromptAllowanceCoversInstructionsLabelsAndTemplateOverhead()
    {
        // Instructions, labels, and separators are well under 1 KB of ASCII; the remainder
        // covers chat-template tokens for the at most 16 messages in an answer request.
        var instructions = Encoding.UTF8.GetByteCount((string)typeof(ConciergeStayWorkflow)
            .GetField("ConciergeInstructions", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!);
        const int labelBytes = 256;
        const int messagesPerAnswer = 1 + 1 + (2 * ConciergeStayWorkflow.RecentTurnWindow) + 1 + 1;
        const int templateTokensPerMessage = 16;

        Assert.True(
            instructions + labelBytes + (messagesPerAnswer * templateTokensPerMessage)
                <= ConciergeStayWorkflow.FixedPromptAllowanceTokens,
            $"Fixed prompt text is {instructions} bytes.");
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

    [Fact]
    public async Task OllamaChatRequestsCarryConfiguredContextWindowAndOutputCap()
    {
        using var listener = StartListener(out var endpoint);
        var values = ValidSettings(AIProviderNames.Ollama, AIProviderNames.Ollama);
        values["AI:Chat:Endpoint"] = endpoint;
        values["AI:Chat:ContextWindowTokens"] = "40000";
        values["AI:Chat:ApiKey"] = NewTestApiKey();
        using var services = CreateServices(values);
        var client = services.GetRequiredService<IChatClient>();

        var serverTask = RespondOnceAsync(listener);
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Hello")],
            new ChatOptions { MaxOutputTokens = ConciergeStayWorkflow.AnswerMaxOutputTokens });
        var (requestBody, authorization) = await serverTask;
        using var request = JsonDocument.Parse(requestBody);

        var options = request.RootElement.GetProperty("options");
        Assert.Equal(40_000, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(
            ConciergeStayWorkflow.AnswerMaxOutputTokens,
            options.GetProperty("num_predict").GetInt32());
        // Ollama's done_reason "length" surfaces as FinishReason.Length with the text intact.
        Assert.Equal(ChatFinishReason.Length, response.FinishReason);
        Assert.Equal("Partial answer", response.Text);
        Assert.Null(authorization);
    }

    private static HttpListener StartListener(out string endpoint)
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            endpoint = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(endpoint);
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
    }

    private static async Task<(string Body, string? Authorization)> RespondOnceAsync(
        HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        var authorization = context.Request.Headers["Authorization"];
        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }

        var payload = Encoding.UTF8.GetBytes(
            """
            {"model":"chat-model","created_at":"2026-10-10T00:00:00Z",
             "message":{"role":"assistant","content":"Partial answer"},
             "done":true,"done_reason":"length"}
            """.ReplaceLineEndings(string.Empty) + "\n");
        context.Response.ContentType = "application/x-ndjson";
        context.Response.ContentLength64 = payload.Length;
        await context.Response.OutputStream.WriteAsync(payload);
        context.Response.Close();
        return (body, authorization);
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
            ["AI:Chat:RequestTimeoutSeconds"] = "30",
            ["AI:Chat:MaxConcurrentRequests"] = "2",
            ["AI:Embeddings:Provider"] = embeddingProvider,
            ["AI:Embeddings:Endpoint"] = Endpoint(embeddingProvider),
            ["AI:Embeddings:Model"] = "embedding-model",
            ["AI:Embeddings:Dimensions"] = "768",
            ["AI:Embeddings:RequestTimeoutSeconds"] = "30",
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
