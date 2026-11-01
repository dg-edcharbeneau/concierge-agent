using System.Net;
using System.Text;
using Concierge.Contracts;
using Concierge.Web.Services;
using Concierge.Web.Voice;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Concierge.Web.Components.Pages;

public partial class Home : ComponentBase, IAsyncDisposable
{
    private static readonly TimeZoneInfo HotelTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly string[] Suggestions =
    [
        "What time is breakfast?",
        "How do I connect to Wi-Fi?",
        "What amenities are available?",
        "Can you recommend a nearby restaurant?"
    ];

    private readonly List<RideActivityEntry> rideActivityEntries = [];
    private readonly HashSet<string> rideActivityReadEntryKeys = new(StringComparer.Ordinal);
    private ConciergeRideActivityWindow? rideActivityWindow;
    private CancellationTokenSource? rideActivityCancellation;
    private Task? rideActivityStreamTask;
    private string? rideActivityCredential;

    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    [Inject]
    private IConciergeService Concierge { get; set; } = default!;

    [Inject]
    private ILogger<Home> Logger { get; set; } = default!;

    [Inject]
    private VoiceSessionRegistry VoiceSessions { get; set; } = default!;

    private ChatSessionModel model = ChatSessionModel.Initial;
    private ElementReference transcriptElement;
    private DotNetObjectReference<Home>? scrollReference;
    private ChatSessionState? session => model.Session;
    private PendingTurnState? pending => model.Pending;
    private string draft
    {
        get => model.Draft;
        set => model = model.SetDraft(value);
    }
    private List<ConciergeTranscriptTurn> turns => model.Turns;
    private bool hasExpired => model.HasExpired;
    private bool showExpiredStrip => model.ShowExpiredStrip;
    private bool isEnding => model.IsEnding;
    private bool showEndConfirmation => model.ShowEndConfirmation;
    private bool chatEnded => model.ChatEnded;
    private string? turnError => model.TurnError;
    private string? endError => model.EndError;
    private int sessionVersion => model.Version;
    private string lastName = string.Empty;
    private string roomNumber = string.Empty;
    private string theme = "system";
    private string selectedSuggestion = string.Empty;
    private string? verificationError;
    private bool ready;
    private bool isVerifying;
    private bool isWorking;
    private bool suggestionsExpanded;
    private bool showNewMessage;
    private bool transcriptNearBottom = true;
    private bool scrollAfterRender;
    private bool activityOpen;
    private bool rideActivityPaused;
    private bool rideActivityReconnecting;
    private string? rideActivityError;
    private string? wiredCredential;
    private int wiredVersion = -1;
    private bool disposed;
    private bool voiceMode;
    private bool voiceMuted;
    private bool voiceIdleWarning;
    private string? voiceEndReason;
    private bool chatEndedForInactivity;
    private string voiceState = "disconnected";
    private string? voiceMessage;
    private string? voiceSessionId;
    private DotNetObjectReference<Home>? voiceReference;
    private IDisposable? voiceClosedSubscription;
    private IDisposable? voiceTurnSubscription;
    private readonly Dictionary<string, VoiceTurnProgress> voicePendingTranscripts = new(StringComparer.Ordinal);
    private HistoryRefreshBatch? historyRefreshBatch;

    private sealed class HistoryRefreshBatch(int sessionVersion, bool replayPending)
    {
        public int SessionVersion { get; } = sessionVersion;
        public bool Requested { get; set; } = true;
        public bool ReplayPending { get; set; } = replayPending;
        public Task? RefreshTask { get; set; }
    }

    private bool IsCurrentSession(int version) => !disposed && model.IsCurrent(version);
    private int UnreadRideActivityCount =>
        rideActivityWindow?.UnreadCount
        ?? rideActivityEntries.Count(entry => !rideActivityReadEntryKeys.Contains(RideActivityEntryKey(entry)));
    private string UnreadRideActivityBadge => UnreadRideActivityCount > 9
        ? "9+"
        : UnreadRideActivityCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string ActivityButtonAccessibleName => UnreadRideActivityCount switch
    {
        0 => "Activity",
        1 => "Activity, 1 unread ride update",
        _ => $"Activity, {UnreadRideActivityCount} unread ride updates",
    };

    private bool TranscriptVisible =>
        ready && session?.Credential is not null && !showEndConfirmation && !chatEnded;

    /// <summary>Keeps the header control group identical outside the voice chat, including the end-of-chat views.</summary>
    private string HeaderControlsClass =>
        voiceMode && !showEndConfirmation && !chatEnded
            ? "voice-header-controls has-voice-mode"
            : "voice-header-controls";

    private bool HasConversation => turns.Count > 0 || pending is not null || voicePendingTranscripts.Count > 0;

