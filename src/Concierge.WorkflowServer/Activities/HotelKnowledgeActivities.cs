using System.Text.RegularExpressions;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Setup;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Npgsql;
using Temporalio.Activities;

namespace Concierge.WorkflowServer.Activities;

public sealed class HotelKnowledgeActivities(
    NpgsqlDataSource dataSource,
    EmbeddingModelIdentityStore identityStore,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IOptions<EmbeddingProviderOptions> embeddingOptions)
{
    private const int CandidateLimit = 50;
    public const int PassageLimit = 5;
    private const int ReciprocalRankConstant = 60;

    /// <summary>
    /// Finds hotel evidence for a guest question. An unambiguous exact fact match wins; otherwise lexical and vector
    /// results are fused. Returns <see cref="HotelRetrievalPath.NoEvidence"/> when nothing is indexed or
    /// matched, and throws if the configured embedding model differs from the indexed one.
    /// </summary>
    [Activity(nameof(RetrieveAsync))]
    public async Task<HotelRetrievalResult> RetrieveAsync(HotelRetrievalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        var cancellationToken = ActivityExecutionContext.Current.CancellationToken;
        var exactFacts = await RetrieveExactFactsAsync(request.Query, cancellationToken);
        if (exactFacts.Count > 0)
        {
            return new HotelRetrievalResult(HotelRetrievalPath.ExactFact, exactFacts);
        }

        if (!await HasCurrentEmbeddedChunksAsync(cancellationToken))
        {
            return new HotelRetrievalResult(HotelRetrievalPath.NoEvidence, []);
        }

        var configuredOptions = embeddingOptions.Value;
        var configuredIdentity = new EmbeddingModelIdentity(
            configuredOptions.Provider,
            configuredOptions.Model,
            configuredOptions.Dimensions);
        await identityStore.ReadCompatibleAsync(configuredIdentity, cancellationToken);

        var generated = await embeddingGenerator.GenerateAsync(
            [request.Query],
            cancellationToken: cancellationToken);
        var vector = generated.Single().Vector.ToArray();
        if (vector.Length != configuredIdentity.Dimensions
            || vector.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidOperationException(
                $"The configured embedding provider/model must return one finite "
                + $"{configuredIdentity.Dimensions}-dimension vector for retrieval.");
        }

        var lexical = await SearchLexicallyAsync(request.Query, cancellationToken);
        var dense = await SearchByVectorAsync(vector, cancellationToken);
        var fused = Fuse(lexical, dense);
        return fused.Count == 0
            ? new HotelRetrievalResult(HotelRetrievalPath.NoEvidence, [])
            : new HotelRetrievalResult(HotelRetrievalPath.Hybrid, fused);
    }

    private async Task<IReadOnlyList<HotelRetrievalEvidence>> RetrieveExactFactsAsync(
        string query,
        CancellationToken cancellationToken)
    {
        const RegexOptions queryOptions =
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        var withoutLateCheckout = Regex.Replace(query, @"\blate[\s-]+checkout\b", " ", queryOptions);
        var mentionsLateCheckout = withoutLateCheckout != query;
        var mentionsCheckout = Regex.IsMatch(withoutLateCheckout, @"\bcheckout\b", queryOptions);
        // A checkout occurrence outside the qualified phrase requests both sections.
        // Full-text matching alone collapses that request into the late-checkout fact.
        if (mentionsLateCheckout && mentionsCheckout)
        {
            return [];
        }

        var checkoutSection = mentionsLateCheckout ? "late-checkout"
            : mentionsCheckout ? "checkout" : string.Empty;

        await using var command = dataSource.CreateCommand(
            """
            WITH query_terms AS (
                SELECT
                    websearch_to_tsquery('english', $1) AS tsquery,
                    tsvector_to_array(to_tsvector('english', $1)) AS lexemes
            ),
            current_facts AS (
                SELECT
                    f.fact_key,
                    f.source_id,
                    to_tsvector('english', f.fact_key || ' ' || f.fact_value) AS fact_vector,
                    tsvector_to_array(to_tsvector(
                        'english',
                        replace(split_part(f.fact_key, '#', 2), '-', ' '))) AS topic_lexemes
                FROM hotel_facts f
                JOIN ingestion_versions v
                  ON v.source_id = f.source_id
                 AND v.version_key = split_part(f.fact_key, '#', 2)
                 AND v.status = 'completed'
                WHERE NOT (split_part(f.fact_key, '#', 2) = 'late-checkout' AND $3 = 'checkout')
                  AND NOT (split_part(f.fact_key, '#', 2) = 'checkout' AND $3 = 'late-checkout')
                  AND (f.effective_from_utc IS NULL OR f.effective_from_utc <= CURRENT_TIMESTAMP)
                  AND (f.effective_to_utc IS NULL OR f.effective_to_utc > CURRENT_TIMESTAMP)
            ),
            matching_facts AS (
                SELECT
                    f.fact_key,
                    f.source_id,
                    count(*) OVER () AS fact_match_count,
                    ts_rank_cd(f.fact_vector, q.tsquery) AS fact_rank,
                    -- Full-text AND semantics can drop a sibling fact on the same authored
                    -- topic (for example, "gym" location vs. "gym" hours) when the question
                    -- also names a facet only one of them states. Such a match is not exact.
                    (SELECT count(*)
                     FROM current_facts g
                     WHERE g.fact_key <> f.fact_key
                       AND EXISTS (
                           SELECT 1
                           FROM unnest(f.topic_lexemes) AS topic(lexeme)
                           WHERE topic.lexeme = ANY(q.lexemes)
                             AND topic.lexeme = ANY(g.topic_lexemes))) AS topic_sibling_count
                FROM current_facts f
                CROSS JOIN query_terms q
                WHERE f.fact_vector @@ q.tsquery
            ),
            selected_chunks AS (
                SELECT
                    f.fact_key,
                    f.source_id,
                    f.fact_match_count,
                    f.fact_rank,
                    f.topic_sibling_count,
                    s.source_key,
                    s.title AS source_title,
                    s.metadata::text AS source_metadata_json,
                    v.version_key,
                    v.content_sha256,
                    v.status AS ingestion_status,
                    v.metadata::text AS version_metadata_json,
                    c.chunk_id,
                    c.title AS chunk_title,
                    c.section_anchor,
                    c.content,
                    row_number() OVER (
                        PARTITION BY f.fact_key
                        ORDER BY
                            (c.search_vector @@ q.tsquery) DESC,
                            ts_rank_cd(c.search_vector, q.tsquery) DESC,
                            c.ordinal,
                            c.chunk_id) AS chunk_rank
                FROM matching_facts f
                JOIN knowledge_sources s ON s.source_id = f.source_id
                JOIN ingestion_versions v
                  ON v.source_id = f.source_id
                 AND v.version_key = split_part(f.fact_key, '#', 2)
                 AND v.status = 'completed'
                JOIN knowledge_chunks c ON c.ingestion_version_id = v.ingestion_version_id
                CROSS JOIN query_terms q
            )
            SELECT
                chunk_id,
                fact_key,
                source_key,
                source_title,
                chunk_title,
                section_anchor,
                content,
                version_key,
                content_sha256,
                ingestion_status,
                source_metadata_json,
                version_metadata_json,
                fact_match_count,
                topic_sibling_count
            FROM selected_chunks
            WHERE chunk_rank = 1
            ORDER BY fact_rank DESC, fact_key
            LIMIT $2;
            """);
        command.Parameters.AddWithValue(query);
        command.Parameters.AddWithValue(PassageLimit);
        command.Parameters.AddWithValue(checkoutSection);

        var evidence = new List<HotelRetrievalEvidence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // A single section-keyed record is a deterministic lookup. A broad query
            // matching multiple records, or one that shares its topic with another
            // current record, belongs in hybrid retrieval, where both arms can rank
            // the candidate passages instead of treating one match as exact.
            if (reader.GetInt64(12) != 1 || reader.GetInt64(13) != 0)
            {
                return [];
            }

            evidence.Add(new HotelRetrievalEvidence(
                reader.GetGuid(0).ToString(),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetString(10),
                reader.GetString(11),
                null,
                null,
                evidence.Count + 1,
                null));
        }

        return evidence;
    }

    private async Task<bool> HasCurrentEmbeddedChunksAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM knowledge_chunks c
                JOIN ingestion_versions v
                  ON v.ingestion_version_id = c.ingestion_version_id
                 AND v.status = 'completed'
                WHERE c.embedding IS NOT NULL
            );
            """);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    internal async Task<IReadOnlyList<RankedChunk>> SearchLexicallyAsync(
        string query,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            WITH query_terms AS (
                SELECT websearch_to_tsquery('english', $1) AS tsquery
            )
            SELECT
                c.chunk_id,
                c.stable_chunk_key,
                c.title AS chunk_title,
                c.section_anchor,
                c.content,
                s.source_key,
                s.title AS source_title,
                s.metadata::text AS source_metadata_json,
                v.version_key,
                v.content_sha256,
                v.status AS ingestion_status,
                v.metadata::text AS version_metadata_json
            FROM knowledge_chunks c
            JOIN ingestion_versions v
              ON v.ingestion_version_id = c.ingestion_version_id
             AND v.status = 'completed'
            JOIN knowledge_sources s ON s.source_id = v.source_id
            CROSS JOIN query_terms q
            WHERE c.search_vector @@ q.tsquery
            ORDER BY ts_rank_cd(c.search_vector, q.tsquery) DESC, c.chunk_id
            LIMIT $2;
            """);
        command.Parameters.AddWithValue(query);
        command.Parameters.AddWithValue(CandidateLimit);
        return await ReadRankedChunksAsync(command, cancellationToken);
    }

    internal async Task<IReadOnlyList<RankedChunk>> SearchByVectorAsync(
        IReadOnlyList<float> vector,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                c.chunk_id,
                c.stable_chunk_key,
                c.title AS chunk_title,
                c.section_anchor,
                c.content,
                s.source_key,
                s.title AS source_title,
                s.metadata::text AS source_metadata_json,
                v.version_key,
                v.content_sha256,
                v.status AS ingestion_status,
                v.metadata::text AS version_metadata_json
            FROM knowledge_chunks c
            JOIN ingestion_versions v
              ON v.ingestion_version_id = c.ingestion_version_id
             AND v.status = 'completed'
            JOIN knowledge_sources s ON s.source_id = v.source_id
            WHERE c.embedding IS NOT NULL
            ORDER BY c.embedding <=> $1::vector, c.chunk_id
            LIMIT $2;
            """);
        command.Parameters.AddWithValue(ToVectorLiteral(vector));
        command.Parameters.AddWithValue(CandidateLimit);
        return await ReadRankedChunksAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<RankedChunk>> ReadRankedChunksAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var results = new List<RankedChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sectionAnchor = reader.IsDBNull(3) ? null : reader.GetString(3);
            var sourceId = reader.GetString(5);
            var stableChunkKey = reader.GetString(1);
            results.Add(new RankedChunk(
                new HotelRetrievalEvidence(
                    reader.GetGuid(0).ToString(),
                    sectionAnchor ?? $"{sourceId}#{stableChunkKey.Split('#')[0]}",
                    sourceId,
                    reader.GetString(6),
                    reader.GetString(2),
                    sectionAnchor,
                    reader.GetString(4),
                    reader.GetString(8),
                    reader.GetString(9),
                    reader.GetString(10),
                    reader.GetString(7),
                    reader.GetString(11),
                    null,
                    null,
                    0,
                    null),
                results.Count + 1));
        }

        return results;
    }

    private static IReadOnlyList<HotelRetrievalEvidence> Fuse(
        IReadOnlyList<RankedChunk> lexical,
        IReadOnlyList<RankedChunk> dense)
    {
        var ranked = new Dictionary<string, FusionCandidate>(StringComparer.Ordinal);
        AddRanks(lexical, isLexical: true, ranked);
        AddRanks(dense, isLexical: false, ranked);

        return ranked.Values
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Evidence.ChunkId, StringComparer.Ordinal)
            .Take(PassageLimit)
            .Select((candidate, index) => candidate.Evidence with
            {
                LexicalRank = candidate.LexicalRank,
                VectorRank = candidate.VectorRank,
                FinalRank = index + 1,
                FusedScore = candidate.Score,
            })
            .ToArray();
    }

    private static void AddRanks(
        IReadOnlyList<RankedChunk> results,
        bool isLexical,
        IDictionary<string, FusionCandidate> candidates)
    {
        foreach (var result in results)
        {
            if (!candidates.TryGetValue(result.Evidence.ChunkId, out var candidate))
            {
                candidate = new FusionCandidate(result.Evidence);
                candidates.Add(result.Evidence.ChunkId, candidate);
            }

            if (isLexical)
            {
                candidate.LexicalRank = result.Rank;
            }
            else
            {
                candidate.VectorRank = result.Rank;
            }

            candidate.Score += 1d / (ReciprocalRankConstant + result.Rank);
        }
    }

    private static string ToVectorLiteral(IReadOnlyList<float> vector) =>
        "[" + string.Join(",", vector.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))
        + "]";

    internal sealed record RankedChunk(HotelRetrievalEvidence Evidence, int Rank);

    private sealed class FusionCandidate(HotelRetrievalEvidence evidence)
    {
        public HotelRetrievalEvidence Evidence { get; } = evidence;

        public int? LexicalRank { get; set; }

        public int? VectorRank { get; set; }

        public double Score { get; set; }
    }
}
