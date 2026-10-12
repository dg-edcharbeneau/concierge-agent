using Concierge.WorkflowServer.AI;

namespace Concierge.WorkflowServer.Setup;

public sealed class SetupOptions
{
    public const string SectionName = "Setup";

    public string KnowledgeDirectory { get; set; } = "knowledge/hotel-zgy";

    public bool OverwriteExistingSeedRecords { get; set; }

    public List<GuestSeedOptions> Guests { get; set; } =
    [
        new()
        {
            GuestId = "bca99f5d-8e64-5f1b-bc77-a12d9385b104",
            GivenName = "Jordan",
            FamilyName = "Rivera",
        },
        new()
        {
            GuestId = "e9f93b63-8607-58f7-8c5a-296bcbe7ea38",
            GivenName = "Taylor",
            FamilyName = "Morgan",
        },
    ];

    public List<StaySeedOptions> Stays { get; set; } =
    [
        new()
        {
            StayId = "d303e5f0-07a1-5345-9dc9-530983063455",
            ExternalStayKey = "zgy-demo-rm-412",
            GuestId = "bca99f5d-8e64-5f1b-bc77-a12d9385b104",
            RoomNumber = "412",
        },
        new()
        {
            StayId = "580bb96e-5b30-5f3f-a5a1-1c90b916bf47",
            ExternalStayKey = "zgy-demo-rm-0812",
            GuestId = "e9f93b63-8607-58f7-8c5a-296bcbe7ea38",
            RoomNumber = "812",
        },
    ];
}

public sealed class GuestSeedOptions
{
    public string GuestId { get; set; } = string.Empty;

    public string GivenName { get; set; } = string.Empty;

    public string FamilyName { get; set; } = string.Empty;
}

public sealed class StaySeedOptions
{
    public string StayId { get; set; } = string.Empty;

    public string ExternalStayKey { get; set; } = string.Empty;

    public string GuestId { get; set; } = string.Empty;

    public string RoomNumber { get; set; } = string.Empty;
}

public sealed record SeedDemoDataRequest(
    bool OverwriteExisting,
    IReadOnlyList<GuestSeedOptions> Guests,
    IReadOnlyList<StaySeedOptions> Stays);

public sealed record SeedDemoDataResult(int GuestsInserted, int StaysInserted, bool OverwriteExisting);

public sealed record SetupWorkflowRequest(
    string StartupGeneration,
    SeedDemoDataRequest SeedData,
    EmbeddingModelIdentity EmbeddingIdentity);

public sealed record KnowledgeDocument(
    string SourceId,
    string Title,
    string RelativePath,
    IReadOnlyList<KnowledgeSection> Sections);

public sealed record KnowledgeSection(
    string SectionId,
    string Title,
    string Content,
    string ContentSha256);

public sealed record KnowledgeChunk(
    string ChunkId,
    string StableChunkKey,
    int Ordinal,
    string Title,
    string? SectionAnchor,
    string Content,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record SectionChunkingRequest(
    string SourceId,
    string SourceTitle,
    KnowledgeSection Section);

public sealed record SectionIngestionRequest(
    string SourceId,
    string SourceTitle,
    string RelativePath,
    KnowledgeSection Section,
    IReadOnlyList<KnowledgeChunk> Chunks,
    IReadOnlyList<float[]> Embeddings,
    EmbeddingModelIdentity EmbeddingIdentity);

public sealed record EmbeddingBatchRequest(
    IReadOnlyList<string> Inputs,
    EmbeddingModelIdentity Identity);

public sealed record SetupOutcome(
    string WorkflowId,
    int GuestCount,
    int StayCount,
    int SourceCount,
    int SectionCount,
    int ChunkCount,
    int EmbeddedSectionCount,
    int RemovedSourceCount,
    string EmbeddingProvider,
    string EmbeddingModel,
    int EmbeddingDimensions,
    DateTimeOffset CompletedAtUtc,
    string StartupGeneration);

public static class SetupWorkflowIdentity
{
    public const string WorkflowId = "concierge-hotel-zgy-setup-v1";

    public const string WorkflowType = "Concierge.HotelSetup";
}
