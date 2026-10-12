using System.Reflection;
using Npgsql;

namespace Concierge.WorkflowServer.Data;

public sealed class DatabaseMigrator(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseMigrator> logger)
{
    internal const string SchemaResourceName = "Concierge.WorkflowServer.Data.schema.sql";
    private const long SchemaLockId = 589_704_493_804_583_889;

    // Keep this version for changes safely repeatable by schema.sql. Bump it only when a
    // schema change cannot be applied by rerunning that script; older databases then fail
    // with reset guidance instead of receiving an unsafe automatic change.
    internal const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Applies the embedded, repeatable <c>schema.sql</c> under an advisory lock. Throws with reset guidance
    /// when the database has an unknown schema version or unversioned application tables.
    /// </summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            $"SELECT pg_advisory_xact_lock({SchemaLockId});",
            cancellationToken);

        var storedVersion = await ReadStoredVersionAsync(connection, transaction, cancellationToken);
        if (storedVersion is not null && storedVersion != CurrentSchemaVersion)
        {
            throw ResetRequired(
                $"The database schema is version {storedVersion}, but this application requires "
                + $"version {CurrentSchemaVersion}.");
        }

        if (storedVersion is null)
        {
            if (await HasExistingApplicationSchemaAsync(connection, transaction, cancellationToken))
            {
                throw ResetRequired(
                    "Application tables exist without a schema version, so the schema cannot be "
                    + "safely identified.");
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                CREATE TABLE concierge_schema_version (
                    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                    version integer NOT NULL
                );
                """,
                cancellationToken);
        }

        logger.LogInformation(
            "Applying repeatable application database schema version {SchemaVersion}.",
            CurrentSchemaVersion);
        await ExecuteAsync(
            connection,
            transaction,
            await ReadSchemaAsync(cancellationToken),
            cancellationToken);

        if (storedVersion is null)
        {
            await using var recordVersion = new NpgsqlCommand(
                """
                INSERT INTO concierge_schema_version (singleton, version)
                VALUES (true, $1);
                """,
                connection,
                transaction);
            recordVersion.Parameters.AddWithValue(CurrentSchemaVersion);
            await recordVersion.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int?> ReadStoredVersionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(
                connection,
                transaction,
                "concierge_schema_version",
                cancellationToken))
        {
            return null;
        }

        await using var command = new NpgsqlCommand(
            "SELECT version FROM concierge_schema_version WHERE singleton = true;",
            connection,
            transaction);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not int version)
        {
            throw ResetRequired(
                "The schema-version guard is missing or malformed; its version could not be read.");
        }

        return version;
    }

    private static async Task<bool> HasExistingApplicationSchemaAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT to_regclass('public.guests') IS NOT NULL
                OR to_regclass('public.stays') IS NOT NULL
                OR to_regclass('public.knowledge_sources') IS NOT NULL
                OR to_regclass('public.knowledge_chunks') IS NOT NULL
                OR to_regclass('public.concierge_schema_migrations') IS NOT NULL;
            """,
            connection,
            transaction);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return schema-detection results."));
    }

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass($1) IS NOT NULL;",
            connection,
            transaction);
        command.Parameters.AddWithValue($"public.{tableName}");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return schema-detection results."));
    }

    private static async Task<string> ReadSchemaAsync(CancellationToken cancellationToken)
    {
        var assembly = typeof(DatabaseMigrator).Assembly;
        await using var stream = assembly.GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded database schema resource '{SchemaResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static InvalidOperationException ResetRequired(string reason) =>
        new(
            $"{reason} No changes were committed. Back up any data you need, then reset the "
            + "local database before starting this demo.");

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = 30,
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
