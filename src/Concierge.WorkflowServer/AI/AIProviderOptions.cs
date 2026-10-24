using Concierge.WorkflowServer.Workflows;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.AI;

public static class AIProviderNames
{
    public const string Foundry = "Foundry";
    public const string Ollama = "Ollama";
}

public abstract class AIProviderOptions
{
    public string Provider { get; set; } = AIProviderNames.Foundry;

    public string Endpoint { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string? ApiKey { get; set; }

    /// <summary>
    /// Bound on one provider call, including the concurrency-slot wait. Must finish inside the
    /// Temporal activity attempt that encloses the call; the validator enforces the ceiling.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; }

    public int MaxConcurrentRequests { get; set; } = 4;
}

public sealed class ChatProviderOptions : AIProviderOptions
{
    public const string SectionName = "AI:Chat";

    /// <summary>
    /// Ceiling for <see cref="AIProviderOptions.RequestTimeoutSeconds"/>: the model activity's
    /// per-attempt allowance less <see cref="ActivityCallBudget.CompletionMargin"/> (18 s).
    /// </summary>
    public static int MaxRequestTimeoutSeconds =>
        ActivityCallBudget.MaxRequestTimeoutSeconds(ConciergeStayWorkflow.ModelActivityTimeout);

    public ChatProviderOptions() => RequestTimeoutSeconds = MaxRequestTimeoutSeconds;

    /// <summary>
    /// Model context window in tokens. Must cover the worst-case prompt budget.
    /// </summary>
    public int ContextWindowTokens { get; set; } = 32_768;

    /// <summary>
    /// Responses API reasoning effort. <c>low</c> is the general default; <c>none</c> is used
    /// for the measured local Ollama Development configuration.
    /// </summary>
    public string ReasoningEffort { get; set; } = "low";
}

public sealed class EmbeddingProviderOptions : AIProviderOptions
{
    public const string SectionName = "AI:Embeddings";

    /// <summary>
    /// Ceiling for <see cref="AIProviderOptions.RequestTimeoutSeconds"/>: the retrieval activity's
    /// per-attempt allowance less <see cref="ActivityCallBudget.CompletionMargin"/> (8 s). The same
    /// generator embeds ingestion batches inside the much longer setup activity.
    /// </summary>
    public static int MaxRequestTimeoutSeconds =>
        ActivityCallBudget.MaxRequestTimeoutSeconds(ConciergeStayWorkflow.RetrievalActivityTimeout);

    public EmbeddingProviderOptions() => RequestTimeoutSeconds = MaxRequestTimeoutSeconds;

    /// <summary>
    /// Ceiling for <see cref="IngestionRequestTimeoutSeconds"/>: the setup activity's per-attempt
    /// allowance less <see cref="ActivityCallBudget.CompletionMargin"/>.
    /// </summary>
    public static int MaxIngestionRequestTimeoutSeconds =>
        ActivityCallBudget.MaxRequestTimeoutSeconds(HotelSetupWorkflow.SetupActivityTimeout);

    /// <summary>
    /// Bound on one ingestion embedding call: a whole knowledge section, possibly on a cold model,
    /// inside the setup activity. Also used by the Ollama warm-up. Provisional until measured.
    /// </summary>
    public int IngestionRequestTimeoutSeconds { get; set; } = 60;

