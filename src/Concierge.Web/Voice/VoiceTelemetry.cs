using System.Diagnostics.Metrics;

namespace Concierge.Web.Voice;

/// <summary>
/// Voice-path metrics on the application meter that ServiceDefaults registers. Spans for a Think
/// turn come from ASP.NET Core and the Temporal client; Think tags the request span.
/// </summary>
internal static class VoiceTelemetry
{
    private static readonly Meter Meter = new(typeof(VoiceTelemetry).Assembly.GetName().Name!);

    /// <summary>Think request duration in seconds, tagged with <c>outcome</c>.</summary>
    public static readonly Histogram<double> ThinkDuration = Meter.CreateHistogram<double>(
        "concierge.voice.think.duration",
        unit: "s",
        description: "Time from Think request authentication to the answer (or failure) being ready.");

    /// <summary>Interruption-to-silence acknowledgement in seconds, tagged with <c>barge_in</c>.</summary>
    public static readonly Histogram<double> InterruptionSilenced = Meter.CreateHistogram<double>(
        "concierge.voice.interruption.silenced.duration",
        unit: "s",
        description: "Time from Deepgram's UserStartedSpeaking to the browser worklet confirming silence.");

    /// <summary>Open browser voice relays.</summary>
    public static readonly UpDownCounter<long> ActiveRelays = Meter.CreateUpDownCounter<long>(
        "concierge.voice.relay.active",
        unit: "{connection}",
        description: "Browser voice relay connections currently open.");

    public static void RecordThink(long startedTimestamp, string outcome) =>
        ThinkDuration.Record(
            System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds,
            new KeyValuePair<string, object?>("outcome", outcome));
}
