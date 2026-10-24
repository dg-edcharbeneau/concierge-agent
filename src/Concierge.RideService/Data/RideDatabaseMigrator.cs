using System.Reflection;
using Npgsql;

namespace Concierge.RideService.Data;

public sealed class RideDatabaseMigrator(
    NpgsqlDataSource dataSource,
    ILogger<RideDatabaseMigrator> logger)
{
    private const string SchemaResourceName = "Concierge.RideService.Data.schema.sql";
    private const long SchemaLockId = 7_204_118_326_075_119_904;

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand(
            $"SELECT pg_advisory_xact_lock({SchemaLockId});", connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var schemaCommand = new NpgsqlCommand(
            await ReadSchemaAsync(cancellationToken), connection, transaction))
        {
            schemaCommand.CommandTimeout = 30;
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Ride database schema is ready.");
    }

    private static async Task<string> ReadSchemaAsync(CancellationToken cancellationToken)
    {
        await using var stream = typeof(RideDatabaseMigrator).Assembly
            .GetManifestResourceStream(SchemaResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded database schema resource '{SchemaResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }
}
