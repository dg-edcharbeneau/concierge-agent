using System.Diagnostics.Metrics;
using Temporalio.Client;
using Temporalio.Extensions.DiagnosticSource;
using Temporalio.Extensions.OpenTelemetry;
using Temporalio.Runtime;

namespace Concierge.Contracts;

/// <summary>
/// Temporal tracing and SDK metrics shared by the Web client and the WorkflowServer client/worker.
/// Register <see cref="ActivitySourceNames"/> and <see cref="MeterName"/> with OpenTelemetry.
/// </summary>
public static class TemporalTelemetry
{
    /// <summary>Meter that receives the Temporal SDK (Core) metrics, such as request and task latencies.</summary>
    public const string MeterName = "Temporalio";

    /// <summary>Client, workflow, and activity sources emitted by <see cref="TracingInterceptor"/>.</summary>
    public static IReadOnlyList<string> ActivitySourceNames { get; } =
    [
        TracingInterceptor.ClientSource.Name,
        TracingInterceptor.WorkflowsSource.Name,
        TracingInterceptor.ActivitiesSource.Name,
    ];

    // One runtime per process: it owns the Core thread pool and its metrics meter.
    private static readonly Lazy<TemporalRuntime> Runtime = new(() => new TemporalRuntime(new TemporalRuntimeOptions
    {
        Telemetry = new TelemetryOptions
        {
            Metrics = new MetricsOptions { CustomMetricMeter = new CustomMetricMeter(new Meter(MeterName)) },
        },
    }));

    /// <summary>
    /// Adds the tracing interceptor and the metrics runtime to client options. A worker built on this
    /// client inherits the interceptor, so one trace runs client update → handler → activities.
    /// </summary>
    public static void ApplyTo(TemporalClientConnectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Interceptors = [new UpdateTracingInterceptor()];
        options.Runtime = Runtime.Value;
    }
}
