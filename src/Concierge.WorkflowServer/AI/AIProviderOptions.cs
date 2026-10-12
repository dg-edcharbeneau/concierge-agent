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

    public int RequestTimeoutSeconds { get; set; } = 60;

    public int MaxConcurrentRequests { get; set; } = 4;
}

public sealed class ChatProviderOptions : AIProviderOptions
{
    public const string SectionName = "AI:Chat";

    /// <summary>
    /// Model context window in tokens. Sent to Ollama as <c>num_ctx</c>; for Foundry it
    /// documents the deployment's window. Must cover the worst-case prompt budget.
    /// </summary>
    public int ContextWindowTokens { get; set; } = 32_768;
}

public sealed class EmbeddingProviderOptions : AIProviderOptions
{
    public const string SectionName = "AI:Embeddings";

    public int Dimensions { get; set; }
}

internal sealed class AIProviderOptionsValidator :
    IValidateOptions<ChatProviderOptions>,
    IValidateOptions<EmbeddingProviderOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, ChatProviderOptions options)
    {
        var failures = ValidateCommon(ChatProviderOptions.SectionName, options);
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
        var failures = ValidateCommon(EmbeddingProviderOptions.SectionName, options);
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

    private static List<string> ValidateCommon(string section, AIProviderOptions options)
    {
        var failures = new List<string>();
        if (!IsProvider(options.Provider))
        {
            failures.Add(
                $"{section}:Provider must be '{AIProviderNames.Foundry}' or '{AIProviderNames.Ollama}'.");
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
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
