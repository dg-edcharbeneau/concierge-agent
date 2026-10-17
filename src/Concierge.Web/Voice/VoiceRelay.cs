using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Concierge.Contracts;

namespace Concierge.Web.Voice;

/// <summary>Why a voice relay stopped.</summary>
public enum VoiceRelayEnd
{
    BrowserClosed,
    Revoked,
    MicBacklog,
    PlaybackBacklog,
    InvalidBrowserMessage,
    DeepgramConnectFailed,
    DeepgramClosed,
    DeepgramProtocolError,
    Failed,
}

/// <summary>
/// Relays one browser voice WebSocket (binary = PCM, text = JSON) to a Deepgram Voice Agent
/// connection held with the server's API key. Ported from the relay-latency prototype: agent audio
/// is paced to a ~380 ms browser playback lead so an interruption can discard the rest server-side.
/// </summary>
internal sealed class VoiceRelay(WebSocket browser, VoiceConnection connection, ILogger logger)
{
    public static readonly Uri AgentUri = new("wss://agent.deepgram.com/v1/agent/converse");

    // 25 mic frames of 20 ms is about half a second; a fuller queue means Deepgram is not keeping up.
    private const int MicQueueFrames = 25;
    // Minutes of agent audio at 20 ms frames; overflow means the browser stopped acknowledging playback.
    private const int BrowserQueueItems = 4000;
    // 20 ms of mono linear16 at 24 kHz.
    private const int PlaybackFrameBytes = VoiceAgentSettings.OutputSampleRate / 50 * 2;
    private const int MaxBrowserMessageBytes = 64 * 1024;
    private const int MaxAgentMessageBytes = 256 * 1024;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloseBound = TimeSpan.FromSeconds(3);
    private static readonly byte[] KeepAliveMessage = """{"type":"KeepAlive"}"""u8.ToArray();
    private readonly Channel<byte[]> toAgent = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MicQueueFrames) { SingleReader = true, SingleWriter = true });
    private readonly PlaybackQueue toBrowser = new(BrowserQueueItems);
    private readonly PlaybackPacer pacer = new(VoiceAgentSettings.OutputSampleRate * 380 / 1000);
    private readonly VoiceInterruptionTimings interruptionTimings = new();
    // Deepgram repeats some warnings (SLOW_THINK_REQUEST) while one Think request is pending;
    // each code is logged once per user turn. Only the Deepgram reader touches this set.
    private readonly HashSet<string> warningCodesThisTurn = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stopping = new();
    private int end = -1;
    private int interruptions;
    private int interruptionSequence;
    private int activeInterruptionNumber;
    // Agent audio between UserStartedSpeaking and the user's final transcript belongs to a superseded reply.
    private bool suppressAgentAudio;

    public async Task<VoiceRelayEnd> RunAsync(string apiKey, byte[] settings, CancellationToken requestAborted)
    {
        var started = Stopwatch.GetTimestamp();
        using var agent = new ClientWebSocket();
        agent.Options.SetRequestHeader("Authorization", "Token " + apiKey);
        agent.Options.CollectHttpResponseDetails = true;
        using var stopOnRevoke = connection.Revoked.Register(() => Stop(VoiceRelayEnd.Revoked));
        using var stopOnAbort = requestAborted.Register(() => Stop(VoiceRelayEnd.BrowserClosed));

        try
        {
            await agent.ConnectAsync(AgentUri, stopping.Token);
            await agent.SendAsync(settings, WebSocketMessageType.Text, true, stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            await CloseBrowserAsync(null);
            return Ended;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Deepgram Voice Agent connection failed (HTTP {StatusCode}). Cause types: {CauseTypes}. "
                + "Check Deepgram__ApiKey and outbound access to agent.deepgram.com.",
                (int)agent.HttpStatusCode,
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            Stop(VoiceRelayEnd.DeepgramConnectFailed);
            await CloseBrowserAsync(null);
            return Ended;
        }

        // Readers never take the stop token: cancelling a WebSocket receive aborts the socket, which
        // would prevent a clean close frame. They end when the peer answers our close.
        var browserReader = Guard(ReadBrowserAsync, VoiceRelayEnd.BrowserClosed);
        var agentReader = Guard(() => ReadAgentAsync(agent), VoiceRelayEnd.DeepgramClosed);
        var agentSender = Guard(() => SendToAgentAsync(agent), VoiceRelayEnd.DeepgramClosed);
        var browserSender = Guard(SendToBrowserAsync, VoiceRelayEnd.BrowserClosed);

        await Task.WhenAny(browserReader, agentReader, agentSender, browserSender,
            Task.Delay(Timeout.Infinite, stopping.Token).ContinueWith(_ => { }, TaskScheduler.Default));
        Stop(VoiceRelayEnd.Failed);
        await Task.WhenAll(agentSender, browserSender);

        await CloseAsync(agent, agentReader, WebSocketCloseStatus.NormalClosure, "session ended");
        await CloseBrowserAsync(browserReader);

        logger.LogInformation(
            "Voice relay ended: {EndReason} after {DurationSeconds:F0} s (connection {ConnectionNumber}, {Interruptions} interruptions).",
            Ended, Stopwatch.GetElapsedTime(started).TotalSeconds, connection.Number, interruptions);
        return Ended;
    }

    private VoiceRelayEnd Ended => (VoiceRelayEnd)Volatile.Read(ref end);

    /// <summary>Records the first end reason and stops the send loops.</summary>
    private void Stop(VoiceRelayEnd reason)
    {
        if (Interlocked.CompareExchange(ref end, (int)reason, -1) == -1)
        {
            stopping.Cancel();
        }
    }

    private async Task Guard(Func<Task> pump, VoiceRelayEnd onFailure)
    {
        try
        {
            await pump();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (InvalidDataException)
        {
            logger.LogWarning("A voice WebSocket message exceeded the relay size limit; ending voice.");
            Stop(VoiceRelayEnd.InvalidBrowserMessage);
        }
        catch (Exception exception) when (exception is WebSocketException or IOException)
        {
            // Expected when either peer drops; only the first failure decides the end reason.
            Stop(onFailure);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Voice relay pump failed. Cause types: {CauseTypes}.",
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            Stop(VoiceRelayEnd.Failed);
        }
    }

    private async Task ReadBrowserAsync()
    {
        var buffer = new byte[MaxBrowserMessageBytes];
        while (true)
        {
            var (type, data) = await ReceiveAsync(browser, buffer);
            if (type == WebSocketMessageType.Close)
            {
                Stop(VoiceRelayEnd.BrowserClosed);
                return;
            }

            if (stopping.IsCancellationRequested)
            {
                continue;
            }

            if (type == WebSocketMessageType.Binary)
            {
                // Never drop speech silently: a full queue ends voice and the guest reconnects.
                if (!toAgent.Writer.TryWrite(data))
                {
                    Stop(VoiceRelayEnd.MicBacklog);
                }

                continue;
            }

            if (TryReadInterruptionSilenced(data, out var interruptionNumber, out var bargeIn))
            {
                if (interruptionTimings.TryComplete(interruptionNumber, out var durationMilliseconds))
                {
                    logger.LogInformation(
                        "Voice worklet silence acknowledged after {InterruptionDurationMilliseconds:F1} ms "
                        + "(connection {ConnectionNumber}, interruption {InterruptionNumber}, browser-rendered barge-in {BargeIn}).",
                        durationMilliseconds, connection.Number, interruptionNumber, bargeIn);
                    VoiceTelemetry.InterruptionSilenced.Record(
                        durationMilliseconds / 1000,
                        new KeyValuePair<string, object?>("barge_in", bargeIn));
                }

                continue;
            }

            if (!TryReadPlaybackAck(data, out var samples) || !pacer.Acknowledge(samples))
            {
                Stop(VoiceRelayEnd.InvalidBrowserMessage);
                continue;
            }

            toBrowser.Signal();
        }
    }

    private async Task SendToAgentAsync(ClientWebSocket agent)
    {
        var reader = toAgent.Reader;
        while (!stopping.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
            wait.CancelAfter(KeepAliveInterval);
            try
            {
                await reader.WaitToReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
            {
                // A hidden page suspends the microphone; Deepgram closes idle connections without this.
                await agent.SendAsync(KeepAliveMessage, WebSocketMessageType.Text, true, stopping.Token);
                continue;
            }

            while (reader.TryRead(out var frame))
            {
                await agent.SendAsync(frame, WebSocketMessageType.Binary, true, stopping.Token);
            }
        }
    }

    private async Task ReadAgentAsync(ClientWebSocket agent)
    {
        var buffer = new byte[MaxAgentMessageBytes];
        while (true)
        {
            var (type, data) = await ReceiveAsync(agent, buffer);
            if (type == WebSocketMessageType.Close)
            {
                if (agent.CloseStatus is { } status && status != WebSocketCloseStatus.NormalClosure)
                {
                    logger.LogWarning("Deepgram closed the Voice Agent connection with status {CloseStatus}.", (int)status);
                }

                Stop(VoiceRelayEnd.DeepgramClosed);
                return;
            }

            // Keep draining until Deepgram answers our close.
            if (stopping.IsCancellationRequested)
            {
                continue;
            }

            if (type == WebSocketMessageType.Binary)
            {
                EnqueueAgentAudio(data);
                continue;
            }

            HandleAgentEvent(data);
        }
    }

    private void EnqueueAgentAudio(byte[] data)
    {
        if (suppressAgentAudio)
        {
            return;
        }

        if (data.Length % 2 != 0)
        {
            logger.LogWarning("Deepgram sent an odd-length linear16 frame; ending voice.");
            Stop(VoiceRelayEnd.DeepgramProtocolError);
            return;
        }

        // Bound each send quantum so one large Deepgram message cannot exceed the playback lead.
        for (var offset = 0; offset < data.Length; offset += PlaybackFrameBytes)
        {
            var length = Math.Min(PlaybackFrameBytes, data.Length - offset);
            if (!toBrowser.TryEnqueue(new PlaybackItem(false, data.AsSpan(offset, length).ToArray())))
            {
                Stop(VoiceRelayEnd.PlaybackBacklog);
                return;
            }
        }
    }

    internal void HandleAgentEvent(byte[] data)
    {
        string? type;
        string? role;
        string? code;
        string? description;
        string? requestId;
        LatencyReport? latency = null;
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            type = ReadString(root, "type");
            role = ReadString(root, "role");
            code = ReadString(root, "code");
            // Deepgram-authored Error/Warning text and the Welcome session ID; never transcript content.
            description = ReadString(root, "description");
            requestId = ReadString(root, "request_id");
            if (type == "LatencyReport")
            {
                latency = new LatencyReport(
                    ReadSeconds(root, "total_latency"),
                    ReadSeconds(root, "stt_latency"),
                    ReadSeconds(root, "ttt_token_latency"),
                    ReadSeconds(root, "ttt_text_latency"),
                    ReadSeconds(root, "ttt_tool_latency"),
                    ReadSeconds(root, "ttt_thinking_latency"),
                    ReadSeconds(root, "tts_latency"));
            }
        }
        catch (JsonException)
        {
            logger.LogWarning("Deepgram sent a text frame that is not JSON; ending voice.");
            Stop(VoiceRelayEnd.DeepgramProtocolError);
            return;
        }

        switch (type)
        {
            case "UserStartedSpeaking":
                // Repeated speech notifications for one utterance still flush, but share one timing sample.
                Interlocked.Increment(ref interruptions);
                toBrowser.FlushAudio();
                if (!suppressAgentAudio)
                {
                    activeInterruptionNumber = Interlocked.Increment(ref interruptionSequence);
                    interruptionTimings.Begin(activeInterruptionNumber);
                }

                suppressAgentAudio = true;
                toBrowser.ForceEnqueue(new PlaybackItem(true, JsonSerializer.SerializeToUtf8Bytes(
                    new { type = "UserStartedSpeaking", interruptionNumber = activeInterruptionNumber })));
                break;
            case "ConversationText" when role == "user":
                // Live sessions sent no AgentStartedSpeaking; the user's final transcript ends the interruption.
                suppressAgentAudio = false;
                activeInterruptionNumber = 0;
                warningCodesThisTurn.Clear();
                Forward(type, role);
                break;
            case "AgentStartedSpeaking":
                suppressAgentAudio = false;
                activeInterruptionNumber = 0;
                Forward(type, null);
                break;
            case "ConversationText":
                Forward(type, role);
                break;
            case "SettingsApplied" or "AgentThinking" or "AgentAudioDone":
                Forward(type, null);
                break;
            case "Welcome":
                logger.LogInformation(
                    "Deepgram Voice Agent session {DeepgramRequestId} opened (connection {ConnectionNumber}).",
                    requestId ?? "(none)", connection.Number);
                break;
            case "LatencyReport" when latency is { } report:
                logger.LogInformation(
                    "Deepgram latency report (connection {ConnectionNumber}): total {TotalLatencySeconds} s, "
                    + "STT {SttLatencySeconds} s, first token {TttTokenLatencySeconds} s, first text {TttTextLatencySeconds} s, "
                    + "first tool {TttToolLatencySeconds} s, first thinking {TttThinkingLatencySeconds} s, TTS {TtsLatencySeconds} s.",
                    connection.Number, report.Total, report.Stt, report.TttToken, report.TttText,
                    report.TttTool, report.TttThinking, report.Tts);
                break;
            case "Error":
                logger.LogWarning(
                    "Deepgram Voice Agent reported error {DeepgramCode}: {DeepgramDescription} (connection {ConnectionNumber}).",
                    code ?? "(none)", description ?? "(none)", connection.Number);
                Forward(type, null);
                break;
            case "Warning" when warningCodesThisTurn.Add(code ?? "(none)"):
                logger.LogInformation(
                    "Deepgram Voice Agent reported warning {DeepgramCode}: {DeepgramDescription} (connection {ConnectionNumber}).",
                    code ?? "(none)", description ?? "(none)", connection.Number);
                break;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Every LatencyReport field is optional; a missing or non-numeric field logs as null.
    private static double? ReadSeconds(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var seconds)
            ? seconds
            : null;

    private readonly record struct LatencyReport(
        double? Total, double? Stt, double? TttToken, double? TttText, double? TttTool, double? TttThinking, double? Tts);

    // The browser needs event types for its state display, never transcript text.
    private void Forward(string type, string? role) =>
        toBrowser.TryEnqueue(new PlaybackItem(true, role is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { type })
            : JsonSerializer.SerializeToUtf8Bytes(new { type, role })));

    private async Task SendToBrowserAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            var item = await toBrowser.DequeueAsync(pacer, stopping.Token);
            await browser.SendAsync(
                item.Data,
                item.IsText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
                true,
                stopping.Token);
        }
    }

    private async Task CloseBrowserAsync(Task? reader)
    {
        var (status, description) = Ended switch
        {
            VoiceRelayEnd.BrowserClosed => (WebSocketCloseStatus.NormalClosure, "Voice ended"),
            VoiceRelayEnd.Revoked when connection.Revocation == VoiceRevocation.Ended =>
                (VoiceCloseCodes.Unauthorized, "Chat ended. Verify your stay again to continue."),
            VoiceRelayEnd.DeepgramConnectFailed =>
                (VoiceCloseCodes.Unavailable, "Voice is unavailable right now. You can keep chatting by text."),
            _ => (VoiceCloseCodes.Disconnected, "Voice disconnected"),
        };

        if (reader is null)
        {
            await VoiceCloseCodes.CloseAsync(browser, status, description);
            return;
        }

        await CloseAsync(browser, reader, status, description);
    }

    private static async Task CloseAsync(WebSocket socket, Task reader, WebSocketCloseStatus status, string description)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var bound = new CancellationTokenSource(CloseBound);
                await socket.CloseOutputAsync(status, description, bound.Token);
            }

            await reader.WaitAsync(CloseBound);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or TimeoutException)
        {
            socket.Abort();
        }
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(WebSocket socket, byte[] buffer)
    {
        var count = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            if (count == buffer.Length)
            {
                throw new InvalidDataException("WebSocket message exceeds the relay limit.");
            }

            result = await socket.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
            count += result.Count;
        }
        while (!result.EndOfMessage);

        return (result.MessageType, buffer[..count]);
    }

    private static bool TryReadPlaybackAck(byte[] data, out long samples)
    {
        samples = 0;
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.ValueEquals("PlaybackAck")
                && root.TryGetProperty("samples", out var value)
                && value.TryGetInt64(out samples);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryReadInterruptionSilenced(byte[] data, out int interruptionNumber, out bool bargeIn)
    {
        interruptionNumber = 0;
        bargeIn = false;
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var valid = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.ValueEquals("InterruptionSilenced")
                && root.TryGetProperty("interruptionNumber", out var number)
                && number.ValueKind == JsonValueKind.Number
                && number.TryGetInt32(out interruptionNumber)
                && interruptionNumber > 0
                && root.TryGetProperty("bargeIn", out var playback)
                && playback.ValueKind is JsonValueKind.True or JsonValueKind.False;
            if (valid)
            {
                bargeIn = root.GetProperty("bargeIn").GetBoolean();
            }
            return valid;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Pairs a Deepgram speech event with its matching worklet silence acknowledgment.</summary>
internal sealed class VoiceInterruptionTimings(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<int, long> pending = new();

    public void Begin(int interruptionNumber) =>
        pending.TryAdd(interruptionNumber, clock.GetTimestamp());

    public bool TryComplete(int interruptionNumber, out double durationMilliseconds)
    {
        if (pending.TryRemove(interruptionNumber, out var entry))
        {
            durationMilliseconds = clock.GetElapsedTime(entry).TotalMilliseconds;
            return true;
        }

        durationMilliseconds = 0;
        return false;
    }
}

/// <summary>Application close codes the browser maps to guest-facing states.</summary>
internal static class VoiceCloseCodes
{
    /// <summary>Show "Voice disconnected" with manual Reconnect.</summary>
    public const WebSocketCloseStatus Disconnected = (WebSocketCloseStatus)4000;

    /// <summary>The chat credential is no longer valid; the guest must verify again.</summary>
    public const WebSocketCloseStatus Unauthorized = (WebSocketCloseStatus)4401;

    /// <summary>Voice cannot start now; text chat still works.</summary>
    public const WebSocketCloseStatus Unavailable = (WebSocketCloseStatus)4503;

    /// <summary>Closes a socket that has no pending reader, bounded so a silent peer cannot hold the request.</summary>
    public static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await socket.CloseAsync(status, description, bound.Token);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            socket.Abort();
        }
    }
}

