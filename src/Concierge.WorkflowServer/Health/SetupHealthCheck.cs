using Concierge.WorkflowServer.Setup;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Concierge.WorkflowServer.Health;

public sealed class SetupHealthCheck(SetupReadinessState readiness) : IHealthCheck
{
    /// <summary>
    /// Reports healthy only after the startup setup workflow has completed, because guest questions depend on it.
    /// </summary>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = readiness.Snapshot;
        var result = StringComparer.Ordinal.Equals(snapshot.Status, "ready")
            ? HealthCheckResult.Healthy(
                $"Startup setup completed as Temporal workflow '{snapshot.WorkflowId}'.")
            : HealthCheckResult.Unhealthy(
                snapshot.Status == "failed"
                    ? $"Startup setup failed; guest Q&A is unavailable. {snapshot.Failure}"
                    : $"Startup setup is {snapshot.Status}; guest Q&A is unavailable.");
        return Task.FromResult(result);
    }
}