    public int Dimensions { get; set; }
}

internal sealed class AIProviderOptionsValidator :
    IValidateOptions<ChatProviderOptions>,
    IValidateOptions<EmbeddingProviderOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ChatProviderOptions options)
    {
        var failures = ValidateCommon(
            ChatProviderOptions.SectionName, options, ChatProviderOptions.MaxRequestTimeoutSeconds);
        if (!string.Equals(options.ReasoningEffort, "low", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ReasoningEffort, "none", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                $"{ChatProviderOptions.SectionName}:ReasoningEffort must be 'low' or 'none'.");
        }

        if (options.ContextWindowTokens < ConciergeStayWorkflow.WorstCaseContextTokens)
        {
            failures.Add(
                $"{ChatProviderOptions.SectionName}:ContextWindowTokens is {options.ContextWindowTokens}, "
                + $"but a concierge turn can need up to {ConciergeStayWorkflow.WorstCaseContextTokens} "
                + "tokens. Use a model with a larger context window and set ContextWindowTokens to it "
                + "(default 32768).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, EmbeddingProviderOptions options)
    {
        var failures = ValidateCommon(
            EmbeddingProviderOptions.SectionName, options, EmbeddingProviderOptions.MaxRequestTimeoutSeconds);
        if (options.IngestionRequestTimeoutSeconds < 1
            || options.IngestionRequestTimeoutSeconds > EmbeddingProviderOptions.MaxIngestionRequestTimeoutSeconds)
        {
            failures.Add(
                $"{EmbeddingProviderOptions.SectionName}:IngestionRequestTimeoutSeconds is "
                + $"{options.IngestionRequestTimeoutSeconds}, but it must be between 1 and "
                + $"{EmbeddingProviderOptions.MaxIngestionRequestTimeoutSeconds} so each section's embedding "
                + "call finishes inside the setup activity attempt that retries it.");
        }
        if (options.Dimensions != EmbeddingModelIdentity.MigratedSchemaDimensions)
        {
            failures.Add(
                $"{EmbeddingProviderOptions.SectionName}:Dimensions must be "
                + $"{EmbeddingModelIdentity.MigratedSchemaDimensions} because migration 0002 fixes "
                + "knowledge_chunks.embedding to vector(768). Use a 768-dimension embedding deployment "
                + "or add a deliberate schema migration and re-ingest existing embeddings.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static List<string> ValidateCommon(
        string section,
        AIProviderOptions options,
        int maxRequestTimeoutSeconds)
    {
        var failures = new List<string>();
        if (!IsProvider(options.Provider))
        {
            failures.Add(
                $"{section}:Provider must be '{AIProviderNames.Foundry}' or '{AIProviderNames.Ollama}'.");
        }

        if (options is ChatProviderOptions)
        {
            try
            {
                _ = AIProviderEndpoints.NormalizeResponsesEndpoint(options.Provider, options.Endpoint);
            }
            catch (ArgumentException)
            {
                failures.Add(
                    $"{section}:Endpoint must be a clean resource root or Responses API base URL "
                    + "(Ollama HTTP(S), Foundry HTTPS), without credentials, query, or fragment.");
            }
        }
        else if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            failures.Add($"{section}:Endpoint must be an absolute HTTP or HTTPS URI.");
        }
        else if (IsFoundry(options.Provider) && endpoint.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{section}:Endpoint must use HTTPS for the Foundry provider.");
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            failures.Add(
                $"{section}:Model is required (use the Azure deployment name for Foundry).");
        }

        if (options.RequestTimeoutSeconds < 1)
        {
            failures.Add($"{section}:RequestTimeoutSeconds must be positive.");
        }
        else if (options.RequestTimeoutSeconds > maxRequestTimeoutSeconds)
        {
            failures.Add(
                $"{section}:RequestTimeoutSeconds is {options.RequestTimeoutSeconds}, but it must be at most "
                + $"{maxRequestTimeoutSeconds} so the call, including its concurrency-slot wait, finishes "
                + "inside the Temporal activity attempt that retries it. Load slow local models before "
                + "use instead of raising the request timeout.");
        }

        if (options.MaxConcurrentRequests < 1)
        {
            failures.Add($"{section}:MaxConcurrentRequests must be positive.");
        }

        return failures;
    }

    private static bool IsProvider(string provider) => IsFoundry(provider) || IsOllama(provider);

    private static bool IsFoundry(string provider) =>
        StringComparer.OrdinalIgnoreCase.Equals(provider, AIProviderNames.Foundry);

    private static bool IsOllama(string provider) =>
        StringComparer.OrdinalIgnoreCase.Equals(provider, AIProviderNames.Ollama);
}

internal static class AIProviderEndpoints
{
    public static Uri NormalizeResponsesEndpoint(string provider, string endpoint)
    {
        var isOllama = StringComparer.OrdinalIgnoreCase.Equals(provider, AIProviderNames.Ollama);
        if (!isOllama
            && !StringComparer.OrdinalIgnoreCase.Equals(provider, AIProviderNames.Foundry))
        {
            throw new ArgumentException("Provider must be Foundry or Ollama.", nameof(provider));
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || (!isOllama && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Query.Length != 0
            || uri.Fragment.Length != 0
            || uri.UserInfo.Length != 0)
        {
            throw new ArgumentException("Endpoint is not a valid Responses API base URL.", nameof(endpoint));
        }

        var suffix = isOllama ? "/v1" : "/openai/v1";
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length != 0 && !string.Equals(path, suffix, StringComparison.Ordinal))
        {
            throw new ArgumentException("Endpoint contains an unsupported path.", nameof(endpoint));
        }

        return new UriBuilder(uri) { Path = suffix + "/" }.Uri;
    }
}