internal sealed record PlaybackItem(bool IsText, byte[] Data);

/// <summary>Bounded FIFO whose interrupt flush removes queued agent audio but keeps JSON events.</summary>
internal sealed class PlaybackQueue(int capacity)
{
    private readonly LinkedList<PlaybackItem> items = new();
    private readonly SemaphoreSlim signal = new(0);

    public bool TryEnqueue(PlaybackItem item)
    {
        lock (items)
        {
            if (items.Count >= capacity)
            {
                return false;
            }

            items.AddLast(item);
        }

        signal.Release();
        return true;
    }

    /// <summary>Interrupt events bypass capacity so a full queue cannot hide them.</summary>
    public void ForceEnqueue(PlaybackItem item)
    {
        lock (items)
        {
            items.AddLast(item);
        }

        signal.Release();
    }

    /// <summary>Removes queued agent audio and returns how many audio frames were discarded.</summary>
    public int FlushAudio()
    {
        var removed = 0;
        lock (items)
        {
            for (var node = items.First; node is not null;)
            {
                var next = node.Next;
                if (!node.Value.IsText)
                {
                    items.Remove(node);
                    removed++;
                }

                node = next;
            }
        }

        return removed;
    }

    /// <summary>Wakes the sender after a playback acknowledgement frees lead budget.</summary>
    public void Signal() => signal.Release();

