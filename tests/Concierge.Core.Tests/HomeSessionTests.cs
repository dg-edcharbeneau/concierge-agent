using System.Reflection;
using System.Runtime.CompilerServices;
using Concierge.Contracts;
using Concierge.Web.Components.Pages;
using Concierge.Web.Services;
using Concierge.Web.Voice;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Concierge.Core.Tests;

public sealed class HomeSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VoiceEndCallbackDistinguishesCompletedAndFailedInteraction(bool failure)
    {
        using var traces = new EndChatTraceCapture();
        using var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        await using var harness = new Harness();
        var voiceSessionId = Guid.NewGuid();
        harness.SetSession("private-credential", Guid.NewGuid());
        harness.Set("Logger", loggerFactory.CreateLogger<Home>());
        harness.Set("voiceMode", true);
        harness.Set("voiceSessionId", voiceSessionId.ToString());
        if (failure)
        {
            harness.Service.End = _ => throw new ConciergeApiException("unavailable", "private-error", 503);
        }

        await harness.VoiceEndRequested();

        traces.AssertEvent("concierge.voice.end_chat.browser_callback_received", voiceSessionId);
        var eventName = failure ? "completion_failed" : "completed";
        var activity = traces.AssertEvent("concierge.voice.end_chat." + eventName, voiceSessionId);
        Assert.Equal(failure ? System.Diagnostics.ActivityStatusCode.Error : System.Diagnostics.ActivityStatusCode.Unset,
            activity.Status);
        Assert.Equal(!failure, harness.Get<bool>("chatEnded"));
        Assert.Contains(logs.Entries, entry =>
            Equals(entry.Values.GetValueOrDefault("EndChatEvent"), "concierge.voice.end_chat." + eventName));
        Assert.DoesNotContain(logs.Entries, entry =>
            entry.Message.Contains("private-credential", StringComparison.Ordinal)
            || entry.Message.Contains("private-error", StringComparison.Ordinal));
    }

    /// <summary>Preserves retry IDs and drafts only for the verified original stay.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReverificationOnlyRecoversTheSameStay(bool sameStay)
    {
        await using var harness = new Harness();
        var stay = Guid.NewGuid();
        harness.SetSession("old", stay);
        harness.Set("pending", new PendingTurnState("retry-id", "Old guest message", "web", DateTimeOffset.UtcNow));
        harness.Set("draft", "Old guest draft");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", sameStay ? stay : Guid.NewGuid());
        await harness.Call("VerifyAsync");

        Assert.Equal(sameStay ? "Old guest draft" : "", harness.Get<string>("draft"));
        Assert.Equal(sameStay ? 1 : 0, harness.Service.Submissions.Count);
        if (sameStay)
        {
            Assert.Equal("retry-id", Assert.Single(harness.Service.Submissions).TurnId);
        }
    }

    /// <summary>Rejects both successful and unauthorized history from an earlier sign-in.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldHistoryCannotChangeANewSession(bool unauthorized)
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        var response = new TaskCompletionSource<ConciergeHistoryResult>();
        harness.Service.History = credential => credential == "old"
            ? response.Task
            : Task.FromResult(History("new-turn"));
        var oldRead = harness.Call("LoadHistoryAndReconcileAsync", false);
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", Guid.NewGuid());
        await harness.Call("VerifyAsync");
        if (unauthorized)
        {
            response.SetException(new ConciergeApiException("unauthorized", "Expired", 401));
        }
        else
        {
            response.SetResult(History("old-turn"));
        }
        await oldRead;

        Assert.Equal("new-turn", Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
        Assert.False(harness.Get<bool>("hasExpired"));
    }

    /// <summary>Prevents a history response from repopulating an ended interaction.</summary>
    [Fact]
    public async Task EndInvalidatesOutstandingHistory()
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        var response = new TaskCompletionSource<ConciergeHistoryResult>();
        harness.Service.History = _ => response.Task;
        var oldRead = harness.Call("LoadHistoryAndReconcileAsync", false);
        await harness.Call("EndInteractionAsync");
        response.SetResult(History("late-turn"));
        await oldRead;
        Assert.Empty(harness.Get<List<ConciergeTranscriptTurn>>("turns"));
        Assert.True(harness.Get<bool>("chatEnded"));
    }

    /// <summary>Rejects an earlier credential's turn completion and failure.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSubmissionCannotChangeANewSession(bool unauthorized)
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        harness.Set("pending", new PendingTurnState("old-turn", "Old", "web", DateTimeOffset.UtcNow));
        var response = new TaskCompletionSource<ConciergeTurnResult>();
        var submitted = false;
        harness.Service.Submission = request =>
        {
            if (submitted) return Task.FromResult(new ConciergeTurnResult(request.TurnId, "Reply", 1));
            submitted = true;
            return response.Task;
        };
        var oldSubmit = harness.Call("SubmitPendingAsync");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", Guid.NewGuid());
        await harness.Call("VerifyAsync");
        if (unauthorized)
        {
            response.SetException(new ConciergeApiException("unauthorized", "Expired", 401));
        }
        else
        {
            response.SetResult(new ConciergeTurnResult("old-turn", "Old reply", 1));
        }
        await oldSubmit;
        Assert.False(harness.Get<bool>("hasExpired"));
        Assert.False(harness.Get<bool>("isWorking"));
        Assert.Null(harness.Get<object?>("turnError"));
    }

    /// <summary>Reconnects the replaced transcript on cancel and disconnects on end.</summary>
    [Fact]
    public async Task CancelRewiresTranscriptAndEndDisconnectsIt()
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        harness.Set("draft", "Keep this");
        var rideFeedCancellation = new CancellationTokenSource();
        harness.Set("rideActivityCancellation", rideFeedCancellation);
        harness.Set("rideActivityCredential", "old");
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Equal(1, harness.JavaScript.Calls.Count(call => call == "conciergeChat.wireTranscript"));
        await harness.Call("EndChatAsync");
        Assert.True(rideFeedCancellation.IsCancellationRequested);
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Contains("conciergeChat.unwireTranscript", harness.JavaScript.Calls);
        await harness.Call("CancelEndChat");
        await harness.WaitUntil(() => harness.Service.RideStreamFromOffset == 0);
        Assert.Equal(0, harness.Service.RideStreamFromOffset);
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Equal(2, harness.JavaScript.Calls.Count(call => call == "conciergeChat.wireTranscript"));
        await harness.Call("EndInteractionAsync");
        await harness.Call("OnAfterRenderAsync", false);
        Assert.Equal(2, harness.JavaScript.Calls.Count(call => call == "conciergeChat.unwireTranscript"));
    }

    [Theory]
    [InlineData("listening")]
    [InlineData("paused")]
    [InlineData("disconnected")]
    public async Task EndVoiceStopsVoiceAndKeepsTheChat(string state)
    {
        await using var harness = new Harness();
        harness.StartListening();
        harness.Set("voiceState", state);
        harness.Set("voiceSessionId", "voice-session");
        harness.Service.History = _ => Task.FromResult(History("latest-voice-turn"));

        await harness.Call("EndVoiceAsync");

        Assert.False(harness.Get<bool>("voiceMode"));
        Assert.Null(harness.Get<string?>("voiceSessionId"));
        Assert.False(harness.Get<bool>("chatEnded"));
        Assert.NotNull(harness.Get<ChatSessionState?>("session"));
        Assert.Contains("conciergeVoice.stop", harness.JavaScript.Calls);
        Assert.Equal("latest-voice-turn", Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
    }

    /// <summary>Does not accept verification or end responses after another sign-in starts.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSessionTransitionCannotReplaceANewSession(bool ending)
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.NewGuid());
        var verification = new TaskCompletionSource<ConciergeSessionResponse>();
        var end = new TaskCompletionSource();
        harness.Service.Verify = _ => verification.Task;
        harness.Service.End = _ => end.Task;
        harness.Set("lastName", "Old");
        harness.Set("roomNumber", "101");
        var oldTransition = harness.Call(ending ? "EndInteractionAsync" : "VerifyAsync");
        await harness.Call("VerifyAgain");
        harness.Service.Verify = null;
        harness.Service.Verification = SessionResponse("new", Guid.NewGuid());
        harness.Set("lastName", "New");
        harness.Set("roomNumber", "102");
        await harness.Call("VerifyAsync");
        verification.SetResult(SessionResponse("stale", Guid.NewGuid()));
        end.SetResult();
        await oldTransition;
        Assert.Equal("new", harness.Get<ChatSessionState>("session").Credential);
        Assert.False(harness.Get<bool>("chatEnded"));
        Assert.False(harness.Get<bool>("isVerifying"));
        Assert.False(harness.Get<bool>("isEnding"));
    }

    /// <summary>Drops legacy unbound recovery even if verification cannot supply an identity.</summary>
    [Fact]
    public async Task UnknownStayIdentityNeverReplaysPending()
    {
        await using var harness = new Harness();
        harness.SetSession("old", Guid.Empty);
        harness.Set("pending", new PendingTurnState("old-turn", "Old", "web", DateTimeOffset.UtcNow));
        harness.Set("draft", "Old draft");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", Guid.Empty);
        await harness.Call("VerifyAsync");
        Assert.Empty(harness.Service.Submissions);
        Assert.Equal("", harness.Get<string>("draft"));
    }

    /// <summary>Reconciliation clears an already committed same-stay retry without resubmitting.</summary>
    [Fact]
    public async Task SameStayCommittedPendingIsNotResubmitted()
    {
        await using var harness = new Harness();
        var stay = Guid.NewGuid();
        harness.SetSession("old", stay);
        harness.Set("pending", new PendingTurnState("committed", "Old", "web", DateTimeOffset.UtcNow));
        harness.Set("draft", "Keep draft");
        await harness.Call("VerifyAgain");
        harness.Set("lastName", "Guest");
        harness.Set("roomNumber", "101");
        harness.Service.Verification = SessionResponse("new", stay);
        harness.Service.History = _ => Task.FromResult(History("committed"));
        await harness.Call("VerifyAsync");
        Assert.Empty(harness.Service.Submissions);
        Assert.Null(harness.Get<object?>("pending"));
        Assert.Equal("Keep draft", harness.Get<string>("draft"));
    }

    /// <summary>Offers same-ID recovery after failed End discards an outstanding reply callback.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedEndKeepsPendingReplyRecoverable(bool replyCompleted, bool unexpectedFailure)
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var pending = new PendingTurnState("pending-id", "Original message", "web", DateTimeOffset.UtcNow);
        harness.Set("pending", pending);
        harness.Set("draft", "Separate draft");
        var response = new TaskCompletionSource<ConciergeTurnResult>();
        harness.Service.Submission = _ => response.Task;
        var submission = harness.Call("SubmitPendingAsync");
        await harness.Call("EndChatAsync");
        harness.Service.End = _ => Task.FromException(unexpectedFailure
            ? new InvalidOperationException("Connection failed")
            : new ConciergeApiException("unavailable", "End unavailable", 503));
        await harness.Call("EndInteractionAsync");
        if (replyCompleted)
        {
            response.SetResult(new ConciergeTurnResult(pending.TurnId, "Reply", 1));
            await submission;
        }

        await harness.Call("CancelEndChat");
        Assert.False(harness.Get<bool>("showEndConfirmation"));
        Assert.False(harness.Get<bool>("isWorking"));
        Assert.False(harness.Get<bool>("isEnding"));
        Assert.Contains("Retry", harness.Get<string>("turnError"));
        Assert.Equal(pending, harness.Get<PendingTurnState>("pending"));
        Assert.Equal("Separate draft", harness.Get<string>("draft"));

        harness.Service.History = _ => Task.FromResult(replyCompleted
            ? History(pending.TurnId)
            : new ConciergeHistoryResult([]));
        var retry = harness.Call("RetryAsync");
        if (!replyCompleted)
        {
            await harness.WaitUntil(() => harness.Service.Submissions.Count == 2);
            Assert.Equal(2, harness.Service.Submissions.Count);
            harness.Service.History = _ => Task.FromResult(History(pending.TurnId));
            response.SetResult(new ConciergeTurnResult(pending.TurnId, "Reply", 1));
            await submission;
        }
        await retry;

        Assert.All(harness.Service.Submissions, request =>
            Assert.Equal(new ConciergeTurnRequest(pending.TurnId, pending.UserMessage, pending.Channel), request));
        Assert.Equal(replyCompleted ? 1 : 2, harness.Service.Submissions.Count);
        Assert.Null(harness.Get<object?>("pending"));
        Assert.Null(harness.Get<object?>("turnError"));
        Assert.False(harness.Get<bool>("isWorking"));
        Assert.Equal("Separate draft", harness.Get<string>("draft"));
        Assert.Equal(pending.TurnId, Assert.Single(harness.Get<List<ConciergeTranscriptTurn>>("turns")).TurnId);
    }

    /// <summary>Renders assistant Markdown while guest and pending guest text stay literal.</summary>
    [Fact]
    public async Task OnlyAssistantRepliesRenderMarkdown()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        harness.Set("turns", new List<ConciergeTranscriptTurn>
        {
            new("turn-1", 1, "web", "**x** <b>y</b>", "**Gym** <b>z</b>", DateTimeOffset.UtcNow)
        });
        harness.Set("pending", new PendingTurnState("turn-2", "**p** <i>q</i>", "web", DateTimeOffset.UtcNow));

        await harness.Call("StateHasChanged");
        var (text, markup) = harness.RenderedContent();

        Assert.Contains("**x** <b>y</b>", text);
        Assert.Contains("**p** <i>q</i>", text);
        Assert.Contains("<p><strong>Gym</strong> &lt;b&gt;z&lt;/b&gt;</p>", markup);
        Assert.DoesNotContain(markup, content => content.Contains("<b>", StringComparison.Ordinal) || content.Contains("**x**", StringComparison.Ordinal) || content.Contains("**p**", StringComparison.Ordinal));
    }

    /// <summary>Places voice actions and status in the header while retaining the chat composer.</summary>
    [Fact]
    public async Task ActiveVoiceControlsStayInHeaderInKeyboardOrderAndComposerRemainsAvailable()
    {
        await using var harness = new Harness();
        harness.StartListening();
        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        var header = elements.First(element => element.Tag == "header");
        var controls = elements
            .Where(element => element.Index > header.Index && element.Index < header.EndIndex)
            .Where(element => element.Tag == "button")
            .ToArray();

        Assert.Equal(4, controls.Length);
        Assert.Equal(
            ["Activity", "Mute microphone", "End voice", "End chat"],
            controls.Select(element => element.Attributes.GetValueOrDefault("aria-label")));
        Assert.Equal("ride-activity-panel", controls[0].Attributes.GetValueOrDefault("aria-controls"));
        // The visible label switches Mute/Unmute, so the name carries state; aria-pressed would double it.
        Assert.False(controls[1].Attributes.ContainsKey("aria-pressed"));
        Assert.All(controls, element => Assert.False(element.Attributes.ContainsKey("tabindex")));
        Assert.Contains(elements, element =>
            element.Tag == "div"
            && element.Index > header.Index && element.Index < header.EndIndex
            && Equals(element.Attributes.GetValueOrDefault("role"), "status")
            && Equals(element.Attributes.GetValueOrDefault("aria-live"), "polite"));
        Assert.Contains(elements, element => element.Tag == "textarea");
        Assert.DoesNotContain(elements, element =>
            element.Tag == "section"
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "Voice conversation"));
        Assert.DoesNotContain(elements, element =>
            Equals(element.Attributes.GetValueOrDefault("aria-label"), "Disconnect voice"));
        // Razor can coalesce static SVGs into markup frames instead of element frames.
        var (_, markup) = harness.RenderedContent();
        Assert.Contains(markup, item =>
            item.Contains("<svg", StringComparison.Ordinal)
            && item.Contains("aria-hidden=\"true\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("disconnected", "Voice stopped.", "Disconnected")]
    [InlineData("disconnected", "Voice service is unavailable.", "Disconnected")]
    [InlineData("paused", "Voice paused while this page was hidden.", "Paused")]
    [InlineData("listening", "Microphone ready.", "Listening")]
    public async Task VoiceStatusShowsOnlyTheConciseState(string state, string diagnostic, string label)
    {
        await using var harness = new Harness();
        harness.StartListening();
        await harness.VoiceState(state, diagnostic, 0);
        await harness.Call("StateHasChanged");

        var (text, markup) = harness.RenderedContent();
        var content = string.Concat(text.Concat(markup));
        Assert.Contains(label, content);
        Assert.DoesNotContain(diagnostic, content);
        Assert.Contains(harness.RenderedElements(), element =>
            element.Tag == "textarea" && !element.Attributes.ContainsKey("disabled"));
    }

    [Fact]
    public async Task MutedVoiceControlNamesTheUnmuteAction()
    {
        await using var harness = new Harness();
        harness.StartListening();
        harness.Set("voiceMuted", true);
        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        var header = elements.First(element => element.Tag == "header");
        var muteButton = Assert.Single(elements, element =>
            element.Tag == "button"
            && element.Index > header.Index && element.Index < header.EndIndex
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "Unmute microphone"));

        Assert.False(muteButton.Attributes.ContainsKey("aria-pressed"));
        Assert.DoesNotContain(elements, element =>
            Equals(element.Attributes.GetValueOrDefault("aria-label"), "Mute microphone"));
    }

    [Theory]
    [InlineData("listening")]
    [InlineData("paused")]
    [InlineData("disconnected")]
    public async Task VoiceToolbarRetainsTheThemePickerAfterTheSessionActions(string state)
    {
        await using var harness = new Harness();
        harness.StartListening();
        harness.Set("voiceState", state);
        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        var header = elements.First(element => element.Tag == "header");
        var themePicker = Assert.Single(elements, element =>
            element.Tag == "select"
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "Color theme"));
        var endChat = Assert.Single(elements, element =>
            element.Tag == "button"
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "End chat"));
        var endVoice = Assert.Single(elements, element =>
            element.Tag == "button"
            && Equals(element.Attributes.GetValueOrDefault("aria-label"), "End voice"));
        Assert.True(endVoice.Index < endChat.Index);
        Assert.Equal(endChat.Index, endVoice.EndIndex);
        Assert.True(themePicker.Index > endChat.EndIndex && themePicker.Index < header.EndIndex);
        Assert.False(themePicker.Attributes.ContainsKey("disabled"));
        var options = elements
            .Where(element => element.Tag == "option"
                && element.Index > themePicker.Index && element.Index < themePicker.EndIndex)
            .Select(element => element.Attributes.GetValueOrDefault("value"));
        Assert.Equal(["system", "light", "dark"], options);
    }

    [Theory]
    [InlineData("paused", "Resume voice")]
    [InlineData("disconnected", "Reconnect voice")]
    public async Task PausedAndDisconnectedVoiceExposeOnlyTheApplicableReconnectAction(string state, string action)
    {
        await using var harness = new Harness();
        harness.StartListening();
        harness.Set("voiceState", state);
        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        var header = elements.First(element => element.Tag == "header");
        var controls = elements
            .Where(element => element.Index > header.Index && element.Index < header.EndIndex)
            .Where(element => element.Tag == "button")
            .ToArray();

        Assert.Equal(4, controls.Length);
        Assert.Equal(
            ["Activity", action, "End voice", "End chat"],
            controls.Select(element => element.Attributes.GetValueOrDefault("aria-label")));
        Assert.Equal("ride-activity-panel", controls[0].Attributes.GetValueOrDefault("aria-controls"));
        Assert.DoesNotContain(controls, element => Equals(element.Attributes.GetValueOrDefault("aria-label"), "Mute microphone"));
    }

    /// <summary>Shows the interruption marker only on interrupted assistant replies.</summary>
    [Fact]
    public async Task InterruptedTranscriptMarkerAppearsOnlyForInterruptedAssistantReplies()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        harness.Set("turns", new List<ConciergeTranscriptTurn>
        {
            new("interrupted-turn", 1, "voice", "Interrupted guest", "Interrupted answer", DateTimeOffset.UtcNow, Interrupted: true),
            new("ordinary-turn", 2, "web", "Ordinary guest", "Ordinary answer", DateTimeOffset.UtcNow)
        });

        await harness.Call("StateHasChanged");
        var (text, markup) = harness.RenderedContent();
        var messages = harness.Get<IEnumerable<object>>("Messages").ToArray();
        var interruptedFlags = messages
            .Select(message => (bool)message.GetType().GetProperty("Interrupted")!.GetValue(message)!)
            .ToArray();
        const string indicator = "<span class=\"interrupted-indicator\">Interrupted</span>";
        var indicatorCount = text.Count(item => item == "Interrupted")
            + markup.Sum(item => item.Split(indicator, StringSplitOptions.None).Length - 1);

        Assert.Equal(new[] { false, true, false, false }, interruptedFlags);
        Assert.Equal(1, indicatorCount);
    }

    /// <summary>Opening ride activity reads one snapshot, leaving later entries unread.</summary>
    [Fact]
    public async Task OpeningRideActivityReadsOnlyTheDisplayedSnapshot()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var rideId = Guid.NewGuid();
        var bookedAt = new DateTimeOffset(2026, 10, 24, 16, 0, 0, TimeSpan.Zero);
        var completedAt = bookedAt.AddMinutes(30);
        var entries = harness.Get<List<RideActivityEntry>>("rideActivityEntries");
        entries.Add(new RideActivityEntry(
            rideId, 1, "Booked", bookedAt, "Hotel ZGY", "Museum", bookedAt.AddHours(1)));
        entries.Add(new RideActivityEntry(
            rideId, 2, "DriverAssigned", bookedAt.AddMinutes(10), "Hotel ZGY", "Museum",
            bookedAt.AddHours(1), "Jordan Lee", "Black sedan", "ZGY 204"));
        entries.Add(new RideActivityEntry(
            rideId, 3, "Completed", completedAt, "Hotel ZGY", "Museum", bookedAt.AddHours(1)));
        entries.Add(new RideActivityEntry(
            rideId, 4, "PickedUp", completedAt.AddMinutes(5), "Hotel ZGY", "Museum", bookedAt.AddHours(1)));
        entries.Add(new RideActivityEntry(
            rideId, 5, "Cancelled", completedAt.AddMinutes(10), "Hotel ZGY", "Museum", bookedAt.AddHours(1)));
        harness.Service.RideSnapshot = _ => Task.FromResult(new ConciergeRideActivitySnapshot(
            new ConciergeRideReadState(harness.Service.RideReadOffset, 0, entries.Count),
            entries.Select((entry, index) => new ConciergeRideActivityItem(index, entry)).ToArray()));
        harness.Set("rideActivityCancellation", new CancellationTokenSource());

        await harness.Call("ToggleActivity");
        var readKeys = harness.Get<HashSet<string>>("rideActivityReadEntryKeys");
        Assert.Equal(5, readKeys.Count);
        Assert.Equal(5, harness.Service.MarkedRideSnapshotOffset);

        var lateEntry = new RideActivityEntry(
            rideId, 6, "Booked", completedAt.AddMinutes(15), "Hotel ZGY", "Museum", bookedAt.AddHours(1));
        var window = harness.Get<ConciergeRideActivityWindow?>("rideActivityWindow")!;
        Assert.True(window.Apply(new ConciergeRideActivityItem(5, lateEntry)));
        await harness.Call("SynchronizeRideActivityEntries");
        await harness.Call("RefreshRideActivityReadKeys");
        Assert.DoesNotContain($"{rideId:N}:6", readKeys);
        Assert.Equal(1, harness.Get<int>("UnreadRideActivityCount"));

        await harness.Call("StateHasChanged");
        var (text, markup) = harness.RenderedContent();
        var renderedText = string.Concat(text.Concat(markup));
        Assert.True(renderedText.IndexOf("Completed", StringComparison.Ordinal)
            < renderedText.IndexOf("Driver assigned", StringComparison.Ordinal));
        Assert.True(renderedText.IndexOf("Driver assigned", StringComparison.Ordinal)
            < renderedText.LastIndexOf("Booked", StringComparison.Ordinal));
        Assert.Contains("Picked up", renderedText);
        Assert.Contains("Cancelled", renderedText);
        Assert.Contains("Jordan Lee", text);
        Assert.Contains("Black sedan", text);
        Assert.Contains("ZGY 204", text);
        Assert.Contains("12:30 PM", string.Concat(text));
        Assert.Contains("Updates for your stay · Miami time (ET)", renderedText);
        Assert.DoesNotContain("YOUR RIDES", renderedText);
        Assert.DoesNotContain(text, item => item.Contains("New", StringComparison.Ordinal));
        Assert.DoesNotContain(text, item => item.Contains("Close", StringComparison.Ordinal));
        Assert.Contains("Unread", renderedText);
        Assert.Contains(harness.RenderedElements(), element =>
            Equals(element.Attributes.GetValueOrDefault("aria-label"), "Close activity"));
    }

    [Fact]
    public async Task EmptyRideActivityUsesApprovedCopy()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());

        await harness.Call("StateHasChanged");

        var (text, markup) = harness.RenderedContent();
        var renderedText = string.Concat(text.Concat(markup));
        Assert.Contains("No activity yet", renderedText);
        Assert.Contains("Ride updates will appear here when you book a ride with the concierge.", renderedText);
    }

    [Fact]
    public async Task ActivityButtonIsFirstAndCapsItsUnreadBadge()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var rideId = Guid.NewGuid();
        var entries = Enumerable.Range(1, 12)
            .Select(sequence => RideEntry(rideId, sequence, DateTimeOffset.UtcNow.AddMinutes(-sequence)))
            .ToArray();
        harness.Get<List<RideActivityEntry>>("rideActivityEntries").AddRange(entries);
        harness.Set("rideActivityWindow", new ConciergeRideActivityWindow(
            new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(0, 0, entries.Length),
                entries.Select((entry, offset) => new ConciergeRideActivityItem(offset, entry)).ToArray())));
        Assert.Equal(12, harness.Get<int>("UnreadRideActivityCount"));

        await harness.Call("StateHasChanged");

        var elements = harness.RenderedElements();
        Assert.Equal("Activity, 12 unread ride updates", harness.Get<string>("ActivityButtonAccessibleName"));
        var buttonNames = elements.Where(element => element.Tag == "button")
            .Select(element => element.Attributes.GetValueOrDefault("aria-label")?.ToString())
            .ToArray();
        Assert.Contains("Activity, 12 unread ride updates", buttonNames);
        var activity = Assert.Single(elements, element =>
            element.Tag == "button"
            && element.Attributes.GetValueOrDefault("aria-label") is string name
            && name.StartsWith("Activity", StringComparison.Ordinal));
        Assert.Equal("Activity, 12 unread ride updates", activity.Attributes.GetValueOrDefault("aria-label"));
        var endChat = Assert.Single(elements, element =>
            Equals(element.Attributes.GetValueOrDefault("aria-label"), "End chat"));
        Assert.True(activity.Index < endChat.Index);
        var (_, markup) = harness.RenderedContent();
        Assert.Contains(elements, element => element.Tag == "span"
            && Equals(element.Attributes.GetValueOrDefault("class"), "activity-unread-badge"));
        Assert.Contains("9+", string.Concat(harness.RenderedContent().Text));
    }

    [Fact]
    public async Task ActivityDrawerUsesOpenStateMotionAndReducedMotionOverride()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        await harness.Call("StateHasChanged");
        var closedDrawer = Assert.Single(harness.RenderedElements(), element => element.Tag == "aside");
        var closedClass = Assert.IsType<string>(closedDrawer.Attributes.GetValueOrDefault("class"));
        Assert.StartsWith("ride-activity-drawer", closedClass, StringComparison.Ordinal);
        Assert.DoesNotContain("is-open", closedClass, StringComparison.Ordinal);

        await harness.Call("ToggleActivity");
        Assert.Contains(harness.RenderedElements(), element =>
            element.Tag == "aside"
            && Equals(element.Attributes.GetValueOrDefault("class"), "ride-activity-drawer is-open"));

        var css = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "Concierge.Web", "Components", "Shared", "RideActivityPanel.razor.css"));
        Assert.Contains("transition:", css, StringComparison.Ordinal);
        Assert.Contains(".ride-activity-drawer.is-open", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
        Assert.Contains("transition: none", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 700px)", css, StringComparison.Ordinal);
        Assert.Contains("position: absolute", css, StringComparison.Ordinal);
        // Scoped .razor.css only reaches the browser through the generated bundle link.
        var appShell = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "Concierge.Web", "Components", "App.razor"));
        Assert.Contains("@Assets[\"Concierge.Web.styles.css\"]", appShell, StringComparison.Ordinal);
        var appCss = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "src", "Concierge.Web", "wwwroot", "css", "app.css"));
        Assert.Contains(".voice-site-header .voice-header-controls.has-voice-mode", appCss, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(3, minmax(0, 1fr))", appCss, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EscapeBubblesFromActivityTriggerToPageShellAndClosesDrawer()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        await harness.Call("ToggleActivity");
        Assert.True(harness.Get<bool>("activityOpen"));

        var elements = harness.RenderedElements();
        var pageShell = Assert.Single(elements, element =>
            element.Tag == "div"
            && Equals(element.Attributes.GetValueOrDefault("class"), "page-shell"));
        var pageShellKeyDown = Assert.IsType<Func<KeyboardEventArgs, Task>>(
            pageShell.Attributes.GetValueOrDefault("onkeydown"));
        Assert.Contains(elements, element =>
            element.Tag == "button"
            && Equals(element.Attributes.GetValueOrDefault("aria-controls"), "ride-activity-panel"));

        // The trigger retains focus after opening, so its Escape keydown bubbles to the page shell.
        await harness.DispatchKeyDown(pageShellKeyDown, new KeyboardEventArgs { Key = "Escape" });

        Assert.False(harness.Get<bool>("activityOpen"));
    }

    [Fact]
    public void ActivityTimestampsUseTimeTodayAndDateForEarlierMiamiDays()
    {
        var panelType = typeof(Home).Assembly.GetType("Concierge.Web.Components.Shared.RideActivityPanel")!;
        var formatter = panelType.GetMethod("FormatTimestamp", BindingFlags.Static | BindingFlags.NonPublic)!;
        var miami = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var nowInMiami = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, miami);

        var today = (string)formatter.Invoke(null, [nowInMiami])!;
        var yesterday = (string)formatter.Invoke(null, [nowInMiami.AddDays(-1)])!;

        Assert.Equal(nowInMiami.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture), today);
        Assert.Equal(nowInMiami.AddDays(-1).ToString("MMM d, h:mm tt",
            System.Globalization.CultureInfo.InvariantCulture), yesterday);
        Assert.DoesNotContain(",", today);
        Assert.Contains(",", yesterday);
    }

    [Fact]
    public async Task EndingChatAwaitsRideStreamCancellationCleanup()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Service.RideStream = (_, _, cancellationToken) =>
            WaitingRideItems(cancellationToken, started, cleanedUp);

        await harness.Call("StartRideActivityFeedAsync", 0, "current");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var activeStream = harness.Get<Task?>("rideActivityStreamTask")!;

        await harness.Call("EndChatAsync");

        Assert.True(activeStream.IsCompleted);
        await cleanedUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(harness.Get<Task?>("rideActivityStreamTask"));
    }

    /// <summary>Starts live updates at the snapshot end and keeps the bounded, deduplicated list.</summary>
    [Fact]
    public async Task RideActivityFeedStartsAtSnapshotOffsetAndKeepsOnlyNewestTwentyFive()
    {
        await using var harness = new Harness();
        harness.SetSession("current", Guid.NewGuid());

        var rideId = Guid.NewGuid();
        var retained = Enumerable.Range(0, 30)
            .Select(index => new ConciergeRideActivityItem(
                index,
                RideEntry(rideId, index + 1, DateTimeOffset.UnixEpoch.AddMinutes(index))))
            .ToArray();
        harness.Service.RideSnapshot = _ => Task.FromResult(new ConciergeRideActivitySnapshot(
            new ConciergeRideReadState(0, 0, 30),
            retained.TakeLast(25).ToArray()));

        var arrivals = Enumerable.Range(30, 30)
            .Select(index => new ConciergeRideActivityItem(
                index,
                RideEntry(rideId, index + 1, DateTimeOffset.UnixEpoch.AddMinutes(index))))
            .ToList();
        // A publisher retry uses a new stream offset but the same ride/sequence application key.
        arrivals.Add(new ConciergeRideActivityItem(
            60,
            RideEntry(rideId, 61, DateTimeOffset.UnixEpoch.AddMinutes(60))));
        arrivals.Add(new ConciergeRideActivityItem(
            61,
            RideEntry(rideId, 61, DateTimeOffset.UnixEpoch.AddMinutes(60))));
        harness.Service.RideStream = (_, _, _) => RideItems(arrivals);

        await harness.Call("StartRideActivityFeedAsync", 0, "current");
        await harness.WaitUntil(() => harness.Get<Task?>("rideActivityStreamTask") is null);

        Assert.Equal(30, harness.Service.RideStreamFromOffset);
        var displayed = harness.Get<List<RideActivityEntry>>("rideActivityEntries");
        Assert.Equal(25, displayed.Count);
        Assert.Contains(displayed, entry => entry.Sequence == 61);
        Assert.DoesNotContain(displayed, entry => entry.Sequence == 35);
        Assert.Single(displayed, entry => entry.Sequence == 61);
    }

    /// <summary>Shows Reconnect when the relay closes while the browser's report was lost.</summary>
    [Fact]
    public async Task ServerRelayClosureDisconnectsVoiceWhenTheBrowserReportWasLost()
    {
        using var logs = new CapturedLogs();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        await using var harness = new Harness();
        harness.Set("Logger", loggerFactory.CreateLogger<Home>());
        var connection = harness.StartListening();
        await harness.VoiceSession(connection.Session.Id.ToString());

        harness.Voice.Close(connection);
        await harness.WaitUntil(() => harness.Get<string>("voiceState") == "disconnected");

        Assert.Equal("Voice disconnected.", harness.Get<string>("voiceMessage"));
        await harness.Call("StateHasChanged");
        var (text, markup) = harness.RenderedContent();
        var content = text.Concat(markup).ToList();
        Assert.Contains(content, item => item.Contains("Reconnect", StringComparison.Ordinal));
        Assert.Contains(content, item => item.Contains("Disconnected", StringComparison.Ordinal));
        Assert.DoesNotContain(content, item => item.Contains("Voice disconnected.", StringComparison.Ordinal));
        Assert.Contains(content, item => item.Contains("End chat", StringComparison.Ordinal));
        Assert.Contains(logs.Entries, entry =>
            entry.Message.StartsWith("Voice relay closure applied by server", StringComparison.Ordinal)
            && Equals(entry.Values.GetValueOrDefault("VoiceState"), "listening")
            && Equals(entry.Values.GetValueOrDefault("ConnectionNumber"), 1));
        Assert.DoesNotContain("conciergeVoice.stop", harness.JavaScript.Calls);

        // The browser's late report of the same closure still applies cleanly.
        await harness.VoiceState("disconnected", "Voice disconnected.", 4000);
        Assert.Equal("disconnected", harness.Get<string>("voiceState"));
    }

    /// <summary>Ignores closures for a voice session the page no longer shows.</summary>
    [Fact]
    public async Task ServerRelayClosureForAnotherVoiceSessionIsIgnored()
    {
        await using var harness = new Harness();
        var stale = harness.StartListening();
        await harness.VoiceSession(stale.Session.Id.ToString());
        var current = harness.Voice.Open(new ConciergeSessionContext(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)), null);
        await harness.VoiceSession(current.Session.Id.ToString());

        harness.Voice.Close(stale);
        await harness.Call("ApplyVoiceRelayClosedAsync", stale.Session.Id.ToString(), 1);

        Assert.Equal("listening", harness.Get<string>("voiceState"));
        Assert.Null(harness.Get<string?>("voiceMessage"));
    }

    /// <summary>Keeps the browser's more specific disconnect message.</summary>
    [Fact]
    public async Task ServerRelayClosureDoesNotOverwriteABrowserDisconnect()
    {
        await using var harness = new Harness();
        var connection = harness.StartListening();
        await harness.VoiceSession(connection.Session.Id.ToString());
        await harness.VoiceState("disconnected", "Voice service is unavailable.", 1011);

        harness.Voice.Close(connection);
        await harness.Call("ApplyVoiceRelayClosedAsync", connection.Session.Id.ToString(), 1);

        Assert.Equal("disconnected", harness.Get<string>("voiceState"));
        Assert.Equal("Voice service is unavailable.", harness.Get<string>("voiceMessage"));
    }

    private static ConciergeSessionResponse SessionResponse(string credential, Guid stay)
        => new(credential, DateTimeOffset.UtcNow.AddHours(1), stay);

    private static ConciergeHistoryResult History(string id) =>
        new([new ConciergeTranscriptTurn(id, 1, "web", "Guest", "Reply", DateTimeOffset.UtcNow)]);

    private static RideActivityEntry RideEntry(Guid rideId, int sequence, DateTimeOffset changedAt) =>
        new(rideId, sequence, "Booked", changedAt, "Hotel", "Museum", changedAt.AddHours(1));

    private static async IAsyncEnumerable<ConciergeRideActivityItem> RideItems(
        IEnumerable<ConciergeRideActivityItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<ConciergeRideActivityItem> WaitingRideItems(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        TaskCompletionSource started,
        TaskCompletionSource cleanedUp)
    {
        try
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        finally
        {
            cleanedUp.TrySetResult();
        }

        yield break;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Concierge.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Concierge.slnx from the test output directory.");
    }

#pragma warning disable BL0006 // The fixture intentionally uses Blazor's renderer instead of a new test dependency.
    private sealed class Harness : IAsyncDisposable
    {
        private readonly Home home = new();
        private readonly TestRenderer renderer = new();
        private readonly int componentId;
        public FakeService Service { get; } = new();
        public FakeJavaScript JavaScript { get; } = new();
        public VoiceSessionRegistry Voice { get; } = new(TimeProvider.System);

        /// <summary>Attaches Home without triggering implicit browser callbacks.</summary>
        public Harness()
        {
            Set("Concierge", Service);
            Set("JavaScript", JavaScript);
            Set("Logger", NullLogger<Home>.Instance);
            Set("VoiceSessions", Voice);
            Set("ready", true);
            componentId = renderer.Attach(home);
        }

        /// <summary>Reads encoded text and raw markup from Home's current render tree, including child components.</summary>
        public (List<string> Text, List<string> Markup) RenderedContent()
        {
            var frames = renderer.Frames(componentId);
            return (
                frames.Where(frame => frame.FrameType == RenderTreeFrameType.Text).Select(frame => frame.TextContent).ToList(),
                frames.Where(frame => frame.FrameType == RenderTreeFrameType.Markup).Select(frame => frame.MarkupContent).ToList());
        }

        /// <summary>Returns elements in document order with their rendered attributes and text.</summary>
        public List<RenderedElement> RenderedElements()
        {
            var frames = renderer.Frames(componentId);
            var elements = new List<RenderedElement>();
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (frame.FrameType != RenderTreeFrameType.Element)
                {
                    continue;
                }

                var endIndex = index + frame.ElementSubtreeLength;
                var attributes = new Dictionary<string, object>();
                for (var attributeIndex = index + 1;
                     attributeIndex < frames.Count
                     && frames[attributeIndex].FrameType == RenderTreeFrameType.Attribute;
                     attributeIndex++)
                {
                    var attribute = frames[attributeIndex];
                    attributes[attribute.AttributeName] = attribute.AttributeValue;
                }
                elements.Add(new RenderedElement(frame.ElementName, attributes, index, endIndex));
            }

            return elements;
        }

        /// <summary>Seeds a verified session for the test.</summary>
        public void SetSession(string credential, Guid stay)
        {
            var session = new ChatSessionState { Credential = credential, StayId = stay, ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1) };
            Set("session", session);
        }

        public sealed record RenderedElement(
            string Tag,
            Dictionary<string, object> Attributes,
            int Index,
            int EndIndex);

        /// <summary>Sets private component state without adding production test surfaces.</summary>
        public void Set(string name, object value)
        {
            var property = typeof(Home).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (property?.SetMethod is not null)
            {
                property.SetValue(home, value);
                return;
            }

            if (property is not null)
            {
                var transition = name switch
                {
                    "session" => ("Restore", new[] { value, (object)DateTimeOffset.UtcNow }),
                    "pending" => ("StartTurn", new[] { value, (object)false }),
                    "draft" => ("SetDraft", new[] { value }),
                    "turns" => ("ReconcileHistory", new[] { value }),
                    _ => throw new InvalidOperationException($"No state transition is available for '{name}'.")
                };
                var modelField = typeof(Home).GetField("model", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var model = modelField.GetValue(home)!;
                var modelTransition = model.GetType().GetMethod(transition.Item1)!;
                modelField.SetValue(home, modelTransition.Invoke(model, transition.Item2));
                return;
            }

            typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(home, value);
        }

        /// <summary>Reads private state for focused assertions.</summary>
        public T Get<T>(string name)
        {
            var property = typeof(Home).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic);
            var value = property is not null
                ? property.GetValue(home)
                : typeof(Home).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(home);
            return (T)value!;
        }

        /// <summary>Runs a component event on its renderer dispatcher with a bounded timeout.</summary>
        public Task Call(string name, params object[] args) =>
            renderer.Dispatcher.InvokeAsync(async () =>
            {
                var result = typeof(Home).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(home, args);
                if (result is Task task) await task;
            }).WaitAsync(TimeSpan.FromSeconds(10));

        public Task VoiceEndRequested() => renderer.Dispatcher.InvokeAsync(home.OnVoiceEndRequested);

        public Task DispatchKeyDown(Func<KeyboardEventArgs, Task> callback, KeyboardEventArgs args) =>
            renderer.Dispatcher.InvokeAsync(() => callback(args));

        /// <summary>Seeds a verified chat in voice mode with an open relay connection.</summary>
        public VoiceConnection StartListening()
        {
            SetSession("credential", Guid.NewGuid());
            Set("voiceMode", true);
            Set("voiceState", "listening");
            return Voice.Open(new ConciergeSessionContext(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(1)), null);
        }

        public Task VoiceState(string state, string? message, int closeCode) =>
            renderer.Dispatcher.InvokeAsync(() => home.OnVoiceState(state, message, closeCode));

        public Task VoiceSession(string id) => renderer.Dispatcher.InvokeAsync(() => home.OnVoiceSession(id));

        /// <summary>Waits for work queued onto the component dispatcher from another thread.</summary>
        public async Task WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!await renderer.Dispatcher.InvokeAsync(condition))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met.");
                await Task.Delay(10);
            }
        }

        /// <summary>Disposes the attached component and renderer.</summary>
        public ValueTask DisposeAsync() => renderer.DisposeAsync();
    }

    // Suppress automatic after-render callbacks so each interop lifecycle step is deterministic.
    private sealed class TestRenderer() : Renderer(new EmptyServices(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        /// <summary>Attaches the component without an initial render.</summary>
        public int Attach(IComponent component) => AssignRootComponentId(component);
        /// <summary>Flattens a component's current frames and its descendants' frames.</summary>
        public List<RenderTreeFrame> Frames(int componentId)
        {
            var current = GetCurrentRenderTreeFrames(componentId);
            var frames = new List<RenderTreeFrame>();
            for (var index = 0; index < current.Count; index++)
            {
                var frame = current.Array[index];
                frames.Add(frame);
                if (frame.FrameType == RenderTreeFrameType.Component) frames.AddRange(Frames(frame.ComponentId));
            }
            return frames;
        }
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.FromCanceled(new CancellationToken(true));
    }
#pragma warning restore BL0006

    private sealed class EmptyServices : IServiceProvider
    {
        /// <summary>Provides no services; test dependencies are injected explicitly.</summary>
        public object? GetService(Type serviceType) => null;
    }

    private sealed class FakeJavaScript : IJSRuntime
    {
        public List<string> Calls { get; } = [];
        /// <summary>Records browser interop with default cancellation.</summary>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        /// <summary>Records browser interop and returns a deterministic scroll position.</summary>
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Calls.Add(identifier);
            return ValueTask.FromResult(identifier == "conciergeChat.isNearBottom" ? (TValue)(object)true : default!);
        }
    }

    private sealed class FakeService : IConciergeService
    {
        public ConciergeSessionResponse Verification { get; set; } = SessionResponse("new", Guid.NewGuid());
        public Func<ConciergeVerificationRequest, Task<ConciergeSessionResponse>>? Verify { get; set; }
        public Func<string?, Task> End { get; set; } = _ => Task.CompletedTask;
        public Func<string?, Task<ConciergeHistoryResult>> History { get; set; } = _ => Task.FromResult(new ConciergeHistoryResult([]));
        public Func<string?, Task<ConciergeRideActivitySnapshot>> RideSnapshot { get; set; } =
            _ => Task.FromResult(new ConciergeRideActivitySnapshot(
                new ConciergeRideReadState(0, 0, 0),
                []));
        public long MarkedRideSnapshotOffset { get; private set; } = -1;
        public long RideReadOffset { get; private set; }
        public Func<string?, long, CancellationToken, IAsyncEnumerable<ConciergeRideActivityItem>> RideStream { get; set; } =
            (_, _, _) => RideItems([]);
        public long RideStreamFromOffset { get; private set; } = -1;
        public Func<ConciergeTurnRequest, Task<ConciergeTurnResult>> Submission { get; set; } =
            request => Task.FromResult(new ConciergeTurnResult(request.TurnId, "Reply", 1));
        public List<ConciergeTurnRequest> Submissions { get; } = [];
        /// <summary>Returns the arranged verification response.</summary>
        public Task<ConciergeSessionResponse> VerifyAsync(ConciergeVerificationRequest request, CancellationToken cancellationToken = default) =>
            Verify?.Invoke(request) ?? Task.FromResult(Verification);
        /// <summary>Returns the arranged history task for this credential.</summary>
        public Task<ConciergeHistoryResult> ReadHistoryAsync(string? bearerCredential, CancellationToken cancellationToken = default) =>
            History(bearerCredential);
        /// <summary>Ride activity is outside these component tests.</summary>
        public Task<ConciergeRideActivitySnapshot> ReadRideActivitySnapshotAsync(
            string? bearerCredential, CancellationToken cancellationToken = default) =>
            RideSnapshot(bearerCredential);
        /// <summary>Returns the arranged ride stream and records the exclusive subscription offset.</summary>
        public IAsyncEnumerable<ConciergeRideActivityItem> StreamRideActivityAsync(
            string? bearerCredential, long fromOffset, CancellationToken cancellationToken = default)
        {
            RideStreamFromOffset = fromOffset;
            return RideStream(bearerCredential, fromOffset, cancellationToken);
        }
        /// <summary>Advances the arranged read cursor through the requested snapshot.</summary>
        public Task<ConciergeRideReadState> MarkRideActivitySnapshotReadAsync(
            string? bearerCredential, long snapshotOffset, CancellationToken cancellationToken = default)
        {
            MarkedRideSnapshotOffset = snapshotOffset;
            RideReadOffset = Math.Max(RideReadOffset, snapshotOffset);
            return Task.FromResult(new ConciergeRideReadState(RideReadOffset, 0, snapshotOffset));
        }
        /// <summary>Records the retry identity and returns the arranged turn task.</summary>
        public Task<ConciergeTurnResult> SubmitTurnAsync(string? bearerCredential, ConciergeTurnRequest request, CancellationToken cancellationToken = default)
        {
            Submissions.Add(request);
            return Submission(request);
        }
        /// <summary>Completes the interaction without external services.</summary>
        public Task EndAsync(string? bearerCredential, CancellationToken cancellationToken = default) => End(bearerCredential);
        /// <summary>Voice is outside these component tests.</summary>
        public Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(string? bearerCredential, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        /// <summary>Voice is outside these component tests.</summary>
        public Task<ConciergeTurnResult> SubmitVoiceTurnAsync(
            ConciergeSessionContext session,
            string turnId,
            string userMessage,
            CancellationToken cancellationToken = default,
            bool enableEndChatFunction = false) =>
            throw new NotSupportedException();
        /// <summary>Voice is outside these component tests.</summary>
        public Task MarkVoiceTurnInterruptedAsync(
            ConciergeSessionContext session,
            string turnId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
