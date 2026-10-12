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

var foundryChatApiKey = builder.AddParameter("foundry-chat-api-key", secret: true, value: "");
var foundryEmbeddingsApiKey = builder.AddParameter("foundry-embeddings-api-key", secret: true, value: "");
var deepgramApiKey = builder.AddParameter("deepgram-api-key", secret: true);

var workflowServer = builder.AddProject<Projects.Concierge_WorkflowServer>("workflow-server")
    .WithReference(temporal)
    .WithReference(concierge)
    .WithEnvironment("AI__Chat__ApiKey", foundryChatApiKey)
    .WithEnvironment("AI__Embeddings__ApiKey", foundryEmbeddingsApiKey)
    .WaitFor(temporal)
    .WaitFor(concierge)
    .WithHttpHealthCheck("/health/ready");

var web = builder.AddProject<Projects.Concierge_Web>("web")
    .WithReference(temporal)
    .WithEnvironment("Deepgram__ApiKey", deepgramApiKey)
    .WaitFor(temporal)
    .WaitFor(workflowServer)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

builder.AddDevTunnel("web-tunnel")
    .WithReference(web)
    .WithAnonymousAccess();

builder.Build().Run();
