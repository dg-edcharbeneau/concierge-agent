using Concierge.WorkflowServer;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Health;
using Concierge.WorkflowServer.Setup;
using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Npgsql;
using TemporalCommunity.Extensions.AI;
using Temporalio.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddConciergeAIProviders(builder.Configuration);

var conciergeConnectionString = builder.Configuration.GetConnectionString("concierge")
    ?? throw new InvalidOperationException(
        "Connection string 'concierge' is required. Configure it through Aspire or secure deployment configuration.");
builder.Services.AddSingleton(_ =>
{
    var dataSourceBuilder = new NpgsqlDataSourceBuilder(conciergeConnectionString);
    return dataSourceBuilder.Build();
});
builder.Services.AddSingleton<DatabaseMigrator>();
builder.Services.AddSingleton<EmbeddingModelIdentityStore>();
builder.Services.AddOptions<SetupOptions>()
    .Bind(builder.Configuration.GetSection(SetupOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<ConciergePersistenceOptions>,
    ConciergePersistenceOptionsValidator>();
builder.Services.AddOptions<ConciergePersistenceOptions>()
    .Bind(builder.Configuration.GetSection(ConciergePersistenceOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<ConciergeSessionService>();
// Malformed request bodies return a bare 400, never the developer exception page.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddSingleton<KnowledgeMarkdownCatalog>();
builder.Services.AddSingleton<SetupReadinessState>();

var connectOptions = TemporalWorkerConnection.Resolve();
builder.Services.AddTemporalClient(options =>
{
    TemporalWorkerConnection.ApplyTo(connectOptions, options);
});

builder.Services.AddHostedTemporalWorker(TemporalWorkerConnection.TaskQueue)
    .AddDurableAI(options => options.RegisterDefaultWorkflow = false)
    .AddWorkflow<ConciergeStayWorkflow>()
    .AddWorkflow<HotelSetupWorkflow>()
    .AddScopedActivities<ConciergeTranscriptActivities>()
    .AddScopedActivities<HotelKnowledgeActivities>()
    // Session activities run as Standalone Activities started directly by Web.
    .AddScopedActivities<ConciergeSessionActivities>()
    .AddScopedActivities<SetupActivities>();
builder.Services.AddHostedService<SetupWorkflowStarter>();

builder.Services.AddSingleton<IPostgreSqlReadinessProbe, PostgreSqlReadinessProbe>();
builder.Services.AddSingleton<ITemporalReadinessProbe, TemporalReadinessProbe>();
builder.Services.AddHealthChecks()
    .AddCheck<PostgreSqlHealthCheck>(
        "postgresql",
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(5))
    .AddCheck<TemporalHealthCheck>(
        "temporal",
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(5))
    .AddCheck<SetupHealthCheck>(
        "setup",
        tags: ["ready"]);

var app = builder.Build();

// Validate provider identity and security-sensitive configuration before changing schema
// or starting Temporal workers. Client construction remains lazy and makes no model request.
_ = app.Services.GetRequiredService<IOptions<ChatProviderOptions>>().Value;
_ = app.Services
    .GetRequiredService<IOptions<EmbeddingProviderOptions>>()
    .Value;

// Schema ownership stays in this process and completes before Temporal workers start.
await app.Services.GetRequiredService<DatabaseMigrator>()
    .MigrateAsync(app.Lifetime.ApplicationStopping);

app.MapDefaultEndpoints();
await app.RunAsync();
