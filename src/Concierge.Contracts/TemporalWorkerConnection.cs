using Temporalio.Client;
using Temporalio.Common.EnvConfig;

namespace Concierge.Contracts;

/// <summary>Shared Temporal connection settings for the Web client and WorkflowServer worker.</summary>
public static class TemporalWorkerConnection
{
    public const string TaskQueue = ConciergeWorkflowNames.TaskQueue;

    /// <summary>
    /// Loads Temporal connection settings from the standard Temporal environment configuration,
    /// defaulting to <c>localhost:7233</c> when no target host is configured.
    /// </summary>
    public static TemporalClientConnectOptions Resolve()
    {
        var options = ClientEnvConfig.LoadClientConnectOptions();
        if (string.IsNullOrWhiteSpace(options.TargetHost))
        {
            options.TargetHost = "localhost:7233";
        }

        return options;
    }

    /// <summary>
    /// Copies the resolved connection settings (host, namespace, TLS, API key, RPC metadata) onto
    /// options owned by the hosting integration, so the Web client and worker connect identically.
    /// </summary>
    public static void ApplyTo(
        TemporalClientConnectOptions resolved,
        TemporalClientConnectOptions target)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(target);

        target.TargetHost = resolved.TargetHost;
        target.Namespace = resolved.Namespace;
        target.Tls = resolved.Tls;
        target.ApiKey = resolved.ApiKey;
        target.RpcMetadata = resolved.RpcMetadata;
    }
}
