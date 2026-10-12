using Concierge.WorkflowServer.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Testing;

namespace Concierge.Core.Tests;

public sealed class DependencyHealthCheckTests
{
    [Fact]
    public async Task PostgreSqlCheckIsHealthyOnlyAfterProbeSucceeds()
    {
        var check = new PostgreSqlHealthCheck(
            new PostgreSqlProbe(_ => Task.CompletedTask),
            NullLogger<PostgreSqlHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("real service request", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TemporalFailureIsActionableAndDoesNotExposeExceptionMessage()
    {
        const string secretBearingMessage = "Password=do-not-leak";
        var check = new TemporalHealthCheck(
            new TemporalProbe(_ => throw new InvalidOperationException(secretBearingMessage)),
            NullLogger<TemporalHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("Temporal", result.Description, StringComparison.Ordinal);
        Assert.Contains("injected address", result.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBearingMessage, result.Description, StringComparison.Ordinal);
        Assert.Null(result.Exception);
    }

    [Fact]
    public async Task TemporalReadinessProbeExecutesAgainstLocalTemporalService()
    {
        await using var environment = await WorkflowEnvironment.StartLocalAsync();
        var check = new TemporalHealthCheck(
            new TemporalReadinessProbe(environment.Client),
            NullLogger<TemporalHealthCheck>.Instance);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private sealed class PostgreSqlProbe(Func<CancellationToken, Task> check)
        : IPostgreSqlReadinessProbe
    {
        public Task CheckAsync(CancellationToken cancellationToken) => check(cancellationToken);
    }

    private sealed class TemporalProbe(Func<CancellationToken, Task> check)
        : ITemporalReadinessProbe
    {
        public Task CheckAsync(CancellationToken cancellationToken) => check(cancellationToken);
    }
}
