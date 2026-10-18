using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Concierge.Web.Voice;

/// <summary>
/// Voice-path metrics on the application meter that ServiceDefaults registers. Spans for a Think
/// turn come from ASP.NET Core and the Temporal client; Think tags the request span.
/// </summary>
internal static class VoiceTelemetry
{
    // ServiceDefaults registers the Web application name as a tracing source.
    internal static readonly ActivitySource Source = new(typeof(VoiceTelemetry).Assembly.GetName().Name!);
    private static readonly Meter Meter = new(typeof(VoiceTelemetry).Assembly.GetName().Name!);

    internal static void RecordEndChat(
        ILogger logger, string transition, VoiceConnection connection, string reason, string? functionCallId = null) =>
        RecordEndChat(logger, transition, connection.Session.Id.ToString(), reason,
            connection.Number, functionCallId ?? connection.EndChatCallId);

    internal static void RecordEndChat(
        ILogger logger, string transition, string? sessionId, string reason,
        int? connectionNumber = null, string? functionCallId = null)
    {
        var name = "concierge.voice.end_chat." + transition;
        var tags = new ActivityTagsCollection
        {
            { "concierge.voice.session_id", sessionId },
            { "concierge.voice.connection", connectionNumber },
            { "concierge.voice.function_call_id", functionCallId },
            { "concierge.voice.end_chat.reason", reason },
        };
        // Relay pumps and timers need their own sampled activity; Activity.Current may be null.
        using var activity = Source.StartActivity(name, ActivityKind.Internal, default(ActivityContext), tags);
        activity?.AddEvent(new ActivityEvent(name, tags: tags));
        if (transition == "completion_failed")
        {
            activity?.SetStatus(ActivityStatusCode.Error, reason);
            logger.LogWarning(
                "Voice end-chat {EndChatEvent}: {EndChatReason} (session {VoiceSessionId}, connection {ConnectionNumber}, function {FunctionCallId}).",
                name, reason, sessionId, connectionNumber, functionCallId);
        }
        else
        {
            logger.LogInformation(
                "Voice end-chat {EndChatEvent}: {EndChatReason} (session {VoiceSessionId}, connection {ConnectionNumber}, function {FunctionCallId}).",
                name, reason, sessionId, connectionNumber, functionCallId);
        }
    }

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
