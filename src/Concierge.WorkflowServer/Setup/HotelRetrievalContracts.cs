namespace Concierge.WorkflowServer.Setup;

public sealed record HotelRetrievalRequest(string Query);

public sealed record HotelRetrievalResult(
    HotelRetrievalPath Path,
    IReadOnlyList<HotelRetrievalEvidence> Evidence)
{
    public bool HasEvidence => Evidence.Count > 0;
}

public enum HotelRetrievalPath
{
    NoEvidence,
    ExactFact,
    Hybrid,
}

public sealed record HotelRetrievalEvidence(
    string ChunkId,
    string EvidenceKey,
    string SourceId,
    string SourceTitle,
    string ChunkTitle,
    string? SectionAnchor,
    string Passage,
    string VersionKey,
    string ContentSha256,
    string IngestionStatus,
    string SourceMetadataJson,
    string VersionMetadataJson,
    int? LexicalRank,
    int? VectorRank,
    int FinalRank,
    double? FusedScore);