    public async Task<PlaybackItem> DequeueAsync(PlaybackPacer pacer, CancellationToken cancellationToken)
    {
        while (true)
        {
            await signal.WaitAsync(cancellationToken);
            lock (items)
            {
                if (items.First is { } first
                    && (first.Value.IsText || pacer.TryReserve(first.Value.Data.Length / 2)))
                {
                    items.RemoveFirst();
                    return first.Value;
                }
            }
        }
    }
}

/// <summary>
/// Tracks agent samples sent to the browser but not yet played or discarded by its playback worklet.
/// Audio beyond the lead budget stays server-side, where an interruption can still discard it.
/// </summary>
internal sealed class PlaybackPacer(long maxLeadSamples)
{
    private readonly Lock gate = new();
    private long sentSamples;
    private long acknowledgedSamples;

    public bool TryReserve(int samples)
    {
        lock (gate)
        {
            if (sentSamples - acknowledgedSamples + samples > maxLeadSamples)
            {
                return false;
            }

            sentSamples += samples;
            return true;
        }
    }

    /// <summary>Accepts the worklet's cumulative retired-sample count; it can never go backwards or exceed sends.</summary>
    public bool Acknowledge(long samples)
    {
        lock (gate)
        {
            if (samples < acknowledgedSamples || samples > sentSamples)
            {
                return false;
            }

            acknowledgedSamples = samples;
            return true;
        }
    }
}
