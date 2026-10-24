using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.Setup;
using Temporalio.Common;
using Temporalio.Workflows;

namespace Concierge.WorkflowServer.Workflows;

[Workflow(SetupWorkflowIdentity.WorkflowType)]
public sealed class HotelSetupWorkflow
{
    // AI:Embeddings:IngestionRequestTimeoutSeconds is validated to finish inside this allowance.
    internal static readonly TimeSpan SetupActivityTimeout = TimeSpan.FromMinutes(5);

    // Temporal owns ingestion embedding retries; the embedding SDK client does not retry.
    private static readonly ActivityOptions SetupActivityOptions = new()
    {
        StartToCloseTimeout = SetupActivityTimeout,
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            MaximumInterval = TimeSpan.FromSeconds(30),
            BackoffCoefficient = 2,
            MaximumAttempts = 10,
        },
    };

    /// <summary>
    /// Seeds demo stays and reconciles the knowledge index with the Markdown sources, re-embedding only
    /// changed sections unless the embedding model changed.
    /// </summary>
    [WorkflowRun]
    public async Task<SetupOutcome> RunAsync(SetupWorkflowRequest request)
    {
        var seed = await Workflow.ExecuteActivityAsync(
            (SetupActivities activities) => activities.SeedFictionalStaysAsync(request.SeedData),
            SetupActivityOptions);

        // Filesystem access and parsing stay in an Activity; this workflow only coordinates results.
        var documents = await Workflow.ExecuteActivityAsync(
            (SetupActivities activities) => activities.DiscoverMarkdown(),
            SetupActivityOptions);
        if (documents.Count == 0)
        {
            throw new InvalidOperationException("Hotel ZGY setup found no Markdown knowledge sources.");
        }

        var identityMatches = await Workflow.ExecuteActivityAsync(
            (SetupActivities activities) =>
                activities.PrepareEmbeddingIndexAsync(request.EmbeddingIdentity),
            SetupActivityOptions);

        var sourceIds = documents.Select(document => document.SourceId).ToArray();
        var removedSources = await Workflow.ExecuteActivityAsync(
            (SetupActivities activities) => activities.RemoveMissingSourcesAsync(sourceIds),
            SetupActivityOptions);

        var chunkCount = 0;
        var sectionCount = 0;
        var embeddedSections = 0;
        foreach (var document in documents)
        {
            await Workflow.ExecuteActivityAsync(
                (SetupActivities activities) => activities.RegisterSourceAsync(document),
                SetupActivityOptions);
            await Workflow.ExecuteActivityAsync(
                (SetupActivities activities) => activities.RemoveMissingSectionsAsync(document),
                SetupActivityOptions);

            foreach (var section in document.Sections)
            {
                var chunkingRequest = new SectionChunkingRequest(
                    document.SourceId,
                    document.Title,
                    section);
                var chunks = await Workflow.ExecuteActivityAsync(
                    (SetupActivities activities) => activities.ChunkSection(chunkingRequest),
                    SetupActivityOptions);
                chunkCount += chunks.Count;
                sectionCount++;
                var ingestionRequest = new SectionIngestionRequest(
                    document.SourceId,
                    document.Title,
                    document.RelativePath,
                    section,
                    chunks,
                    [],
                    request.EmbeddingIdentity);
                var needsEmbedding = !identityMatches
                    || await Workflow.ExecuteActivityAsync(
                        (SetupActivities activities) =>
                            activities.SectionNeedsEmbeddingAsync(ingestionRequest),
                        SetupActivityOptions);
                if (!needsEmbedding)
                {
                    await Workflow.ExecuteActivityAsync(
                        (SetupActivities activities) =>
                            activities.RefreshFactProvenanceAsync(ingestionRequest),
                        SetupActivityOptions);
                    continue;
                }

                var inputs = chunks
                    .Select(chunk => $"{document.Title}\n{section.Title}\n{chunk.Content}")
                    .ToArray();
                var embeddings = await Workflow.ExecuteActivityAsync(
                    (SetupActivities activities) => activities.GenerateEmbeddingsAsync(
                        new EmbeddingBatchRequest(inputs, request.EmbeddingIdentity)),
                    SetupActivityOptions);
                var upsertRequest = ingestionRequest with { Embeddings = embeddings };
                await Workflow.ExecuteActivityAsync(
                    (SetupActivities activities) => activities.UpsertKnowledgeSectionAsync(
                        upsertRequest),
                    SetupActivityOptions);
                embeddedSections++;
            }
        }

        await Workflow.ExecuteActivityAsync(
            (SetupActivities activities) => activities.CompleteEmbeddingIndexAsync(
                request.EmbeddingIdentity),
            SetupActivityOptions);

        return new SetupOutcome(
            SetupWorkflowIdentity.WorkflowId,
            seed.GuestsInserted,
            seed.StaysInserted,
            documents.Count,
            sectionCount,
            chunkCount,
            embeddedSections,
            removedSources,
            request.EmbeddingIdentity.Provider,
            request.EmbeddingIdentity.Model,
            request.EmbeddingIdentity.Dimensions,
            Workflow.UtcNow,
            request.StartupGeneration);
    }
}
