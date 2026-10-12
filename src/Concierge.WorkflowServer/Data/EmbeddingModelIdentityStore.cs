using Concierge.WorkflowServer.AI;
using Npgsql;

namespace Concierge.WorkflowServer.Data;

public sealed class EmbeddingModelIdentityStore(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Returns the identity of the stored vectors. Throws when setup has not completed or the stored vectors
    /// came from a different embedding model, so search never compares incompatible vectors.
    /// </summary>
    public async Task<EmbeddingModelIdentity> ReadCompatibleAsync(
        EmbeddingModelIdentity configuredIdentity,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var persistedIdentity = await ReadAsync(connection, null, cancellationToken)
            ?? throw new InvalidOperationException(
                "The persisted embedding model identity is missing. Run the startup setup workflow "
                + "before vector search or ingestion.");

        configuredIdentity.EnsureCompatibleWith(persistedIdentity);
        return persistedIdentity;
    }

    /// <summary>
    /// Prepares the vector index before setup ingests knowledge. When the stored vectors came from a
    /// different model (or an unfinished run), clears them and disables search until
    /// <see cref="CompleteIngestionAsync"/>. Returns <see langword="true"/> when the stored vectors match
    /// <paramref name="configuredIdentity"/> and can be reused.
    /// </summary>
    public async Task<bool> PrepareForIngestionAsync(
        EmbeddingModelIdentity configuredIdentity,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Serialize with other setup runs and CompleteIngestionAsync so the identity and vectors
        // are read and changed as one unit.
        await LockIndexAsync(connection, transaction, cancellationToken);

        var persistedIdentity = await ReadAsync(connection, transaction, cancellationToken);
        var hasEmbeddings = await HasEmbeddingsAsync(connection, transaction, cancellationToken);
        // Stored vectors are reusable only when a completed run recorded the configured model.
        // A missing identity means either a fresh index or an earlier run that never completed.
        var identityMatches = persistedIdentity is not null
            && configuredIdentity.IsCompatibleWith(persistedIdentity);

        if (!identityMatches && (persistedIdentity is not null || hasEmbeddings))
        {
            // The model changed, or a previous run stopped partway. Clear every vector so search
            // never mixes models, and remove the identity so ReadCompatibleAsync refuses search
            // until CompleteIngestionAsync records the new identity after re-embedding.
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE knowledge_chunks SET embedding = NULL WHERE embedding IS NOT NULL;",
                cancellationToken);
            if (persistedIdentity is not null)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    "DELETE FROM knowledge_embedding_identity WHERE identity_id = 1;",
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        // The setup workflow uses this to skip re-embedding unchanged sections.
        return identityMatches;
    }

    /// <summary>
    /// Records <paramref name="configuredIdentity"/> as the index identity once every chunk has a vector,
    /// which makes the index searchable again. Throws if any chunk lacks a vector or another model was recorded.
    /// </summary>
    public async Task CompleteIngestionAsync(
        EmbeddingModelIdentity configuredIdentity,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockIndexAsync(connection, transaction, cancellationToken);

        var missingEmbeddings = await CountMissingEmbeddingsAsync(
            connection,
            transaction,
            cancellationToken);
        if (missingEmbeddings > 0)
        {
            throw new InvalidOperationException(
                $"Cannot mark Hotel ZGY setup ready: {missingEmbeddings} knowledge chunks do not have embeddings.");
        }

        var persistedIdentity = await ReadAsync(connection, transaction, cancellationToken);
        if (persistedIdentity is not null && !configuredIdentity.IsCompatibleWith(persistedIdentity))
        {
            throw new InvalidOperationException(
                "The embedding identity changed during setup. Retry the setup workflow before enabling Q&A.");
        }

        if (persistedIdentity is null)
        {
            await UpsertAsync(connection, transaction, configuredIdentity, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<EmbeddingModelIdentity?> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT provider, model, dimensions
            FROM knowledge_embedding_identity
            WHERE identity_id = 1;
            """,
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EmbeddingModelIdentity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2));
    }

    private static async Task<bool> HasEmbeddingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM knowledge_chunks WHERE embedding IS NOT NULL);",
            connection,
            transaction);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> CountMissingEmbeddingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)
            FROM knowledge_chunks
            WHERE embedding IS NULL;
            """,
            connection,
            transaction);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task LockIndexAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            "LOCK TABLE knowledge_embedding_identity IN SHARE ROW EXCLUSIVE MODE;",
            cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "LOCK TABLE knowledge_chunks IN SHARE ROW EXCLUSIVE MODE;",
            cancellationToken);
    }

    private static async Task UpsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        EmbeddingModelIdentity identity,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO knowledge_embedding_identity (identity_id, provider, model, dimensions)
            VALUES (1, $1, $2, $3)
            ON CONFLICT (identity_id) DO UPDATE
            SET provider = EXCLUDED.provider,
                model = EXCLUDED.model,
                dimensions = EXCLUDED.dimensions,
                updated_at_utc = CURRENT_TIMESTAMP;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(identity.Provider);
        command.Parameters.AddWithValue(identity.Model);
        command.Parameters.AddWithValue(identity.Dimensions);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
