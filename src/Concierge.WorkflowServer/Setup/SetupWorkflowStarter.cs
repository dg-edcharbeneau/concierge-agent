using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace Concierge.WorkflowServer.Setup;

public sealed class SetupWorkflowStarter(
    ITemporalClient temporalClient,
    IOptions<SetupOptions> setupOptions,
    IOptions<EmbeddingProviderOptions> embeddingOptions,
    SetupReadinessState readiness,
    ILogger<SetupWorkflowStarter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        readiness.MarkRunning();
        var startupGeneration = Guid.NewGuid().ToString("N");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var seedData = new SeedDemoDataRequest(
                    setupOptions.Value.OverwriteExistingSeedRecords,
                    setupOptions.Value.Guests,
                    setupOptions.Value.Stays);
                var embeddingIdentity = new EmbeddingModelIdentity(
                    embeddingOptions.Value.Provider,
                    embeddingOptions.Value.Model,
                    embeddingOptions.Value.Dimensions);
                var request = new SetupWorkflowRequest(
                    startupGeneration,
                    seedData,
                    embeddingIdentity);

                var handle = await temporalClient.StartWorkflowAsync(
                    (HotelSetupWorkflow workflow) => workflow.RunAsync(request),
                    new WorkflowOptions(SetupWorkflowIdentity.WorkflowId, TemporalWorkerConnection.TaskQueue)
                    {
                        IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                        IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                    });

                readiness.MarkRunning();
                logger.LogInformation(
                    "Started or joined startup setup workflow {WorkflowId} on {TaskQueue}.",
                    SetupWorkflowIdentity.WorkflowId,
                    TemporalWorkerConnection.TaskQueue);
                var outcome = await handle.GetResultAsync();
                if (!StringComparer.Ordinal.Equals(
                        outcome.StartupGeneration,
                        request.StartupGeneration))
                {
                    logger.LogInformation(
                        "Joined setup workflow {WorkflowId} completed startup generation {CompletedGeneration}; "
                        + "current generation {CurrentGeneration} still requires reconciliation.",
                        SetupWorkflowIdentity.WorkflowId,
                        outcome.StartupGeneration,
                        request.StartupGeneration);
                    continue;
                }

                readiness.MarkReady();
                logger.LogInformation(
                    "Startup setup workflow {WorkflowId} completed: {SourceCount} Markdown sources, "
                    + "{SectionCount} sections, {ChunkCount} chunks, {EmbeddedSectionCount} sections re-embedded, "
                    + "{RemovedSourceCount} removed sources; embedding {EmbeddingProvider}/{EmbeddingModel} ({Dimensions} dimensions).",
                    outcome.WorkflowId,
                    outcome.SourceCount,
                    outcome.SectionCount,
                    outcome.ChunkCount,
                    outcome.EmbeddedSectionCount,
                    outcome.RemovedSourceCount,
                    outcome.EmbeddingProvider,
                    outcome.EmbeddingModel,
                    outcome.EmbeddingDimensions);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                readiness.MarkFailed(exception);
                logger.LogError(
                    exception,
                    "Startup setup workflow {WorkflowId} failed; readiness remains closed and the starter will retry.",
                    SetupWorkflowIdentity.WorkflowId);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }
}
