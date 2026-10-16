using Concierge.AppHost;
using TemporalCommunity.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithImage(PostgreSqlContainerImage.Repository, PostgreSqlContainerImage.Tag)
    // Aspire cannot infer PostgreSQL 18's data root from pgvector's image tag.
    .WithVolume("concierge-postgres-data", "/var/lib/postgresql");

var concierge = postgres.AddDatabase("concierge");

var temporal = builder.AddTemporalDevContainer("temporal", options =>
{
    options.Namespace = "default";
})
.WithDataVolume("temporal-data");

// The string-value overload bypasses configuration, so preserve user-secrets explicitly.
var deepgramApiKey = builder.AddParameter(
    "deepgram-api-key", secret: true, value: builder.Configuration["Parameters:deepgram-api-key"] ?? "");

var workflowServer = builder.AddProject<Projects.Concierge_WorkflowServer>("workflow-server")
    .WithReference(temporal)
    .WithReference(concierge)
    .WaitFor(temporal)
    .WaitFor(concierge)
    .WithHttpHealthCheck("/health/ready");

ConfigureAIProvider(builder, workflowServer, "Chat", "foundry-chat-api-key");
ConfigureAIProvider(builder, workflowServer, "Embeddings", "foundry-embeddings-api-key");

var web = builder.AddProject<Projects.Concierge_Web>("web")
    .WithReference(temporal)
    .WithEnvironment("Deepgram__ApiKey", deepgramApiKey)
    .WaitFor(temporal)
    .WaitFor(workflowServer)
    .WithHttpsEndpoint(name: "think", env: "THINK_PORT")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

var thinkTunnel = builder.AddDevTunnel("think-tunnel")
    .WithReference(web.GetEndpoint("think"), allowAnonymous: true);

web.WithEnvironment(
    "THINK_PUBLIC_URL",
    thinkTunnel.GetEndpoint(web.GetEndpoint("think")));

builder.Build().Run();

// The provider decides which resources exist, so it is read from configuration while the
// model is built instead of being a parameter, whose value is resolved only at run time.
static void ConfigureAIProvider(
    IDistributedApplicationBuilder builder,
    IResourceBuilder<ProjectResource> workflowServer,
    string capability,
    string apiKeyParameterName)
{
    var section = builder.Configuration.GetSection($"AI:{capability}");
    var provider = section["Provider"];
    var isFoundry = string.Equals(provider, "Foundry", StringComparison.OrdinalIgnoreCase);
    if (!isFoundry && !string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"AppHost setting AI:{capability}:Provider is '{provider}', but it must be 'Foundry' or 'Ollama'. "
            + "Set it in src/Concierge.AppHost/appsettings.json (or appsettings.Development.json) or with "
            + $"'aspire secret set \"AI:{capability}:Provider\" \"Foundry\" --apphost src/Concierge.AppHost'.");
    }

    foreach (var key in new[] { "Endpoint", "Model" })
    {
        if (string.IsNullOrWhiteSpace(section[key]))
        {
            var meaning = key == "Endpoint"
                ? "the AI service URL"
                : "the model name (Azure deployment name for Foundry)";
            throw new InvalidOperationException(
                $"AppHost setting AI:{capability}:{key} is required: set {meaning} in "
                + "src/Concierge.AppHost/appsettings.json (or appsettings.Development.json), "
                + $"AppHost user-secrets, or the AI__{capability}__{key} environment variable.");
        }
    }

    // Forward configured values, leaving absent optional settings to the worker's
    // existing options defaults. API keys stay on the secret-parameter path below.
    var forwardedKeys = new List<string>
    {
        "Provider", "Endpoint", "Model", "RequestTimeoutSeconds", "MaxConcurrentRequests",
        capability == "Chat" ? "ContextWindowTokens" : "Dimensions",
    };
    if (capability == "Chat")
    {
        forwardedKeys.Add("ReasoningEffort");
    }

    foreach (var key in forwardedKeys)
    {
        if (section[key] is { } value)
        {
            workflowServer.WithEnvironment($"AI__{capability}__{key}", value);
        }
    }

    if (isFoundry)
    {
        // An empty key makes the workflow server use DefaultAzureCredential, and the
        // explicit value keeps Aspire from prompting for it.
        var apiKey = builder.AddParameter(
            apiKeyParameterName,
            secret: true,
            value: builder.Configuration[$"Parameters:{apiKeyParameterName}"] ?? "");
        workflowServer.WithEnvironment($"AI__{capability}__ApiKey", apiKey);
    }
}
