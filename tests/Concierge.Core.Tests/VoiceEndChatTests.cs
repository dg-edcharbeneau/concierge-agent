using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using System.Diagnostics;
using System.Reflection;
using Concierge.Contracts;
using Concierge.Web.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Concierge.Core.Tests;

public sealed class VoiceEndChatTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task LateModelCompletionCannotRegisterForARevisedFamily(int revisionOrdinal, bool allowed)
    {
        var connection = NewConnection();
        var old = AdmitTurn(connection);
        if (revisionOrdinal != 1) AdmitTurn(connection, "Unrelated fragment", revisionOrdinal);
        var model = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> CompleteModelAsync()
        {
            await model.Task;
            return connection.RegisterEndChatCall("late-call", old);
        }
        var registration = CompleteModelAsync();
        AdmitTurn(connection, "Where is breakfast?", revisionOrdinal);
        // Even a new delivery of the old text must not undo the revision.
        Assert.Equal(old, AdmitTurn(connection));
        model.SetResult();

        Assert.Equal(allowed, await registration);
        Assert.Equal(allowed, connection.ConfirmEndChatCall("late-call"));
        Assert.Equal(allowed, connection.AcceptEndChatToolResult("late-call"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevisionRevokesConfirmedCallAndAnyAcceptedFarewell(bool resultAlreadyReceived)
    {
        var connection = NewConnection();
        var old = AdmitTurn(connection);
        Assert.True(connection.RegisterEndChatCall("old-call", old));
        Assert.True(connection.ConfirmEndChatCall("old-call"));
        if (resultAlreadyReceived) Assert.True(connection.AcceptEndChatToolResult("old-call"));

        var current = AdmitTurn(connection, "Where is breakfast?");

        Assert.False(connection.HasEndChatResult);
        Assert.Null(connection.EndChatResultReceivedAtUtc);
        Assert.False(connection.TryClaimEndChatFlow(farewellOnly: true));
        Assert.False(connection.RegisterEndChatCall("old-call", old));
        Assert.False(connection.ConfirmEndChatCall("old-call"));
        Assert.False(connection.AcceptEndChatToolResult("old-call"));
        Assert.True(connection.RegisterEndChatCall("current-call", current));
        Assert.True(connection.ConfirmEndChatCall("current-call"));
        Assert.True(connection.AcceptEndChatToolResult("current-call"));
    }

    [Fact]
    public void ExactRetryPreservesTheConfirmedCall()
    {
        var connection = NewConnection();
        var turn = AdmitTurn(connection);
        Assert.True(connection.RegisterEndChatCall("retry-call", turn));
        Assert.True(connection.ConfirmEndChatCall("retry-call"));
        Assert.Equal(turn, AdmitTurn(connection));
        Assert.True(connection.RegisterEndChatCall("retry-call", turn));
        Assert.True(connection.AcceptEndChatToolResult("retry-call"));
    }

    [Fact]
    public void AdmissionRequiresValidatedProvenanceForThisConnection()
    {
        var connection = NewConnection();
        var turn = ThinkRequest.TurnId(connection.Session.Id, connection.Number, 1, "Goodbye");
        Assert.Throws<ArgumentException>(() => connection.AdmitEndChatTurn(turn, "Different text"));
        var foreign = ThinkRequest.TurnId(Guid.NewGuid(), connection.Number, 1, "Goodbye");
        Assert.Throws<ArgumentException>(() => connection.AdmitEndChatTurn(foreign, "Goodbye"));
        Assert.False(connection.RegisterEndChatCall("unadmitted-call", turn));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task IdleWarningIsSentOnceAtThirtySecondsBeforeTheUnchangedDeadline(int idleMinutes)
    {
        var time = new FakeTimeProvider();
        var connection = NewConnection(time);
        var relay = new VoiceRelay(
            WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions()),
            connection, NullLogger.Instance, time, TimeSpan.FromMinutes(idleMinutes));
        time.Advance(TimeSpan.FromSeconds(idleMinutes * 60 - 31));
        relay.CheckEndChatTimers();
        Assert.Equal(0, BrowserEventCount(relay));
        time.Advance(TimeSpan.FromSeconds(1));
        relay.CheckEndChatTimers();
        relay.CheckEndChatTimers();
        Assert.Equal(1, BrowserEventCount(relay));
        Assert.True((await NextBrowserEvent(relay)).GetProperty("active").GetBoolean());
        time.Advance(TimeSpan.FromSeconds(29));
        relay.CheckEndChatTimers();
        Assert.False(relay.EndChatFlowRequested);
        time.Advance(TimeSpan.FromSeconds(1));
        relay.CheckEndChatTimers();
        Assert.True(relay.EndChatFlowRequested);
        Assert.False((await NextBrowserEvent(relay)).GetProperty("active").GetBoolean());
        var ended = await NextBrowserEvent(relay);
        Assert.Equal("EndChatRequested", ended.GetProperty("type").GetString());
        Assert.Equal("idle_timeout", ended.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("""{"type":"UserStartedSpeaking"}""")]
    [InlineData("""{"type":"ConversationText","role":"user"}""")]
    public async Task SpeechClearsTheWarningAndRestartsItsDeadline(string speech)
    {
        var time = new FakeTimeProvider();
        var connection = NewConnection(time);
        var relay = NewRelay(connection, time);
        time.Advance(TimeSpan.FromSeconds(90));
        relay.CheckEndChatTimers();
        Assert.True((await NextBrowserEvent(relay)).GetProperty("active").GetBoolean());
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes(speech));
        var cleared = await NextBrowserEvent(relay);
        Assert.Equal("IdleWarning", cleared.GetProperty("type").GetString());
        Assert.False(cleared.GetProperty("active").GetBoolean());
        relay.HandleAgentEvent("""{"type":"AgentAudioDone"}"""u8.ToArray());
        time.Advance(TimeSpan.FromSeconds(89));
        relay.CheckEndChatTimers();
        Assert.False(relay.EndChatFlowRequested);
        // Only the forwarded speech and AudioDone events remain, not another warning.
        Assert.Equal(2, BrowserEventCount(relay));
    }

    [Fact]
    public async Task ReplyEligibilitySuppressesWarningAndReconnectRestartsTheClock()
    {
        var time = new FakeTimeProvider();
        var registry = new VoiceSessionRegistry(time);
        var stay = new ConciergeSessionContext(Guid.NewGuid(), Guid.NewGuid(), time.GetUtcNow().AddDays(1));
        var connection = registry.Open(stay, null);
        var relay = NewRelay(connection, time);
        connection.RecordReplyStarted();
        time.Advance(TimeSpan.FromSeconds(100));
        relay.CheckEndChatTimers();
        Assert.Equal(0, BrowserEventCount(relay));
        connection.RecordReplyCompleted();
        relay.CheckEndChatTimers();
        Assert.True((await NextBrowserEvent(relay)).GetProperty("active").GetBoolean());
        connection.RecordReplyStarted();
        relay.CheckEndChatTimers();
        Assert.False((await NextBrowserEvent(relay)).GetProperty("active").GetBoolean());
        time.Advance(TimeSpan.FromMinutes(2));
        relay.CheckEndChatTimers();
        Assert.False(relay.EndChatFlowRequested);
        var reconnected = registry.Open(stay, connection.Session.Id);
        reconnected.RecordReplyCompleted();
        var newRelay = NewRelay(reconnected, time);
        newRelay.CheckEndChatTimers();
        Assert.Equal(0, BrowserEventCount(newRelay));
        time.Advance(TimeSpan.FromSeconds(90));
        newRelay.CheckEndChatTimers();
        Assert.True((await NextBrowserEvent(newRelay)).GetProperty("active").GetBoolean());
        registry.Close(reconnected);
        time.Advance(TimeSpan.FromMinutes(2));
        newRelay.CheckEndChatTimers();
        Assert.False(newRelay.EndChatFlowRequested);
        Assert.Equal(0, BrowserEventCount(newRelay));
    }

    private static PlaybackQueue BrowserQueue(VoiceRelay relay) =>
        (PlaybackQueue)typeof(VoiceRelay).GetField("toBrowser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay)!;

    private static int BrowserEventCount(VoiceRelay relay) =>
        ((LinkedList<PlaybackItem>)typeof(PlaybackQueue)
            .GetField("items", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(BrowserQueue(relay))!).Count;

    private static async Task<JsonElement> NextBrowserEvent(VoiceRelay relay)
    {
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var item = await BrowserQueue(relay).DequeueAsync(new PlaybackPacer(9120), bound.Token);
        using var document = JsonDocument.Parse(item.Data);
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData("idle_timeout", "concierge.voice.end_chat.requested")]
    [InlineData("farewell_fallback", "concierge.voice.end_chat.requested")]
    [InlineData("farewell_playback_ready", "concierge.voice.end_chat.requested")]
    [InlineData("user_speech", "concierge.voice.end_chat.cancelled")]
    [InlineData("deepgram_cancelled", "concierge.voice.end_chat.cancelled")]
    public void EndLifecycleLogsAndTracesWithoutAnAmbientActivity(string reason, string eventName)
    {
        Assert.Null(Activity.Current);
        using var traces = new EndChatTraceCapture();
        using var logs = new CapturedLogs();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-18T12:00:00Z"));
        var connection = NewConnection(time);
        var relay = new VoiceRelay(
            WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions()),
            connection, logs.CreateLogger("VoiceRelay"), time);
        if (reason != "idle_timeout")
        {
            Assert.True(connection.RegisterEndChatCall("call-observed", AdmitTurn(connection)));
            Assert.True(connection.ConfirmEndChatCall("call-observed"));
            Assert.True(connection.AcceptEndChatToolResult("call-observed"));
        }

        switch (reason)
        {
            case "idle_timeout":
                time.Advance(TimeSpan.FromMinutes(2));
                relay.CheckEndChatTimers();
                relay.CheckEndChatTimers(); // A timer tick must not log another request.
                break;
            case "farewell_fallback":
                time.Advance(TimeSpan.FromSeconds(15));
                relay.CheckEndChatTimers();
                break;
            case "farewell_playback_ready":
                relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"ConversationText","role":"assistant","content":"private farewell"}"""));
                relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"AgentAudioDone"}"""));
                break;
            case "user_speech":
                relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"UserStartedSpeaking"}"""));
                break;
            case "deepgram_cancelled":
                relay.HandleAgentEvent(Encoding.UTF8.GetBytes(
                    """{"type":"FunctionCallCancelled","functions":[{"id":"call-observed","name":"end_chat"}]}"""));
                break;
        }

        var entry = Assert.Single(logs.Entries, entry => Equals(entry.Values.GetValueOrDefault("EndChatEvent"), eventName));
        Assert.Equal(reason, entry.Values["EndChatReason"]);
        Assert.Equal(connection.Session.Id.ToString(), entry.Values["VoiceSessionId"]);
        Assert.Equal(connection.Number, entry.Values["ConnectionNumber"]);
        Assert.Equal(reason == "idle_timeout" ? null : "call-observed", entry.Values["FunctionCallId"]);
        Assert.DoesNotContain("private farewell", entry.Message, StringComparison.Ordinal);
        var activity = traces.AssertEvent(eventName, connection.Session.Id);
        Assert.Equal(reason, activity.GetTagItem("concierge.voice.end_chat.reason"));
        Assert.Equal(connection.Number, activity.GetTagItem("concierge.voice.connection"));
        Assert.Equal(reason == "idle_timeout" ? null : "call-observed", activity.GetTagItem("concierge.voice.function_call_id"));
    }

    [Theory]
    [InlineData("call-observed", "concierge.voice.end_chat.function_request_accepted")]
    [InlineData("unknown", "concierge.voice.end_chat.function_request_rejected")]
    public void FunctionRequestAcceptanceIsObservable(string callId, string eventName)
    {
        using var traces = new EndChatTraceCapture();
        using var logs = new CapturedLogs();
        var connection = NewConnection();
        Assert.True(connection.RegisterEndChatCall("call-observed", AdmitTurn(connection)));
        var relay = new VoiceRelay(
            WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions()),
            connection, logs.CreateLogger("VoiceRelay"));
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes(
            $$"""{"type":"FunctionCallRequest","functions":[{"id":"{{callId}}","name":"end_chat","client_side":true,"arguments":"{}"}]}"""));

        Assert.Contains(logs.Entries, entry => Equals(entry.Values.GetValueOrDefault("EndChatEvent"), eventName));
        var activity = traces.AssertEvent(eventName, connection.Session.Id);
        Assert.Equal(callId, activity.GetTagItem("concierge.voice.function_call_id"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchedIsRecordedOnlyAfterSuccessfulBrowserSend(bool sendFails)
    {
        using var traces = new EndChatTraceCapture();
        using var logs = new CapturedLogs();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-18T12:00:00Z"));
        var connection = NewConnection(time);
        using var browser = new SendingWebSocket();
        var relay = new VoiceRelay(browser, connection, logs.CreateLogger("VoiceRelay"), time);
        time.Advance(TimeSpan.FromMinutes(2));
        relay.CheckEndChatTimers();
        Assert.DoesNotContain(logs.Entries, entry =>
            Equals(entry.Values.GetValueOrDefault("EndChatEvent"), "concierge.voice.end_chat.dispatched"));
        var stopping = (CancellationTokenSource)typeof(VoiceRelay)
            .GetField("stopping", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay)!;
        browser.Send = () =>
        {
            Assert.DoesNotContain(logs.Entries, entry =>
                Equals(entry.Values.GetValueOrDefault("EndChatEvent"), "concierge.voice.end_chat.dispatched"));
            if (sendFails) throw new WebSocketException();
            stopping.Cancel();
            return Task.CompletedTask;
        };
        var send = (Task)typeof(VoiceRelay)
            .GetMethod("SendToBrowserAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(relay, null)!;
        if (sendFails)
        {
            await Assert.ThrowsAsync<WebSocketException>(() => send);
            Assert.DoesNotContain(logs.Entries, entry =>
                Equals(entry.Values.GetValueOrDefault("EndChatEvent"), "concierge.voice.end_chat.dispatched"));
        }
        else
        {
            await send.WaitAsync(TimeSpan.FromSeconds(5));
            var activity = traces.AssertEvent("concierge.voice.end_chat.dispatched", connection.Session.Id);
            Assert.Equal("idle_timeout", activity.GetTagItem("concierge.voice.end_chat.reason"));
        }
    }

    private sealed class SendingWebSocket : WebSocket
    {
        public Func<Task> Send { get; set; } = () => Task.CompletedTask;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            Send();
    }

    [Fact]
    public void DeepgramMustReturnTheDeferredClientSideCallWithEmptyArguments()
    {
        var connection = NewConnection();
        Assert.True(connection.RegisterEndChatCall("call-1", AdmitTurn(connection)));

        var partial = """{"type":"FunctionCallRequest","functions":[{"id":"call-1","name":"end_chat","client_side":false,"arguments":"{}"}]}""";
        Assert.False(DeepgramFunctionProtocol.TryCreateEndChatResponse(
            Encoding.UTF8.GetBytes(partial), connection, out _));
        Assert.False(connection.HasEndChatResult);

        var invalid = """{"type":"FunctionCallRequest","functions":[{"id":"call-1","name":"end_chat","client_side":true,"arguments":"{\"force\":true}"}]}""";
        Assert.False(DeepgramFunctionProtocol.TryCreateEndChatResponse(
            Encoding.UTF8.GetBytes(invalid), connection, out _));
        Assert.False(connection.HasEndChatResult);

        var confirmed = """{"type":"FunctionCallRequest","functions":[{"id":"call-1","name":"end_chat","client_side":true,"arguments":"{}"}]}""";
        Assert.True(DeepgramFunctionProtocol.TryCreateEndChatResponse(
            Encoding.UTF8.GetBytes(confirmed), connection, out var response));
        using var responseJson = JsonDocument.Parse(response);
        Assert.Equal("FunctionCallResponse", responseJson.RootElement.GetProperty("type").GetString());
        Assert.Equal("call-1", responseJson.RootElement.GetProperty("id").GetString());
        Assert.Equal(ConciergeEndChat.FunctionName, responseJson.RootElement.GetProperty("name").GetString());
        Assert.True(connection.ConfirmEndChatCall("call-1"));
    }

    [Fact]
    public void FunctionCallCancellationIsHonoredAndServerEchoDoesNotEndChat()
    {
        var connection = NewConnection();
        Assert.True(connection.RegisterEndChatCall("call-cancel", AdmitTurn(connection)));
        var relay = NewRelay(connection);
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes(
            """{"type":"FunctionCallCancelled","functions":[{"id":"call-cancel","name":"end_chat"}]}"""));

        Assert.False(connection.ConfirmEndChatCall("call-cancel"));
        Assert.False(connection.AcceptEndChatToolResult("call-cancel"));
        Assert.False(connection.HasEndChatResult);

        var echoedConnection = NewConnection();
        Assert.True(echoedConnection.RegisterEndChatCall("call-echo", AdmitTurn(echoedConnection)));
        Assert.True(echoedConnection.ConfirmEndChatCall("call-echo"));
        NewRelay(echoedConnection).HandleAgentEvent(Encoding.UTF8.GetBytes(
            """{"type":"FunctionCallResponse","id":"call-echo","name":"end_chat","content":"End chat confirmed."}"""));

        Assert.False(echoedConnection.HasEndChatResult);
        Assert.True(echoedConnection.TryClaimEndChatFlow());
    }

    [Fact]
    public void ConfirmedFunctionCallCanStillBeCancelledBeforeFarewellCompletes()
    {
        var connection = NewConnection();
        Assert.True(connection.RegisterEndChatCall("call-barge-in", AdmitTurn(connection)));
        Assert.True(connection.ConfirmEndChatCall("call-barge-in"));
        Assert.True(connection.AcceptEndChatToolResult("call-barge-in"));
        var relay = NewRelay(connection);

        relay.HandleAgentEvent(Encoding.UTF8.GetBytes(
            """{"type":"FunctionCallCancelled","functions":[{"id":"call-barge-in","name":"end_chat"}]}"""));

        Assert.False(connection.HasEndChatResult);
        Assert.False(connection.AcceptEndChatToolResult("call-barge-in"));
        Assert.True(connection.TryClaimEndChatFlow());
    }

    [Fact]
    public void ResumedSpeechCancelsFarewellAndPreventsAnEndRequest()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T12:00:00Z"));
        var connection = NewConnection(time);
        Assert.True(connection.RegisterEndChatCall("call-interrupted-farewell", AdmitTurn(connection)));
        Assert.True(connection.ConfirmEndChatCall("call-interrupted-farewell"));
        Assert.True(connection.AcceptEndChatToolResult("call-interrupted-farewell"));
        var relay = NewRelay(connection, time);

        relay.HandleAgentEvent(Encoding.UTF8.GetBytes(
            """{"type":"ConversationText","role":"assistant"}"""));
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"UserStartedSpeaking"}"""));
        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"AgentAudioDone"}"""));

        Assert.False(connection.HasEndChatResult);
        Assert.False(relay.EndChatFlowRequested);
        Assert.False(relay.EndChatAfterPlaybackRequested);
        Assert.False(connection.AcceptEndChatToolResult("call-interrupted-farewell"));

        // The cancelled result no longer arms the 15-second fallback.
        time.Advance(TimeSpan.FromSeconds(16));
        relay.CheckEndChatTimers();
        Assert.False(relay.EndChatFlowRequested);
    }

    [Fact]
    public void FarewellTimeoutRequestsEndOnlyAfterBrowserPlaybackDrains()
    {
        var now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
        var time = new FakeTimeProvider(now);
        var connection = NewConnection(time);
        Assert.True(connection.RegisterEndChatCall("call-no-audio", AdmitTurn(connection)));
        Assert.True(connection.ConfirmEndChatCall("call-no-audio"));
        Assert.True(connection.AcceptEndChatToolResult("call-no-audio"));
        var relay = NewRelay(connection, time);

        time.Advance(TimeSpan.FromSeconds(15));
        relay.CheckEndChatTimers();

        Assert.True(relay.EndChatFlowRequested);
        Assert.True(relay.EndChatAfterPlaybackRequested);
    }

    [Fact]
    public void DetectedDeepgramSpeechResetsIdleDeadline()
    {
        var now = DateTimeOffset.Parse("2026-10-17T12:00:00Z");
        var time = new FakeTimeProvider(now);
        var connection = new VoiceSessionRegistry(time).Open(
            new ConciergeSessionContext(Guid.NewGuid(), Guid.NewGuid(), now.AddDays(1)), null);
        var relay = new VoiceRelay(
            WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions()),
            connection,
            NullLogger.Instance,
            time);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(connection.IsIdle(TimeSpan.FromMinutes(2)));

        relay.HandleAgentEvent(Encoding.UTF8.GetBytes("""{"type":"UserStartedSpeaking"}"""));

        Assert.False(connection.IsIdle(TimeSpan.FromMinutes(2)));
    }

    internal static string AdmitTurn(VoiceConnection connection, string text = "Goodbye", int ordinal = 1)
    {
        var turnId = ThinkRequest.TurnId(connection.Session.Id, connection.Number, ordinal, text);
        connection.AdmitEndChatTurn(turnId, text);
        return turnId;
    }

    private static VoiceConnection NewConnection() =>
        new VoiceSessionRegistry(TimeProvider.System)
            .Open(new ConciergeSessionContext(
                Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)), null);

    private static VoiceConnection NewConnection(TimeProvider time) =>
        new VoiceSessionRegistry(time)
            .Open(new ConciergeSessionContext(
                Guid.NewGuid(), Guid.NewGuid(), time.GetUtcNow().AddDays(1)), null);

    private static VoiceRelay NewRelay(VoiceConnection connection) =>
        new(
            WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions()),
            connection,
            NullLogger.Instance);

    private static VoiceRelay NewRelay(VoiceConnection connection, TimeProvider time) =>
        new(
            WebSocket.CreateFromStream(new MemoryStream(), new WebSocketCreationOptions()),
            connection,
            NullLogger.Instance,
            time);
}