    private IEnumerable<ChatMessage> Messages
    {
        get
        {
            var messages = turns.SelectMany(turn =>
            {
                var timestamp = TimeZoneInfo.ConvertTime(turn.OccurredAtUtc, HotelTimeZone);
                var day = DateOnly.FromDateTime(timestamp.DateTime);
                return new[]
                {
                    new ChatMessage(turn.TurnId, turn.GuestMessage, true, timestamp, day),
                    new ChatMessage(turn.TurnId, turn.AssistantMessage, false, timestamp, day, turn.Interrupted)
                };
            }).ToList();

            if (pending is not null && turns.All(turn => turn.TurnId != pending.TurnId))
            {
                var timestamp = TimeZoneInfo.ConvertTime(pending.SubmittedAtUtc, HotelTimeZone);
                messages.Add(new ChatMessage(
                    pending.TurnId,
                    pending.UserMessage,
                    true,
                    timestamp,
                    DateOnly.FromDateTime(timestamp.DateTime)));
            }

            foreach (var progress in voicePendingTranscripts.Values
                .Where(progress => turns.All(turn => turn.TurnId != progress.TurnId))
                .OrderBy(progress => progress.SubmittedAtUtc))
            {
                var timestamp = TimeZoneInfo.ConvertTime(progress.SubmittedAtUtc, HotelTimeZone);
                messages.Add(new ChatMessage(progress.TurnId, progress.UserMessage, true,
                    timestamp, DateOnly.FromDateTime(timestamp.DateTime)));
            }

            return messages;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var currentVersion = sessionVersion;
        if (firstRender)
        {
            theme = await JavaScript.InvokeAsync<string>("conciergeChat.loadTheme");
            if (!IsCurrentSession(currentVersion)) return;
            await JavaScript.InvokeVoidAsync("conciergeChat.applyTheme", theme);
            if (!IsCurrentSession(currentVersion)) return;
            var storedSession = await JavaScript.InvokeAsync<ChatSessionState?>("conciergeChat.loadSession");
            if (!IsCurrentSession(currentVersion)) return;
            model = model.Restore(storedSession, DateTimeOffset.UtcNow);
            if (storedSession is not null)
            {
                if (string.IsNullOrWhiteSpace(storedSession.Credential))
                {
                    await JavaScript.InvokeVoidAsync("conciergeChat.clearSession");
                    if (!IsCurrentSession(currentVersion)) return;
                }
            }

            isWorking = pending is not null && !hasExpired;
            ready = true;
            StateHasChanged();

            if (session?.Credential is not null && !hasExpired)
            {
                await LoadHistoryAndReconcileAsync(replayPending: true);
            }

            return;
        }

        if (!TranscriptVisible)
        {
            if (wiredCredential is not null)
            {
                wiredCredential = null;
                await JavaScript.InvokeVoidAsync("conciergeChat.unwireTranscript");
            }
            return;
        }

        if (scrollAfterRender)
        {
            scrollAfterRender = false;
            await JavaScript.InvokeVoidAsync("conciergeChat.scrollToBottom", transcriptElement);
            if (!IsCurrentSession(currentVersion) || !TranscriptVisible) return;
            showNewMessage = false;
            transcriptNearBottom = true;
        }

        if (wiredVersion != currentVersion
            || !StringComparer.Ordinal.Equals(wiredCredential, session!.Credential))
        {
            scrollReference ??= DotNetObjectReference.Create(this);
            wiredCredential = session!.Credential;
            wiredVersion = currentVersion;
            await JavaScript.InvokeVoidAsync(
                "conciergeChat.wireTranscript",
                transcriptElement,
                scrollReference,
                currentVersion);
        }
    }

    private async Task ChangeThemeAsync(ChangeEventArgs args)
    {
        theme = args.Value?.ToString() is "light" or "dark" ? args.Value!.ToString()! : "system";
        await JavaScript.InvokeVoidAsync("conciergeChat.applyTheme", theme);
    }

    private async Task VerifyAsync()
    {
        if (isVerifying) return;
        var currentVersion = sessionVersion;
        verificationError = null;
        if (string.IsNullOrWhiteSpace(lastName) || string.IsNullOrWhiteSpace(roomNumber))
        {
            verificationError = "Enter your last name and room number to continue.";
            return;
        }

        isVerifying = true;
        try
        {
            var result = await Concierge.VerifyAsync(
                new ConciergeVerificationRequest(lastName.Trim(), roomNumber.Trim()));
            if (!IsCurrentSession(currentVersion)) return;
            if (string.IsNullOrWhiteSpace(result.Credential))
            {
                verificationError = "Verification could not be completed. Please try again.";
                return;
            }

            model = model.VerificationSucceeded(result, DateTimeOffset.UtcNow);
            currentVersion = sessionVersion;
            await ResetRideActivityAsync();
            lastName = string.Empty;
            roomNumber = string.Empty;
            suggestionsExpanded = false;
            selectedSuggestion = string.Empty;
            isWorking = false;
            showNewMessage = false;
            transcriptNearBottom = true;
            await PersistSessionAsync();
            if (!IsCurrentSession(currentVersion)) return;
            StateHasChanged();

            if (!hasExpired)
            {
                await LoadHistoryAndReconcileAsync(replayPending: true);
            }
        }
        catch (ConciergeApiException exception)
        {
            if (!IsCurrentSession(currentVersion)) return;
            verificationError = ApiMessage(
                exception,
                "We could not verify those details. Check them and try again.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!IsCurrentSession(currentVersion)) return;
            verificationError = "The concierge service is unavailable. Please try again.";
        }
        finally
        {
            if (IsCurrentSession(currentVersion)) isVerifying = false;
        }
    }

    private Task LoadHistoryAndReconcileAsync(bool replayPending)
    {
        if (session?.Credential is null)
        {
            return Task.CompletedTask;
        }

        var currentVersion = sessionVersion;
        if (historyRefreshBatch is { } currentBatch
            && currentBatch.SessionVersion == currentVersion)
        {
            currentBatch.Requested = true;
            currentBatch.ReplayPending |= replayPending;
            return currentBatch.RefreshTask!;
        }

        var batch = new HistoryRefreshBatch(currentVersion, replayPending);
        historyRefreshBatch = batch;
        batch.RefreshTask = DrainHistoryRefreshesAsync(batch);
        return batch.RefreshTask;
    }

    private async Task DrainHistoryRefreshesAsync(HistoryRefreshBatch batch)
    {
        // Keep task assignment visible before a synchronously-completing fake/API read can finish.
        await Task.Yield();
        var replayRequested = false;
        var latestReadSucceeded = false;
        try
        {
            while (batch.Requested)
            {
                batch.Requested = false;
                replayRequested |= batch.ReplayPending;
                batch.ReplayPending = false;
                latestReadSucceeded = await RefreshHistoryAndReconcileOnceAsync(batch.SessionVersion);
            }
        }
        finally
        {
            if (ReferenceEquals(historyRefreshBatch, batch))
            {
                historyRefreshBatch = null;
            }
        }

        if (batch.SessionVersion == sessionVersion
            && replayRequested
            && latestReadSucceeded
            && pending is not null)
        {
            await SubmitPendingAsync();
        }
    }

    private async Task<bool> RefreshHistoryAndReconcileOnceAsync(int currentVersion)
    {
        var credential = IsCurrentSession(currentVersion) ? session?.Credential : null;
        if (credential is null)
        {
            return false;
        }

        isWorking = pending is not null;
        model = model.SetTurnError(null);
        try
        {
            var history = await Concierge.ReadHistoryAsync(credential);
            if (!IsCurrentSession(currentVersion)) return false;
            var pendingBeforeReconciliation = pending;
            model = model.ReconcileHistory(history.Turns);
            foreach (var turn in history.Turns) voicePendingTranscripts.Remove(turn.TurnId);
            if (pendingBeforeReconciliation is not null && pending is null)
            {
                await PersistSessionAsync();
                if (!IsCurrentSession(currentVersion)) return false;
                model = model.SetTurnError(null);
            }

            await StartRideActivityFeedAsync(currentVersion, credential);
            suggestionsExpanded = turns.Count == 0 && pending is null;
            await FinishTranscriptChangeAsync();
            return true;
        }
        catch (ConciergeApiException exception)
        {
            if (!IsCurrentSession(currentVersion)) return false;
            if (IsUnauthorized(exception))
            {
                await MarkExpiredAsync();
                return false;
            }

            model = model.SetTurnError(ApiMessage(
                exception,
                "We couldn’t load your conversation. Please try again."));
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!IsCurrentSession(currentVersion)) return false;
            model = model.SetTurnError("We couldn’t load your conversation. Check your connection and try again.");
            return false;
        }
        finally
        {
            if (IsCurrentSession(currentVersion))
            {
                isWorking = false;
                StateHasChanged();
            }
        }
    }

    private async Task SendDraftAsync()
    {
        var message = draft.Trim();
        if (message.Length == 0 || !CanSend)
        {
            return;
        }

        await StartTurnAsync(message, clearDraft: true);
    }

    private async Task SendSuggestionAsync()
    {
        if (!CanSend)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(selectedSuggestion))
        {
            return;
        }

        await StartTurnAsync(selectedSuggestion, clearDraft: false);
    }

