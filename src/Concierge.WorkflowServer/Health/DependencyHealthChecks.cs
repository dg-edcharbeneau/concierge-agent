using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;

namespace Concierge.WorkflowServer.Health;

public interface IPostgreSqlReadinessProbe
{
    /// <summary>
    /// Sends a real request to PostgreSQL; throws when the database is unreachable.
    /// </summary>
    Task CheckAsync(CancellationToken cancellationToken);
}

public interface ITemporalReadinessProbe
{
    /// <summary>
    /// Sends a real request to the Temporal frontend; throws when the service is unreachable.
    /// </summary>
    Task CheckAsync(CancellationToken cancellationToken);
}

public sealed class PostgreSqlReadinessProbe(NpgsqlDataSource dataSource)
    : IPostgreSqlReadinessProbe
{
    /// <summary>
    /// Runs <c>SELECT 1</c> with a 3-second command timeout.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT 1;");
        command.CommandTimeout = 3;
        await command.ExecuteScalarAsync(cancellationToken);
    }
}

public sealed class TemporalReadinessProbe(ITemporalClient temporalClient)
    : ITemporalReadinessProbe
{
    /// <summary>
    /// Calls <c>GetSystemInfo</c> with a 3-second timeout and no retries.
    /// </summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await temporalClient.WorkflowService.GetSystemInfoAsync(
            new GetSystemInfoRequest(),
            new RpcOptions
            {
                CancellationToken = cancellationToken,
                Timeout = TimeSpan.FromSeconds(3),
                Retry = false,
            });
    }
}

public sealed class PostgreSqlHealthCheck(
    IPostgreSqlReadinessProbe probe,
    ILogger<PostgreSqlHealthCheck> logger) : IHealthCheck
{
    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await DependencyHealthCheck.RunAsync(
            "PostgreSQL",
            "Verify the concierge database is running and its injected connection is reachable.",
            probe.CheckAsync,
            logger,
            cancellationToken);
}

public sealed class TemporalHealthCheck(
    ITemporalReadinessProbe probe,
    ILogger<TemporalHealthCheck> logger) : IHealthCheck
{
    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await DependencyHealthCheck.RunAsync(
            "Temporal",
            "Verify the Temporal service is running and its injected address and namespace are reachable.",
            probe.CheckAsync,
            logger,
            cancellationToken);
}

internal static class DependencyHealthCheck
{
    /// <summary>
    /// Runs <paramref name="check"/> and maps any exception to an unhealthy result with remediation text.
    /// Exception details are logged by type only so connection strings and secrets never reach the health endpoint.
    /// </summary>
    public static async Task<HealthCheckResult> RunAsync(
        string dependency,
        string remediation,
        Func<CancellationToken, Task> check,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await check(cancellationToken);
            return HealthCheckResult.Healthy($"{dependency} accepted a real service request.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Readiness request to {Dependency} failed after {ElapsedMilliseconds} ms ({FailureType}). {Remediation}",
                dependency,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                exception.GetType().Name,
                remediation);
            return HealthCheckResult.Unhealthy(
                $"{dependency} readiness request failed. {remediation}");
        }
    }
}
