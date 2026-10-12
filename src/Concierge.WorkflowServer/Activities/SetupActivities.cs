using System.Globalization;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Setup;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Npgsql;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Concierge.WorkflowServer.Activities;

public sealed class SetupActivities(
    NpgsqlDataSource dataSource,
    KnowledgeMarkdownCatalog catalog,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    EmbeddingModelIdentityStore identityStore,
    IOptions<EmbeddingProviderOptions> embeddingOptions,
    ILogger<SetupActivities> logger)
{
    public const string StayIdentityMismatchErrorType = "StayIdentityMismatch";

    /// <summary>
    /// Inserts the fictional guests and stays, updating existing rows only when
    /// <see cref="SeedDemoDataRequest.OverwriteExisting"/> is set. Fails without retry if a stay ID or
    /// external key would be reassigned.
    /// </summary>
    [Activity(nameof(SeedFictionalStaysAsync))]
    public async Task<SeedDemoDataResult> SeedFictionalStaysAsync(SeedDemoDataRequest request)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        ValidateSeeds(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var guestsInserted = 0;
        var staysInserted = 0;

        foreach (var guest in request.Guests)
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO guests (guest_id, given_name, family_name)
                VALUES ($1, $2, $3)
                ON CONFLICT (guest_id) DO UPDATE
                SET given_name = EXCLUDED.given_name,
                    family_name = EXCLUDED.family_name
                WHERE $4;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(Guid.Parse(guest.GuestId));
            command.Parameters.AddWithValue(guest.GivenName);
            command.Parameters.AddWithValue(guest.FamilyName);
            command.Parameters.AddWithValue(request.OverwriteExisting);
            guestsInserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var stay in request.Stays)
        {
            await EnsureStayIdentityUnchangedAsync(connection, transaction, stay, cancellationToken);
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO stays (
                    stay_id,
                    external_stay_key,
                    guest_id,
                    room_number,
                    check_in_utc,
                    check_out_utc,
                    eligibility_ends_at_utc)
                VALUES (
                    $1,
                    $2,
                    $3,
                    $4,
                    (((CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date - 1)::timestamp
                        AT TIME ZONE 'America/New_York'),
                    (((CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date + 7)::timestamp
                        AT TIME ZONE 'America/New_York'),
                    (((CURRENT_TIMESTAMP AT TIME ZONE 'America/New_York')::date + 8)::timestamp
                        AT TIME ZONE 'America/New_York'))
                ON CONFLICT (external_stay_key) DO UPDATE
                SET guest_id = EXCLUDED.guest_id,
                    room_number = EXCLUDED.room_number,
                    check_in_utc = EXCLUDED.check_in_utc,
                    check_out_utc = EXCLUDED.check_out_utc,
                    eligibility_ends_at_utc = EXCLUDED.eligibility_ends_at_utc
                WHERE $5;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(Guid.Parse(stay.StayId));
            command.Parameters.AddWithValue(stay.ExternalStayKey);
            command.Parameters.AddWithValue(Guid.Parse(stay.GuestId));
            command.Parameters.AddWithValue(stay.RoomNumber);
            command.Parameters.AddWithValue(request.OverwriteExisting);
            staysInserted += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Fictional setup data reconciled: {GuestCount} guest rows, {StayCount} stay rows; overwrite={OverwriteExisting}.",
            guestsInserted,
            staysInserted,
            request.OverwriteExisting);
        return new SeedDemoDataResult(guestsInserted, staysInserted, request.OverwriteExisting);
    }

    /// <summary>
    /// Reads and parses the configured Markdown knowledge directory. Kept in an activity because workflows
    /// must not touch the filesystem.
    /// </summary>
    [Activity(nameof(DiscoverMarkdown))]
    public IReadOnlyList<KnowledgeDocument> DiscoverMarkdown()
    {
        var documents = catalog.DiscoverAndParse();
        logger.LogInformation(
            "Discovered and parsed {SourceCount} Markdown knowledge sources with {SectionCount} sections.",
            documents.Count,
            documents.Sum(document => document.Sections.Count));
        return documents;
    }

    /// <summary>
    /// Splits one section into deterministic, size-bounded chunks.
    /// </summary>
    [Activity(nameof(ChunkSection))]
    public IReadOnlyList<KnowledgeChunk> ChunkSection(SectionChunkingRequest request) =>
        KnowledgeChunker.Chunk(request);

    /// <summary>
    /// Creates or updates the source registry row for a Markdown document.
    /// </summary>
    [Activity(nameof(RegisterSourceAsync))]
    public async Task RegisterSourceAsync(KnowledgeDocument document)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO knowledge_sources (
                source_id,
                source_key,
                title,
                source_uri,
                media_type,
                metadata)
            VALUES ($1, $2, $3, $4, 'text/markdown', $5::jsonb)
            ON CONFLICT (source_key) DO UPDATE
            SET title = EXCLUDED.title,
                source_uri = EXCLUDED.source_uri,
                metadata = EXCLUDED.metadata,
                updated_at_utc = CURRENT_TIMESTAMP;
            """);
        command.Parameters.AddWithValue(KnowledgeChunker.DeterministicId(document.SourceId));
        command.Parameters.AddWithValue(document.SourceId);
        command.Parameters.AddWithValue(document.Title);
        command.Parameters.AddWithValue(document.RelativePath);
        command.Parameters.AddWithValue(JsonSerializer.Serialize(new
        {
            relativePath = document.RelativePath,
        }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes facts and ingestion versions for sections no longer present in the document.
    /// </summary>
    [Activity(nameof(RemoveMissingSectionsAsync))]
    public async Task RemoveMissingSectionsAsync(KnowledgeDocument document)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var sourceId = KnowledgeChunker.DeterministicId(document.SourceId);
        var sectionIds = document.Sections.Select(section => section.SectionId).ToArray();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var removeFacts = new NpgsqlCommand(
            """
            DELETE FROM hotel_facts
            WHERE source_id = $1
              AND split_part(fact_key, '#', 2) <> ALL($2);
            """,
            connection,
            transaction))
        {
            removeFacts.Parameters.AddWithValue(sourceId);
            removeFacts.Parameters.AddWithValue(sectionIds);
            await removeFacts.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var removeVersions = new NpgsqlCommand(
            """
            DELETE FROM ingestion_versions
            WHERE source_id = $1
              AND NOT (version_key = ANY($2));
            """,
            connection,
            transaction))
        {
            removeVersions.Parameters.AddWithValue(sourceId);
            removeVersions.Parameters.AddWithValue(sectionIds);
            await removeVersions.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Prepares the vector index for this setup run; see <see cref="EmbeddingModelIdentityStore.PrepareForIngestionAsync"/>.
    /// Returns <see langword="true"/> when existing vectors can be reused.
    /// </summary>
    [Activity(nameof(PrepareEmbeddingIndexAsync))]
    public Task<bool> PrepareEmbeddingIndexAsync(EmbeddingModelIdentity identity) =>
        identityStore.PrepareForIngestionAsync(identity, ActivityExecutionContext.Current.CancellationToken);

    /// <summary>
    /// Deletes Hotel ZGY sources (and their facts) whose source IDs are not in <paramref name="activeSourceIds"/>.
    /// Returns the number of sources removed.
    /// </summary>
    [Activity(nameof(RemoveMissingSourcesAsync))]
    public async Task<int> RemoveMissingSourcesAsync(IReadOnlyList<string> activeSourceIds)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var active = activeSourceIds.ToHashSet(StringComparer.Ordinal);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var removedCount = 0;

        await using var command = new NpgsqlCommand(
            """
            SELECT source_id, source_key
            FROM knowledge_sources
            WHERE source_key LIKE 'zgy-%'
            ORDER BY source_key;
            """,
            connection,
            transaction);
        var existing = new List<(Guid SourceId, string SourceKey)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add((reader.GetGuid(0), reader.GetString(1)));
            }
        }

        foreach (var source in existing.Where(source => !active.Contains(source.SourceKey)))
        {
            await using (var deleteFacts = new NpgsqlCommand(
                "DELETE FROM hotel_facts WHERE source_id = $1;",
                connection,
                transaction))
            {
                deleteFacts.Parameters.AddWithValue(source.SourceId);
                await deleteFacts.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteSource = new NpgsqlCommand(
                "DELETE FROM knowledge_sources WHERE source_id = $1;",
                connection,
                transaction))
            {
                deleteSource.Parameters.AddWithValue(source.SourceId);
                await deleteSource.ExecuteNonQueryAsync(cancellationToken);
            }

            removedCount++;
            logger.LogWarning(
                "Removed Markdown source {SourceKey} and its source registry record.",
                source.SourceKey);
        }

        await transaction.CommitAsync(cancellationToken);
        return removedCount;
    }

    /// <summary>
    /// Returns <see langword="false"/> only when this exact section content is already fully embedded
    /// with a compatible embedding model, so unchanged sections skip the embedding call.
    /// </summary>
    [Activity(nameof(SectionNeedsEmbeddingAsync))]
    public async Task<bool> SectionNeedsEmbeddingAsync(SectionIngestionRequest request)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var persistedIdentity = await ReadIdentityAsync(cancellationToken);
        if (persistedIdentity is null || !request.EmbeddingIdentity.IsCompatibleWith(persistedIdentity))
        {
            return true;
        }

        var sourceId = KnowledgeChunker.DeterministicId(request.SourceId);
        await using var command = dataSource.CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM ingestion_versions v
                WHERE v.source_id = $1
                  AND v.version_key = $2
                  AND v.content_sha256 = $3
                  AND v.status = 'completed'
                  AND EXISTS (
                      SELECT 1
                      FROM knowledge_chunks c
                      WHERE c.ingestion_version_id = v.ingestion_version_id
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM knowledge_chunks c
                      WHERE c.ingestion_version_id = v.ingestion_version_id
                        AND c.embedding IS NULL
                  )
            );
            """);
        command.Parameters.AddWithValue(sourceId);
        command.Parameters.AddWithValue(request.Section.SectionId);
        command.Parameters.AddWithValue(request.Section.ContentSha256);
        return !(bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Updates the source locator of a section's hotel fact when the section is reused without re-embedding.
    /// </summary>
    [Activity(nameof(RefreshFactProvenanceAsync))]
    public async Task RefreshFactProvenanceAsync(SectionIngestionRequest request)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var sourceId = KnowledgeChunker.DeterministicId(request.SourceId);
        await using var command = dataSource.CreateCommand(
            """
            UPDATE hotel_facts
            SET source_locator = $1,
                updated_at_utc = CURRENT_TIMESTAMP
            WHERE fact_key = $2
              AND source_id = $3;
            """);
        command.Parameters.AddWithValue(
            $"{request.RelativePath}#{request.Section.SectionId}");
        command.Parameters.AddWithValue($"{request.SourceId}#{request.Section.SectionId}");
        command.Parameters.AddWithValue(sourceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Embeds a batch of chunk texts. Throws if the request's identity differs from the configured model or
    /// the provider returns the wrong count, wrong dimension, or non-finite values.
    /// </summary>
    [Activity(nameof(GenerateEmbeddingsAsync))]
    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(EmbeddingBatchRequest request)
    {
        var configured = CurrentEmbeddingIdentity();
        configured.EnsureCompatibleWith(request.Identity);
        if (request.Inputs.Count == 0)
        {
            throw new InvalidOperationException("Embedding generation requires at least one knowledge chunk.");
        }

        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var result = await embeddingGenerator.GenerateAsync(
            request.Inputs,
            cancellationToken: cancellationToken);
        var vectors = result.Select(embedding => embedding.Vector.ToArray()).ToArray();
        if (vectors.Length != request.Inputs.Count
            || vectors.Any(vector => vector.Length != configured.Dimensions
                || vector.Any(value => !float.IsFinite(value))))
        {
            throw new InvalidOperationException(
                $"Embedding provider '{configured.Provider}' model '{configured.Model}' must return one "
                + $"finite {configured.Dimensions}-dimension vector for each of {request.Inputs.Count} chunks.");
        }

        return vectors;
    }

    /// <summary>
    /// Replaces a section's ingestion version, chunks, vectors, and hotel fact in one transaction.
    /// </summary>
    [Activity(nameof(UpsertKnowledgeSectionAsync))]
    public async Task UpsertKnowledgeSectionAsync(SectionIngestionRequest request)
    {
        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var configured = CurrentEmbeddingIdentity();
        configured.EnsureCompatibleWith(request.EmbeddingIdentity);
        if (request.Chunks.Count == 0 || request.Chunks.Count != request.Embeddings.Count)
        {
            throw new InvalidOperationException(
                "Knowledge upsert requires one embedding for each non-empty section chunk.");
        }

        if (request.Embeddings.Any(vector => vector.Length != configured.Dimensions))
        {
            throw new InvalidOperationException(
                $"Knowledge upsert received a vector whose dimension does not match {configured.Dimensions}.");
        }

        var versionUuid = KnowledgeChunker.DeterministicId(
            $"{request.SourceId}#{request.Section.SectionId}#{request.Section.ContentSha256}");
        var factId = KnowledgeChunker.DeterministicId(
            $"{request.SourceId}#{request.Section.SectionId}#hotel-fact");
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid persistedSourceId;
        await using (var readSource = new NpgsqlCommand(
            "SELECT source_id FROM knowledge_sources WHERE source_key = $1;",
            connection,
            transaction))
        {
            readSource.Parameters.AddWithValue(request.SourceId);
            persistedSourceId = (Guid)(await readSource.ExecuteScalarAsync(cancellationToken))!;
        }

        await using (var clearPriorSection = new NpgsqlCommand(
            "DELETE FROM ingestion_versions WHERE source_id = $1 AND version_key = $2;",
            connection,
            transaction))
        {
            clearPriorSection.Parameters.AddWithValue(persistedSourceId);
            clearPriorSection.Parameters.AddWithValue(request.Section.SectionId);
            await clearPriorSection.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var addVersion = new NpgsqlCommand(
            """
            INSERT INTO ingestion_versions (
                ingestion_version_id,
                source_id,
                version_key,
                content_sha256,
                status,
                started_at_utc,
                completed_at_utc,
                metadata)
            VALUES (
                $1,
                $2,
                $3,
                $4,
                'completed',
                CURRENT_TIMESTAMP,
                CURRENT_TIMESTAMP,
                $5::jsonb);
            """,
            connection,
            transaction))
        {
            addVersion.Parameters.AddWithValue(versionUuid);
            addVersion.Parameters.AddWithValue(persistedSourceId);
            addVersion.Parameters.AddWithValue(request.Section.SectionId);
            addVersion.Parameters.AddWithValue(request.Section.ContentSha256);
            addVersion.Parameters.AddWithValue(JsonSerializer.Serialize(new
            {
                sectionId = request.Section.SectionId,
                sectionTitle = request.Section.Title,
                embeddingProvider = configured.Provider,
                embeddingModel = configured.Model,
                embeddingDimensions = configured.Dimensions,
            }));
            await addVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var index = 0; index < request.Chunks.Count; index++)
        {
            var chunk = request.Chunks[index];
            await using var addChunk = new NpgsqlCommand(
                """
                INSERT INTO knowledge_chunks (
                    chunk_id,
                    ingestion_version_id,
                    stable_chunk_key,
                    ordinal,
                    title,
                    section_anchor,
                    content,
                    metadata,
                    embedding)
                VALUES (
                    $1,
                    $2,
                    $3,
                    $4,
                    $5,
                    $6,
                    $7,
                    $8::jsonb,
                    $9::vector);
                """,
                connection,
                transaction);
            addChunk.Parameters.AddWithValue(Guid.Parse(chunk.ChunkId));
            addChunk.Parameters.AddWithValue(versionUuid);
            addChunk.Parameters.AddWithValue(chunk.StableChunkKey);
            addChunk.Parameters.AddWithValue(chunk.Ordinal);
            addChunk.Parameters.AddWithValue(chunk.Title);
            addChunk.Parameters.AddWithValue((object?)chunk.SectionAnchor ?? DBNull.Value);
            addChunk.Parameters.AddWithValue(chunk.Content);
            addChunk.Parameters.AddWithValue(JsonSerializer.Serialize(chunk.Metadata));
            addChunk.Parameters.AddWithValue(ToVectorLiteral(request.Embeddings[index]));
            await addChunk.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var upsertFact = new NpgsqlCommand(
            """
            INSERT INTO hotel_facts (
                fact_id,
                fact_key,
                fact_value,
                source_id,
                source_locator,
                metadata,
                updated_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6::jsonb, CURRENT_TIMESTAMP)
            ON CONFLICT (fact_key) DO UPDATE
            SET fact_value = EXCLUDED.fact_value,
                source_id = EXCLUDED.source_id,
                source_locator = EXCLUDED.source_locator,
                metadata = EXCLUDED.metadata,
                updated_at_utc = CURRENT_TIMESTAMP;
            """,
            connection,
            transaction))
        {
            upsertFact.Parameters.AddWithValue(factId);
            upsertFact.Parameters.AddWithValue(
                $"{request.SourceId}#{request.Section.SectionId}");
            upsertFact.Parameters.AddWithValue(request.Section.Content);
            upsertFact.Parameters.AddWithValue(persistedSourceId);
            upsertFact.Parameters.AddWithValue(
                $"{request.RelativePath}#{request.Section.SectionId}");
            upsertFact.Parameters.AddWithValue(JsonSerializer.Serialize(new
            {
                sourceId = request.SourceId,
                sectionId = request.Section.SectionId,
                evidenceKey = $"{request.SourceId}#{request.Section.SectionId}",
            }));
            await upsertFact.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Marks the vector index searchable; see <see cref="EmbeddingModelIdentityStore.CompleteIngestionAsync"/>.
    /// </summary>
    [Activity(nameof(CompleteEmbeddingIndexAsync))]
    public Task CompleteEmbeddingIndexAsync(EmbeddingModelIdentity identity) =>
        identityStore.CompleteIngestionAsync(identity, ActivityExecutionContext.Current.CancellationToken);

    private EmbeddingModelIdentity CurrentEmbeddingIdentity()
    {
        var options = embeddingOptions.Value;
        return new EmbeddingModelIdentity(options.Provider, options.Model, options.Dimensions);
    }

    private async Task<EmbeddingModelIdentity?> ReadIdentityAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT provider, model, dimensions
            FROM knowledge_embedding_identity
            WHERE identity_id = 1;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new EmbeddingModelIdentity(reader.GetString(0), reader.GetString(1), reader.GetInt32(2))
            : null;
    }

    private static string ToVectorLiteral(float[] vector) =>
        $"[{string.Join(",", vector.Select(value => value.ToString("R", CultureInfo.InvariantCulture)))}]";

    /// <summary>
    /// A persisted stay's (stay_id, external_stay_key) pair is immutable: transcripts reference stay_id,
    /// and seeding with overwrite only updates mutable fields. Any row that matches either half of the
    /// configured pair but not both is an identity change, which fails setup without retries so the
    /// surrounding transaction rolls back and nothing is partially written.
    /// </summary>
    private static async Task EnsureStayIdentityUnchangedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        StaySeedOptions stay,
        CancellationToken cancellationToken)
    {
        var configuredStayId = Guid.Parse(stay.StayId);
        await using var command = new NpgsqlCommand(
            """
            SELECT stay_id, external_stay_key
            FROM stays
            WHERE stay_id = $1 OR external_stay_key = $2
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(configuredStayId);
        command.Parameters.AddWithValue(stay.ExternalStayKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var persistedStayId = reader.GetGuid(0);
            var persistedKey = reader.GetString(1);
            if (persistedStayId == configuredStayId
                && StringComparer.Ordinal.Equals(persistedKey, stay.ExternalStayKey))
            {
                continue;
            }

            throw new ApplicationFailureException(
                $"Fictional stay identity mismatch: configured stay_id '{configuredStayId}' with "
                + $"external_stay_key '{stay.ExternalStayKey}', but the database already pairs "
                + $"stay_id '{persistedStayId}' with external_stay_key '{persistedKey}'. "
                + "Seeding does not support stay identity changes; restore the original Setup stay ID/key "
                + "pairing in configuration.",
                errorType: StayIdentityMismatchErrorType,
                nonRetryable: true);
        }
    }

    private static void ValidateSeeds(SeedDemoDataRequest request)
    {
        if (request.Guests.Count == 0 || request.Stays.Count == 0)
        {
            throw new InvalidOperationException(
                "At least one fictional guest and stay must be configured for startup setup.");
        }

        foreach (var guest in request.Guests)
        {
            _ = Guid.Parse(guest.GuestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(guest.GivenName);
            ArgumentException.ThrowIfNullOrWhiteSpace(guest.FamilyName);
        }

        var guestIds = request.Guests
            .Select(guest => Guid.Parse(guest.GuestId))
            .ToHashSet();
        foreach (var stay in request.Stays)
        {
            _ = Guid.Parse(stay.StayId);
            _ = Guid.Parse(stay.GuestId);
            ArgumentException.ThrowIfNullOrWhiteSpace(stay.ExternalStayKey);
            if (!guestIds.Contains(Guid.Parse(stay.GuestId)))
            {
                throw new InvalidOperationException(
                    $"Fictional stay '{stay.ExternalStayKey}' references a guest that is not configured.");
            }

            if (!RoomNumberFormat.IsValid(stay.RoomNumber))
            {
                throw new InvalidOperationException(
                    $"Fictional stay '{stay.ExternalStayKey}' room number must contain one to four digits "
                    + "and must not start with 0.");
            }
        }
    }
}
