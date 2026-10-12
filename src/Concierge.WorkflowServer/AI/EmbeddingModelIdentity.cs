namespace Concierge.WorkflowServer.AI;

/// <summary>
/// Identifies the embedding provider, model, and vector size that produced the stored knowledge vectors.
/// </summary>
public sealed record EmbeddingModelIdentity(string Provider, string Model, int Dimensions)
{
    public const int MigratedSchemaDimensions = 768;

    /// <summary>
    /// Returns whether vectors from <paramref name="other"/> can be compared with vectors from this identity.
    /// Provider names are case-insensitive; model names and dimensions must match exactly.
    /// </summary>
    public bool IsCompatibleWith(EmbeddingModelIdentity other) =>
        StringComparer.OrdinalIgnoreCase.Equals(Provider, other.Provider)
        && StringComparer.Ordinal.Equals(Model, other.Model)
        && Dimensions == other.Dimensions;

    /// <summary>
    /// Throws when <paramref name="indexedIdentity"/> is not compatible, so callers never search or write
    /// vectors produced by a different embedding model.
    /// </summary>
    /// <exception cref="InvalidOperationException">The identities are not compatible.</exception>
    public void EnsureCompatibleWith(EmbeddingModelIdentity indexedIdentity)
    {
        if (!IsCompatibleWith(indexedIdentity))
        {
            throw new InvalidOperationException(
                "The configured embedding provider/model/dimensions do not match the indexed vector identity. "
                + "Refusing vector search, startup, and ingestion. Clear the indexed embeddings and explicitly "
                + "re-ingest the knowledge index before switching models.");
        }
    }
}
