using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Concierge.WorkflowServer.AI;

/// <summary>
/// Telemetry for the bounded model clients. The source and meter use the application name that
/// ServiceDefaults registers; the MEAI source/meter carries the gen_ai spans and metrics.
/// </summary>
internal static class ConciergeAITelemetry
{
    /// <summary>Default MEAI <c>UseOpenTelemetry</c> source and meter name (gen_ai spans, durations, tokens, time to first chunk).</summary>
    public const string MeaiSourceName = "Experimental.Microsoft.Extensions.AI";

    private static readonly string Name = typeof(ConciergeAITelemetry).Assembly.GetName().Name!;
    private static readonly ActivitySource Source = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> SlotWait = Meter.CreateHistogram<double>(
        "concierge.ai.concurrency_slot.wait.duration",
        unit: "s",
        description: "Time a model request waited for a concurrency slot before reaching the provider.");

    /// <summary>
    /// Waits for <paramref name="gate"/> inside a span and records the wait, so slot contention is
    /// distinguishable from provider latency.
    /// </summary>
    public static async Task WaitForSlotAsync(SemaphoreSlim gate, string client, CancellationToken cancellationToken)
    {
        using var span = Source.StartActivity("ai.concurrency_slot.wait");
        span?.SetTag("concierge.ai.client", client);
        var started = Stopwatch.GetTimestamp();
        var outcome = "acquired";
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            span?.SetStatus(ActivityStatusCode.Error, "Canceled while waiting for a concurrency slot.");
            throw;
        }
        finally
        {
            SlotWait.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("concierge.ai.client", client),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }
}