    private bool CanSend =>
        ready
        && !isWorking
        && pending is null
        && !hasExpired
        && !isEnding
        && !showEndConfirmation
        && session?.Credential is not null;

    private async Task StartTurnAsync(string message, bool clearDraft)
    {
        var currentVersion = sessionVersion;
        if (!CanSend || Encoding.UTF8.GetByteCount(message) > 2000)
        {
            model = model.SetTurnError("Messages must be no more than 2,000 bytes.");
            return;
        }

        var nearBottom = await JavaScript.InvokeAsync<bool>(
            "conciergeChat.isNearBottom",
            transcriptElement);
        if (!IsCurrentSession(currentVersion) || !CanSend) return;
        transcriptNearBottom = nearBottom;

        model = model.StartTurn(new PendingTurnState(
            Guid.NewGuid().ToString("N"),
            message,
            "web",
            DateTimeOffset.UtcNow), clearDraft);

        suggestionsExpanded = false;

        model = model.SetTurnError(null);
        isWorking = true;
        transcriptNearBottom = true;
        showNewMessage = false;
        scrollAfterRender = true;
        await PersistSessionAsync();
        if (!IsCurrentSession(currentVersion)) return;
        StateHasChanged();
        await SubmitPendingAsync();
    }

    private async Task SubmitPendingAsync()
    {
        if (pending is null || session?.Credential is null || hasExpired)
        {
            return;
        }

        var currentVersion = sessionVersion;
        var turn = pending;
        isWorking = true;
        model = model.SetTurnError(null);
        StateHasChanged();

        try
        {
            await Concierge.SubmitTurnAsync(
                session.Credential,
                new ConciergeTurnRequest(turn.TurnId, turn.UserMessage, turn.Channel));
            if (!IsCurrentSession(currentVersion))
            {
                return;
            }

            await LoadHistoryAndReconcileAsync(replayPending: false);
            if (!IsCurrentSession(currentVersion)) return;
            if (pending is not null && pending.TurnId == turn.TurnId && turnError is null)
            {
                model = model.SetTurnError("The reply was accepted but isn’t in the conversation yet. Retry to check it.");
            }
        }
        catch (ConciergeApiException exception)
        {
            if (!IsCurrentSession(currentVersion))
            {
                return;
            }

            if (IsUnauthorized(exception))
            {
                await MarkExpiredAsync();
                return;
            }

            model = model.SetTurnError(ApiMessage(
                exception,
                "The concierge couldn’t complete that reply. Retry to try the same message again."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (IsCurrentSession(currentVersion))
            {
                model = model.SetTurnError("We couldn’t reach the concierge. Check your connection and retry.");
            }
        }
        finally
        {
            if (IsCurrentSession(currentVersion))
            {
                isWorking = false;
                StateHasChanged();
            }
        }
    }

    private async Task RetryAsync()
    {
        if (isWorking || hasExpired)
        {
            return;
        }

        await LoadHistoryAndReconcileAsync(replayPending: pending is not null);
    }

    private async Task MarkExpiredAsync()
    {
        if (voiceMode)
        {
            // An expired credential must not leave the microphone open; the server revokes the bearer on close.
            _ = JavaScript.InvokeVoidAsync("conciergeVoice.stop").AsTask().ContinueWith(
                static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        voiceMode = false;
        voiceSessionId = null;
        StopWatchingVoiceRelay();
        await ResetRideActivityAsync();
        model = model.MarkExpired();
        isWorking = false;
        isVerifying = false;
        suggestionsExpanded = false;
        StateHasChanged();
    }

    private async Task VerifyAgain()
    {
        await ResetRideActivityAsync();
        model = model.BeginVerification();
        verificationError = null;
        isWorking = false;
        isVerifying = false;
        StateHasChanged();
    }

    private async Task VerifyStayAgain()
    {
        chatEndedForInactivity = false;
        voiceEndReason = null;
        await ResetRideActivityAsync();
        model = model.BeginStayVerification();
        lastName = string.Empty;
        roomNumber = string.Empty;
        verificationError = null;
        suggestionsExpanded = false;
        ready = true;
        isWorking = false;
        isVerifying = false;
    }

    private async Task PersistDraftAsync()
    {
        if (session is null)
        {
            return;
        }

        await PersistSessionAsync();
    }

    private Task PersistSessionAsync() =>
        session is null
            ? Task.CompletedTask
            : JavaScript.InvokeVoidAsync("conciergeChat.saveSession", session).AsTask();

    private async Task EndChatAsync()
    {
        // The guest is leaving the conversation; voice must not keep listening behind the dialog.
        CloseActivity();
        await StopRideActivityFeedAsync();
        await StopVoiceAsync("Voice stopped.");
        if (!model.HasEndContent)
        {
            await EndInteractionAsync();
            return;
        }

        model = model.RequestEndConfirmation();
    }

    private async Task EndVoiceAsync()
    {
        if (!voiceMode)
        {
            return;
        }

        await StopVoiceAsync("Voice stopped.");
        voiceMode = false;
        voiceSessionId = null;
        StopWatchingVoiceRelay();
        await LoadHistoryAndReconcileAsync(replayPending: false);
    }

    private async Task CancelEndChat()
    {
        model = model.CancelEnd();
        if (!hasExpired && session?.Credential is { } credential)
        {
            await StartRideActivityFeedAsync(sessionVersion, credential);
        }
    }

    private async Task EndInteractionAsync()
    {
        if (session?.Credential is null || isEnding)
        {
            return;
        }

        await StopVoiceAsync("Voice stopped.");
        CloseActivity();
        await StopRideActivityFeedAsync();
        model = model.BeginEnd();
        var currentVersion = sessionVersion;
        var credential = session.Credential;
        var endingVoiceSessionId = voiceSessionId;
        isWorking = false;
        StateHasChanged();
        try
        {
            if (endingVoiceSessionId is not null)
            {
                VoiceTelemetry.RecordEndChat(Logger, "interaction_started", endingVoiceSessionId, "end_interaction_requested");
            }
            await Concierge.EndAsync(credential);
            if (endingVoiceSessionId is not null)
            {
                VoiceTelemetry.RecordEndChat(Logger, "interaction_completed", endingVoiceSessionId, "end_interaction_succeeded");
            }
            if (!IsCurrentSession(currentVersion)) return;
            await CompleteEndAsync();
            if (endingVoiceSessionId is not null)
            {
                VoiceTelemetry.RecordEndChat(Logger, "completed", endingVoiceSessionId, "session_cleared");
            }
        }
        catch (ConciergeApiException exception)
        {
            if (endingVoiceSessionId is not null)
            {
                VoiceTelemetry.RecordEndChat(Logger, "completion_failed", endingVoiceSessionId,
                    IsUnauthorized(exception) ? "unauthorized" : "api_failure");
            }
            if (!IsCurrentSession(currentVersion)) return;
            if (IsUnauthorized(exception))
            {
                model = model.HideEndConfirmation();
                await MarkExpiredAsync();
                return;
            }

            model = model.EndFailed(ApiMessage(exception, "The chat could not be ended. Please try again."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (endingVoiceSessionId is not null)
            {
                VoiceTelemetry.RecordEndChat(Logger, "completion_failed", endingVoiceSessionId, "service_or_browser_failure");
            }
            if (!IsCurrentSession(currentVersion)) return;
            model = model.EndFailed("The chat could not be ended because the concierge service is unavailable.");
        }
        catch (OperationCanceledException)
        {
            if (endingVoiceSessionId is not null)
            {
                VoiceTelemetry.RecordEndChat(Logger, "completion_failed", endingVoiceSessionId, "cancelled");
            }
            throw;
        }
        finally
        {
            if (IsCurrentSession(currentVersion))
            {
                if (isEnding)
                {
                    model = model.FinishEndAttempt();
                }

                if (!chatEnded && !hasExpired && session?.Credential is { } activeCredential)
                {
                    await StartRideActivityFeedAsync(currentVersion, activeCredential);
                }
            }
        }
    }

    private async Task CompleteEndAsync()
    {
        await ResetRideActivityAsync();
        chatEndedForInactivity = voiceEndReason == "idle_timeout";
        voiceIdleWarning = false;
        voiceMode = false;
        voiceMuted = false;
        voiceSessionId = null;
        StopWatchingVoiceRelay();
        model = model.CompleteEnd();
        verificationError = null;
        suggestionsExpanded = false;
        lastName = string.Empty;
        roomNumber = string.Empty;
        isWorking = false;
        await JavaScript.InvokeVoidAsync("conciergeChat.clearSession");
    }

    private bool CanStartVoice => CanSend && !voiceMode;

    private bool VoiceActive => voiceState is not ("disconnected" or "paused");

    private string VoiceStateLabel => voiceState switch
    {
        "connecting" => "Connecting…",
        "listening" => "Listening",
        "thinking" => "Thinking…",
        "speaking" => "Speaking",
        "paused" => "Paused",
        _ when voiceEndReason == "idle_timeout" => "Stopped (inactive)",
        _ => "Disconnected",
    };

    private async Task StartVoiceAsync()
    {
        if (!CanStartVoice)
        {
            return;
        }

        voiceMode = true;
        voiceSessionId = null;
        StopWatchingVoiceRelay();
        await ConnectVoiceAsync();
    }

    /// <summary>Opens a new Deepgram connection; reuses the voice session so turn IDs stay unique.</summary>
    private async Task ConnectVoiceAsync()
    {
        if (session?.Credential is null || hasExpired)
        {
            return;
        }

        voiceMuted = false;
        voiceIdleWarning = false;
        voiceEndReason = null;
        voiceReference ??= DotNetObjectReference.Create(this);
        // The previous connection's closure is no longer news; OnVoiceSession watches the new one.
        StopWatchingVoiceRelay();
        voiceState = "connecting";
        voiceMessage = null;
        await JavaScript.InvokeVoidAsync("conciergeVoice.start", session.Credential, voiceSessionId, voiceReference);
    }

    private Task ResumeVoiceAsync() => JavaScript.InvokeVoidAsync("conciergeVoice.resume").AsTask();

    private async Task ToggleVoiceMuteAsync()
    {
        voiceMuted = await JavaScript.InvokeAsync<bool>("conciergeVoice.setMuted", !voiceMuted);
    }

    private async Task StopVoiceAsync(string? message)
    {
        voiceIdleWarning = false;
        if (!voiceMode)
        {
            return;
        }

        voiceMuted = false;
        StopWatchingVoiceRelay();
        await JavaScript.InvokeVoidAsync("conciergeVoice.stop");
        voiceState = "disconnected";
        voiceMessage = message;
    }

    /// <summary>Called from <c>voice.js</c> with the server's voice session ID, which Reconnect reuses.</summary>
    [JSInvokable]
    public Task OnVoiceSession(string id) =>
        InvokeAsync(() =>
        {
            voiceSessionId = voiceMode ? id : null;
            WatchVoiceRelay(voiceSessionId);
        });

    /// <summary>
    /// Subscribes to server-side relay closure for <paramref name="id"/>. The browser also reports
    /// closure through <see cref="OnVoiceState"/>, but that JS call is lost if the Blazor circuit is
    /// disconnected when the voice socket drops; the relay runs in this process, so the server
    /// pushes the closure itself.
    /// </summary>
    private void WatchVoiceRelay(string? id)
    {
        StopWatchingVoiceRelay();
        if (disposed || id is null || !Guid.TryParse(id, out var voiceSession))
        {
            return;
        }

        voiceClosedSubscription = VoiceSessions.SubscribeConnectionClosed(
            voiceSession,
            // Runs on the relay's thread; queue onto the circuit without blocking it.
            connectionNumber => _ = InvokeAsync(() => ApplyVoiceRelayClosedAsync(id, connectionNumber))
                .ContinueWith(
                    task => Logger.LogWarning(task.Exception,
                        "Applying voice relay closure failed for session {VoiceSessionId} (connection {ConnectionNumber}).",
                        id, connectionNumber),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default));
        var version = sessionVersion;
        voiceTurnSubscription = VoiceSessions.SubscribeTurns(voiceSession,
            progress => _ = InvokeAsync(async () =>
            {
                if (!IsCurrentSession(version) || voiceSessionId != id || !voiceMode || chatEnded || isEnding) return;
                if (progress.Finished)
                {
                    voicePendingTranscripts.Remove(progress.TurnId);
                    await LoadHistoryAndReconcileAsync(replayPending: false);
                }
                else if (turns.All(turn => turn.TurnId != progress.TurnId))
                {
                    voicePendingTranscripts[progress.TurnId] = progress;
                    await FinishTranscriptChangeAsync();
                }
                StateHasChanged();
            }).ContinueWith(
                task => Logger.LogWarning(task.Exception,
                    "Applying voice transcript progress failed for session {VoiceSessionId}.", id),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default));
    }

    private void StopWatchingVoiceRelay()
    {
        voiceClosedSubscription?.Dispose();
        voiceClosedSubscription = null;
        voiceTurnSubscription?.Dispose();
        voiceTurnSubscription = null;
        voicePendingTranscripts.Clear();
    }

    private async Task ApplyVoiceRelayClosedAsync(string id, int connectionNumber)
    {
        if (disposed || !voiceMode || voiceEndReason is not null || !StringComparer.Ordinal.Equals(voiceSessionId, id))
        {
            return;
        }

        // The browser's own report is more specific (e.g. 4401 expiry); keep it.
        if (voiceState == "disconnected")
        {
            return;
        }

        Logger.LogInformation(
            "Voice relay closure applied by server for session {VoiceSessionId} (connection {ConnectionNumber}); browser last reported {VoiceState}.",
            id, connectionNumber, voiceState);
        voiceState = "disconnected";
        voiceMuted = false;
        voiceIdleWarning = false;
        voiceMessage = "Voice disconnected.";
        // No JS here: the circuit may be down. Reconnect's conciergeVoice.start ends any stale browser voice.
        await LoadHistoryAndReconcileAsync(replayPending: false);
        StateHasChanged();
    }

    /// <summary>Runs the existing End Chat flow after a confirmed spoken or idle voice ending.</summary>
    [JSInvokable]
    public Task OnVoiceEndRequested(string? reason = null) =>
        InvokeAsync(async () =>
        {
            VoiceTelemetry.RecordEndChat(Logger, "browser_callback_received", voiceSessionId, "browser_end_requested");
            if (!disposed && voiceMode && session?.Credential is not null && !isEnding)
            {
                voiceEndReason = reason == "idle_timeout" ? reason : "farewell";
                voiceIdleWarning = false;
                await EndInteractionAsync();
                StateHasChanged();
            }
            else
            {
                VoiceTelemetry.RecordEndChat(Logger, "browser_callback_ignored", voiceSessionId, "inactive_or_already_ending");
            }
        });

    /// <summary>The relay, not a browser timer, decides whether the chat is nearly idle.</summary>
    [JSInvokable]
    public Task OnVoiceIdleWarning(bool active) =>
        InvokeAsync(() =>
        {
            if (disposed || !voiceMode || voiceEndReason is not null)
            {
                return;
            }

            voiceIdleWarning = active;
            StateHasChanged();
        });

    /// <summary>Called from <c>voice.js</c> when the relay changes state or closes.</summary>
    [JSInvokable]
    public Task OnVoiceState(string state, string? message, int closeCode) =>
        InvokeAsync(async () =>
        {
            if (disposed || !voiceMode || voiceEndReason is not null)
            {
                return;
            }

            voiceState = state;
            voiceMessage = message;
            if (state == "disconnected")
            {
                voiceMuted = false;
                voiceIdleWarning = false;
            }
            else if (state is "thinking" or "speaking")
            {
                voiceIdleWarning = false;
            }
            if (state == "disconnected" && closeCode == (int)Voice.VoiceCloseCodes.Unauthorized)
            {
                await MarkExpiredAsync();
                return;
            }

            // The relay's Finished callback is authoritative. Keep disconnect recovery for cases
            // where the circuit misses that callback.
            if (state == "disconnected")
            {
                await LoadHistoryAndReconcileAsync(replayPending: false);
            }

            StateHasChanged();
        });

    private void ToggleSuggestions() => suggestionsExpanded = !suggestionsExpanded;

    private async Task ToggleActivity()
    {
        if (activityOpen)
        {
            CloseActivity();
            return;
        }

        activityOpen = true;
        var credential = session?.Credential;
        var currentVersion = sessionVersion;
        if (credential is null)
        {
            return;
        }

        var retrying = rideActivityPaused || rideActivityError is not null;
        if (retrying)
        {
            rideActivityPaused = false;
            rideActivityReconnecting = true;
        }

        var cancellationToken = rideActivityCancellation?.Token ?? CancellationToken.None;
        try
        {
            // A fresh server snapshot makes the read boundary include entries published before this
            // open action, while later live arrivals remain beyond its exclusive end offset.
            var snapshot = await Concierge.ReadRideActivitySnapshotAsync(credential, cancellationToken);
            if (!IsCurrentSession(currentVersion) || !activityOpen) return;
            MergeRideActivitySnapshot(snapshot);

            var readState = await Concierge.MarkRideActivitySnapshotReadAsync(
                credential,
                snapshot.ReadState.SnapshotOffset,
                cancellationToken);
            if (!IsCurrentSession(currentVersion) || !activityOpen) return;
            rideActivityWindow?.ApplyReadState(readState);
            RefreshRideActivityReadKeys();
            rideActivityPaused = false;
            rideActivityReconnecting = false;
            rideActivityError = null;
            if (rideActivityStreamTask is null)
            {
                await StartRideActivityFeedAsync(currentVersion, credential);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The stay changed or ended while the read snapshot was in flight.
        }
        catch (ConciergeApiException exception)
        {
            if (!IsCurrentSession(currentVersion)) return;
            if (IsUnauthorized(exception))
            {
                await MarkExpiredAsync();
                return;
            }

            rideActivityPaused = true;
            rideActivityReconnecting = false;
            rideActivityError = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!IsCurrentSession(currentVersion)) return;
            rideActivityPaused = true;
            rideActivityReconnecting = false;
            rideActivityError = "Ride activity could not be refreshed. Please try again.";
        }
    }

    private void CloseActivity() => activityOpen = false;

    private Task HandlePageKeyDown(KeyboardEventArgs args)
    {
        if (activityOpen && StringComparer.Ordinal.Equals(args.Key, "Escape"))
        {
            CloseActivity();
        }

        return Task.CompletedTask;
    }

    private async Task ResetRideActivityAsync()
    {
        activityOpen = false;
        rideActivityEntries.Clear();
        rideActivityReadEntryKeys.Clear();
        rideActivityWindow = null;
        rideActivityPaused = false;
        rideActivityReconnecting = false;
        rideActivityError = null;
        await StopRideActivityFeedAsync();
    }

    private async Task StartRideActivityFeedAsync(int version, string credential)
    {
        if (StringComparer.Ordinal.Equals(rideActivityCredential, credential)
            && rideActivityStreamTask is not null)
        {
            return;
        }

        await StopRideActivityFeedAsync();
        rideActivityCredential = credential;
        var cancellation = new CancellationTokenSource();
        rideActivityCancellation = cancellation;

        try
        {
            var snapshot = await Concierge.ReadRideActivitySnapshotAsync(credential, cancellation.Token);
            if (!IsCurrentSession(version)
                || cancellation.IsCancellationRequested
                || !ReferenceEquals(rideActivityCancellation, cancellation))
            {
                return;
            }

            MergeRideActivitySnapshot(snapshot);
            var streamStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            rideActivityStreamTask = ConsumeRideActivityAsync(
                version,
                credential,
                snapshot.ReadState.SnapshotOffset,
                cancellation,
                streamStart.Task);
            streamStart.SetResult();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A new stay/session superseded this initial snapshot request.
        }
        catch (ConciergeApiException exception)
        {
            if (!IsCurrentSession(version) || !ReferenceEquals(rideActivityCancellation, cancellation)) return;
            if (IsUnauthorized(exception))
            {
                await MarkExpiredAsync();
                return;
            }

            rideActivityError = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!IsCurrentSession(version) || !ReferenceEquals(rideActivityCancellation, cancellation)) return;
            Logger.LogWarning(exception, "Could not load the ride activity snapshot for the current stay.");
            rideActivityError = "Ride activity could not be loaded. Please try again.";
        }
    }

    private async Task ConsumeRideActivityAsync(
        int version,
        string credential,
        long fromOffset,
        CancellationTokenSource cancellation,
        Task streamStart)
    {
        try
        {
            await streamStart;
            await foreach (var item in Concierge
                .StreamRideActivityAsync(credential, fromOffset, cancellation.Token)
                .WithCancellation(cancellation.Token))
            {
                await InvokeAsync(() =>
                {
                    if (!IsCurrentSession(version)
                        || !ReferenceEquals(rideActivityCancellation, cancellation)
                        || rideActivityWindow is null)
                    {
                        return;
                    }

                    rideActivityPaused = false;
                    rideActivityReconnecting = false;
                    rideActivityError = null;
                    if (rideActivityWindow.Apply(item))
                    {
                        SynchronizeRideActivityEntries();
                        RefreshRideActivityReadKeys();
                    }

                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Cancellation owns subscription disposal in ConciergeService.
        }
        catch (ConciergeApiException exception)
        {
            await InvokeAsync(async () =>
            {
                if (!ReferenceEquals(rideActivityCancellation, cancellation)) return;
                if (IsUnauthorized(exception))
                {
                    // The stream enumerator has already unwound; detach this task so expiry can
                    // await every other owned resource without awaiting itself.
                    rideActivityStreamTask = null;
                    rideActivityCancellation = null;
                    rideActivityCredential = null;
                    await MarkExpiredAsync();
                    return;
                }

                rideActivityPaused = true;
                rideActivityError = "Ride activity updates are temporarily unavailable.";
                StateHasChanged();
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "Ride activity updates stopped for the current stay.");
            await InvokeAsync(() =>
            {
                if (!ReferenceEquals(rideActivityCancellation, cancellation)) return;
                rideActivityPaused = true;
                rideActivityError = "Ride activity updates are temporarily unavailable.";
                StateHasChanged();
            });
        }
        finally
        {
            if (ReferenceEquals(rideActivityCancellation, cancellation))
            {
                rideActivityStreamTask = null;
                rideActivityCancellation = null;
                rideActivityCredential = null;
            }

            cancellation.Dispose();
        }
    }

    private void MergeRideActivitySnapshot(ConciergeRideActivitySnapshot snapshot)
    {
        if (rideActivityWindow is null)
        {
            rideActivityWindow = new ConciergeRideActivityWindow(snapshot);
        }
        else
        {
            rideActivityWindow.MergeSnapshot(snapshot);
        }

        SynchronizeRideActivityEntries();
        RefreshRideActivityReadKeys();
        rideActivityError = null;
        rideActivityPaused = false;
        StateHasChanged();
    }

    private void SynchronizeRideActivityEntries()
    {
        rideActivityEntries.Clear();
        if (rideActivityWindow is null) return;
        rideActivityEntries.AddRange(rideActivityWindow.Entries
            .Select(item => item.Entry)
            .OrderByDescending(entry => entry.ChangedAtUtc)
            .ThenByDescending(entry => entry.Sequence));
    }

    private void RefreshRideActivityReadKeys()
    {
        rideActivityReadEntryKeys.Clear();
        if (rideActivityWindow is null) return;
        foreach (var item in rideActivityWindow.Entries)
        {
            if (item.Offset < rideActivityWindow.ReadState.ReadOffset)
            {
                rideActivityReadEntryKeys.Add(RideActivityEntryKey(item.Entry));
            }
        }
    }

    private async Task StopRideActivityFeedAsync()
    {
        var cancellation = rideActivityCancellation;
        var streamTask = rideActivityStreamTask;
        rideActivityCancellation = null;
        rideActivityCredential = null;
        rideActivityStreamTask = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            if (streamTask is null)
            {
                cancellation.Dispose();
            }
        }

        if (streamTask is not null)
        {
            try
            {
                await streamTask;
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected result of disposing a live feed.
            }
        }
    }

    private static string RideActivityEntryKey(RideActivityEntry entry) =>
        $"{entry.RideId:N}:{entry.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Called from <c>concierge.js</c> when the transcript scroll position changes; hides the
    /// new-message indicator once the guest scrolls back to the bottom.
    /// </summary>
    [JSInvokable]
    public Task OnTranscriptScroll(bool nearBottom, int version) =>
        InvokeAsync(() =>
        {
            if (!IsCurrentSession(version) || !TranscriptVisible) return;
            transcriptNearBottom = nearBottom;
            if (nearBottom && showNewMessage)
            {
                showNewMessage = false;
                StateHasChanged();
            }
        });

    /// <summary>
    /// Disconnects transcript scrolling and releases its .NET callback reference.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        disposed = true;
        voiceIdleWarning = false;
        StopWatchingVoiceRelay();
        await ResetRideActivityAsync();
        model = model.InvalidateOutstandingWork();
        if (voiceReference is not null)
        {
            try
            {
                await JavaScript.InvokeVoidAsync("conciergeVoice.stop");
            }
            catch (JSDisconnectedException)
            {
                // The browser closed the page; pagehide already closed the voice socket.
            }

            voiceReference.Dispose();
        }

        // Prerendered instances never wired a listener and cannot call JavaScript.
        if (scrollReference is null)
        {
            return;
        }

        try
        {
            await JavaScript.InvokeVoidAsync("conciergeChat.unwireTranscript");
        }
        catch (JSDisconnectedException)
        {
            // The browser has already dropped the circuit and its listener.
        }
        scrollReference?.Dispose();
    }

    private async Task FinishTranscriptChangeAsync()
    {
        if (transcriptNearBottom)
        {
            showNewMessage = false;
            scrollAfterRender = true;
            await InvokeAsync(StateHasChanged);
        }
        else
        {
            showNewMessage = true;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ScrollToLatestAsync()
    {
        var currentVersion = sessionVersion;
        if (!TranscriptVisible)
        {
            return;
        }

        await JavaScript.InvokeVoidAsync("conciergeChat.scrollToBottom", transcriptElement);
        if (!IsCurrentSession(currentVersion) || !TranscriptVisible) return;
        showNewMessage = false;
        transcriptNearBottom = true;
    }

    private static bool IsUnauthorized(ConciergeApiException exception) =>
        exception.StatusCode == (int)HttpStatusCode.Unauthorized
        || StringComparer.Ordinal.Equals(exception.Code, "unauthorized");

    private static string ApiMessage(ConciergeApiException exception, string fallback) =>
        string.IsNullOrWhiteSpace(exception.Message) ? fallback : exception.Message;

    private sealed record ChatMessage(
        string TurnId,
        string Text,
        bool IsGuest,
        DateTimeOffset Timestamp,
        DateOnly HotelDay,
        bool Interrupted = false);
}
