using Concierge.AppHost;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Data;
using Concierge.WorkflowServer.Health;
using DotNet.Testcontainers.Builders;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Concierge.Core.Tests;

public sealed class DatabaseSchemaScriptTests
{
    private const string SchemaResourceName = "Concierge.WorkflowServer.Data.schema.sql";

    [Fact]
    public void SingleSchemaScriptIsEmbeddedAndDocumentsSafeAdditions()
    {
        using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(
            SchemaResourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        Assert.Contains("Repeatable schema additions only", sql, StringComparison.Ordinal);
        Assert.Contains("NOT NULL columns must include a default or be", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS guests", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS embedding vector(768)", sql, StringComparison.Ordinal);
        Assert.Contains(
            "ADD COLUMN IF NOT EXISTS interrupted boolean NOT NULL DEFAULT FALSE",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS concierge_sessions", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaDefinesRequiredTablesAndPostgreSqlIndexes()
    {
        var sql = ReadSchema();

        foreach (var table in new[]
        {
            "guests",
            "stays",
            "hotel_facts",
            "knowledge_sources",
            "knowledge_chunks",
            "ingestion_versions",
            "transcript_turns",
        })
        {
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {table}", sql, StringComparison.Ordinal);
        }

        Assert.Contains("timestamptz", sql, StringComparison.Ordinal);
        Assert.Contains("USING gin (search_vector)", sql, StringComparison.Ordinal);
        Assert.Contains("ix_stays_room_eligibility", sql, StringComparison.Ordinal);
        Assert.Contains("ix_transcript_turns_stay_order", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS vector", sql, StringComparison.Ordinal);
        Assert.Contains("embedding vector(768)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("hnsw", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ivfflat", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmbeddingSchemaUsesProvenDimensionIdentityAndExactSearchOnly()
    {
        var sql = ReadSchema();

        Assert.Contains("embedding vector(768)", sql, StringComparison.Ordinal);
        Assert.Contains("knowledge_embedding_identity", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("embeddinggemma", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "INSERT INTO knowledge_embedding_identity",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-ingest", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("hnsw", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ivfflat", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConciergeSchemaProtectsRoomOccupancyAndStoresOnlyCredentialHashes()
    {
        var sql = ReadSchema();

        Assert.Contains("EXCLUDE USING gist", sql, StringComparison.Ordinal);
        Assert.Contains("room_number WITH =", sql, StringComparison.Ordinal);
        Assert.Contains("credential_sha256 bytea", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("credential text", sql, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadSchema()
    {
        using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(
            SchemaResourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed class DatabaseSchemaTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    private const int ExpectedSchemaVersion = 1;

    private static readonly string[] ExpectedTables =
    [
        "concierge_schema_version",
        "concierge_sessions",
        "guests",
        "hotel_facts",
        "ingestion_versions",
        "knowledge_chunks",
        "knowledge_embedding_identity",
        "knowledge_sources",
        "stays",
        "transcript_turns",
    ];

    [Fact]
    public async Task FreshDatabaseCreatesExpectedSchemaAndVersionGuard()
    {
        await using var database = await postgreSql.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);

        await CreateMigrator(dataSource).MigrateAsync();

        Assert.Equal(ExpectedTables, await ReadTablesAsync(dataSource));
        Assert.Equal("vector", await ExecuteScalarAsync<string>(
            dataSource,
            "SELECT extname FROM pg_extension WHERE extname = 'vector';"));
        Assert.Equal(1, await ExecuteScalarAsync<long>(
            dataSource,
            """
            SELECT count(*)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND udt_name = 'vector';
            """));
        Assert.Equal(0, await ExecuteScalarAsync<long>(
            dataSource,
            """
            SELECT count(*)
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND (
                  lower(indexdef) LIKE '%using hnsw%'
                  OR lower(indexdef) LIKE '%using ivfflat%'
              );
            """));

        Assert.Equal(
            ExpectedSchemaVersion,
            await ExecuteScalarAsync<int>(
                dataSource,
                "SELECT version FROM concierge_schema_version WHERE singleton = true;"));
    }

    [Fact]
    public async Task EmbeddingSchemaHasFixedDimensionMetadataAndRefusesPopulatedIdentitySwitch()
    {
        await using var database = await postgreSql.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        await CreateMigrator(dataSource).MigrateAsync();

        Assert.Equal("vector(768)", await ExecuteScalarAsync<string>(
            dataSource,
            """
            SELECT format_type(a.atttypid, a.atttypmod)
            FROM pg_attribute a
            WHERE a.attrelid = 'knowledge_chunks'::regclass
              AND a.attname = 'embedding';
            """));

        Assert.Equal(0, await ExecuteScalarAsync<long>(
            dataSource,
            "SELECT count(*) FROM knowledge_embedding_identity;"));

        var dimensionError = await Assert.ThrowsAsync<PostgresException>(
            () => InsertKnowledgeChunkWithEmbeddingAsync(dataSource, 767));
        Assert.Contains("expected 768 dimensions", dimensionError.MessageText, StringComparison.Ordinal);

        await CreateIdentityStore(dataSource).CompleteIngestionAsync(
            new EmbeddingModelIdentity("Ollama", "embeddinggemma:latest", 768));
        await InsertKnowledgeChunkWithEmbeddingAsync(dataSource, 768);

        await using var incompatibleSwitch = dataSource.CreateCommand(
            """
            UPDATE knowledge_embedding_identity
            SET provider = 'Foundry', model = 'different-deployment'
            WHERE identity_id = 1;
            """);
        var error = await Assert.ThrowsAsync<PostgresException>(
            () => incompatibleSwitch.ExecuteNonQueryAsync());
        Assert.Contains("Refusing embedding identity change", error.MessageText, StringComparison.Ordinal);
        Assert.Contains("re-ingest", error.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReapplyingSchemaLeavesVersionAndSchemaStable()
    {
        await using var database = await postgreSql.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var migrator = CreateMigrator(dataSource);

        await migrator.MigrateAsync();
        var versionBefore = await ExecuteScalarAsync<int>(
            dataSource,
            "SELECT version FROM concierge_schema_version WHERE singleton = true;");
        var schemaBefore = await ReadSchemaFingerprintAsync(dataSource);

        await migrator.MigrateAsync();

        Assert.Equal(
            versionBefore,
            await ExecuteScalarAsync<int>(
                dataSource,
                "SELECT version FROM concierge_schema_version WHERE singleton = true;"));
        Assert.Equal(schemaBefore, await ReadSchemaFingerprintAsync(dataSource));
    }

    [Fact]
    public async Task ConcurrentFirstApplicationCreatesSingleVersionGuard()
    {
        await using var database = await postgreSql.CreateDatabaseAsync();
        var dataSources = Enumerable.Range(0, 12)
            .Select(_ => NpgsqlDataSource.Create(database.ConnectionString))
            .ToArray();

        try
        {
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var migrations = dataSources.Select(async dataSource =>
            {
                await start.Task;
                await CreateMigrator(dataSource).MigrateAsync();
            }).ToArray();

            start.SetResult();
            await Task.WhenAll(migrations);

            Assert.Equal(1, await ExecuteScalarAsync<long>(
                dataSources[0],
                "SELECT count(*) FROM concierge_schema_version;"));
            Assert.Equal(ExpectedTables, await ReadTablesAsync(dataSources[0]));
            Assert.Equal(
                ExpectedSchemaVersion,
                await ExecuteScalarAsync<int>(
                    dataSources[0],
                    "SELECT version FROM concierge_schema_version WHERE singleton = true;"));
        }
        finally
        {
            await Task.WhenAll(dataSources.Select(dataSource => dataSource.DisposeAsync().AsTask()));
        }
    }

    [Fact]
    public async Task UnsupportedSchemaVersionIsRejectedWithResetGuidance()
    {
        await using var database = await postgreSql.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);

        await using (var createVersion = dataSource.CreateCommand(
            """
            CREATE TABLE concierge_schema_version (
                singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                version integer NOT NULL
            );
            """))
        {
            await createVersion.ExecuteNonQueryAsync();
        }

        await using (var recordVersion = dataSource.CreateCommand(
            "INSERT INTO concierge_schema_version (singleton, version) VALUES (true, 0);"))
        {
            await recordVersion.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateMigrator(dataSource).MigrateAsync());

        Assert.Contains("schema is version 0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reset the local database", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("guests", await ReadTablesAsync(dataSource));
    }

    [Fact]
    public async Task UnversionedExistingTablesAreRejectedWithResetGuidanceAndNothingCommitted()
    {
        await using var database = await postgreSql.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);

        // Shape of a database created by the old numbered migration runner: application
        // tables plus its history table, but no concierge_schema_version row.
        await using (var createUnversioned = dataSource.CreateCommand(
            """
            CREATE TABLE guests (guest_id uuid PRIMARY KEY);
            CREATE TABLE concierge_schema_migrations (version text PRIMARY KEY);
            INSERT INTO concierge_schema_migrations (version) VALUES ('0001_baseline');
            """))
        {
            await createUnversioned.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateMigrator(dataSource).MigrateAsync());

        Assert.Contains("without a schema version", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No changes were committed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reset the local database", exception.Message, StringComparison.Ordinal);
        var tables = await ReadTablesAsync(dataSource);
        Assert.DoesNotContain("concierge_schema_version", tables);
        Assert.DoesNotContain("stays", tables);
        Assert.DoesNotContain("knowledge_chunks", tables);
    }

    [Fact]
    public async Task PostgreSqlReadinessProbeExecutesAgainstRealPostgreSql()
    {
        await using var database = await postgreSql.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var check = new PostgreSqlHealthCheck(
            new PostgreSqlReadinessProbe(dataSource),
            NullLogger<PostgreSqlHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task PinnedImageProvidesExpectedPostgreSqlAndVectorVersions()
    {
        await using var database = await postgreSql.CreateDatabaseAsync();
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);

        Assert.StartsWith(
            PostgreSqlContainerImage.PostgreSqlMajorVersion + ".",
            await ExecuteScalarAsync<string>(dataSource, "SHOW server_version;"),
            StringComparison.Ordinal);

        await using (var createExtension = dataSource.CreateCommand(
            "CREATE EXTENSION IF NOT EXISTS vector;"))
        {
            await createExtension.ExecuteNonQueryAsync();
        }

        Assert.Equal(
            PostgreSqlContainerImage.VectorExtensionVersion,
            await ExecuteScalarAsync<string>(
                dataSource,
                "SELECT extversion FROM pg_extension WHERE extname = 'vector';"));
    }

    private static DatabaseMigrator CreateMigrator(NpgsqlDataSource dataSource) =>
        new(dataSource, NullLogger<DatabaseMigrator>.Instance);

    private static EmbeddingModelIdentityStore CreateIdentityStore(NpgsqlDataSource dataSource) =>
        new(dataSource);

    private static async Task<string[]> ReadTablesAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = 'public'
              AND table_type = 'BASE TABLE'
            ORDER BY table_name;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return [.. tables];
    }

    private static async Task InsertKnowledgeChunkWithEmbeddingAsync(
        NpgsqlDataSource dataSource,
        int dimensions)
    {
        await using var command = dataSource.CreateCommand(
            """
            WITH source AS (
                INSERT INTO knowledge_sources (source_id, source_key, title, source_uri)
                VALUES (gen_random_uuid(), 'test-source', 'Test source', 'test://source')
                RETURNING source_id
            ),
            version AS (
                INSERT INTO ingestion_versions (
                    ingestion_version_id,
                    source_id,
                    version_key,
                    content_sha256,
                    status,
                    started_at_utc,
                    completed_at_utc)
                SELECT
                    gen_random_uuid(),
                    source_id,
                    'v1',
                    repeat('a', 64),
                    'completed',
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                FROM source
                RETURNING ingestion_version_id
            )
            INSERT INTO knowledge_chunks (
                chunk_id,
                ingestion_version_id,
                stable_chunk_key,
                ordinal,
                title,
                content,
                embedding)
            SELECT
                gen_random_uuid(),
                ingestion_version_id,
                'chunk-1',
                0,
                'Test',
                'Test content',
                array_fill(0.0::real, ARRAY[$1])::vector
            FROM version;
            """);
        command.Parameters.AddWithValue(dimensions);
        await command.ExecuteNonQueryAsync();
    }

    private static Task<string> ReadSchemaFingerprintAsync(NpgsqlDataSource dataSource) =>
        ExecuteScalarAsync<string>(
            dataSource,
            """
            SELECT string_agg(definition, E'\n' ORDER BY definition)
            FROM (
                SELECT 'column:' || table_name || ':' || column_name || ':' || data_type AS definition
                FROM information_schema.columns
                WHERE table_schema = 'public'
                UNION ALL
                SELECT 'index:' || indexname || ':' || indexdef
                FROM pg_indexes
                WHERE schemaname = 'public'
                UNION ALL
                SELECT 'constraint:' || conname || ':' || pg_get_constraintdef(oid)
                FROM pg_constraint
                WHERE connamespace = 'public'::regnamespace
            ) schema_objects;
            """);

    private static async Task<T> ExecuteScalarAsync<T>(
        NpgsqlDataSource dataSource,
        string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        var result = await command.ExecuteScalarAsync();
        return Assert.IsType<T>(result);
    }
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container =
        new PostgreSqlBuilder(PostgreSqlContainerImage.FullName)
        .WithDatabase("concierge_tests")
        .WithUsername("postgres")
        .WithPassword("postgres")
        // The image's init-only server answers on the Unix socket, then restarts; probing TCP waits for
        // the final server so the first CREATE DATABASE is not reset mid-restart.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
            "pg_isready", "--host", "127.0.0.1", "--port", "5432", "--username", "postgres"))
        .Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    public async Task<PostgreSqlTestDatabase> CreateDatabaseAsync()
    {
        var databaseName = $"concierge_{Guid.NewGuid():N}";
        await using var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        await using var command = dataSource.CreateCommand($"CREATE DATABASE {databaseName};");
        await command.ExecuteNonQueryAsync();
        return new PostgreSqlTestDatabase(container.GetConnectionString(), databaseName);
    }
}

public sealed class PostgreSqlTestDatabase(string adminConnectionString, string databaseName)
    : IAsyncDisposable
{
    public string ConnectionString { get; } =
        new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

    public async ValueTask DisposeAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(adminConnectionString);
        await using var command = dataSource.CreateCommand(
            $"DROP DATABASE IF EXISTS {databaseName} WITH (FORCE);");
        await command.ExecuteNonQueryAsync();
    }
}
