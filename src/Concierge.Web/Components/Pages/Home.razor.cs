using System.Net;
using System.Text;
using Concierge.Contracts;
using Concierge.Web.Services;
using Microsoft.AspNetCore.Components;
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

    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    [Inject]
    private IConciergeService Concierge { get; set; } = default!;

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
    private string? wiredCredential;
    private int wiredVersion = -1;
    private bool disposed;

    private bool IsCurrentSession(int version) => !disposed && model.IsCurrent(version);

    private bool TranscriptVisible =>
        ready && session?.Credential is not null && !showEndConfirmation && !chatEnded;

    private bool HasConversation => turns.Count > 0 || pending is not null;

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
                    new ChatMessage(turn.TurnId, turn.AssistantMessage, false, timestamp, day)
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

    private async Task LoadHistoryAndReconcileAsync(bool replayPending)
    {
        var currentVersion = sessionVersion;
        var credential = session?.Credential;
        if (credential is null)
        {
            return;
        }

        isWorking = pending is not null;
        model = model.SetTurnError(null);
        try
        {
            var history = await Concierge.ReadHistoryAsync(credential);
            if (!IsCurrentSession(currentVersion)) return;
            var pendingBeforeReconciliation = pending;
            model = model.ReconcileHistory(history.Turns);
            if (pendingBeforeReconciliation is not null && pending is null)
            {
                await PersistSessionAsync();
                if (!IsCurrentSession(currentVersion)) return;
                model = model.SetTurnError(null);
            }
            else if (pending is not null && replayPending)
            {
                await SubmitPendingAsync();
                return;
            }

            suggestionsExpanded = turns.Count == 0 && pending is null;
            await FinishTranscriptChangeAsync();
        }
        catch (ConciergeApiException exception)
        {
            if (!IsCurrentSession(currentVersion)) return;
            if (IsUnauthorized(exception))
            {
                MarkExpired();
                return;
            }

            model = model.SetTurnError(ApiMessage(
                exception,
                "We couldn’t load your conversation. Please try again."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!IsCurrentSession(currentVersion)) return;
            model = model.SetTurnError("We couldn’t load your conversation. Check your connection and try again.");
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
                MarkExpired();
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

    private void MarkExpired()
    {
        model = model.MarkExpired();
        isWorking = false;
        isVerifying = false;
        suggestionsExpanded = false;
        StateHasChanged();
    }

    private void VerifyAgain()
    {
        model = model.BeginVerification();
        verificationError = null;
        isWorking = false;
        isVerifying = false;
        StateHasChanged();
    }

    private void VerifyStayAgain()
    {
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
        if (!model.HasEndContent)
        {
            await EndInteractionAsync();
            return;
        }

        model = model.RequestEndConfirmation();
    }

    private void CancelEndChat()
    {
        model = model.CancelEnd();
    }

    private async Task EndInteractionAsync()
    {
        if (session?.Credential is null || isEnding)
        {
            return;
        }

        model = model.BeginEnd();
        var currentVersion = sessionVersion;
        var credential = session.Credential;
        isWorking = false;
        StateHasChanged();
        try
        {
            await Concierge.EndAsync(credential);
            if (!IsCurrentSession(currentVersion)) return;
            await CompleteEndAsync();
        }
        catch (ConciergeApiException exception)
        {
            if (!IsCurrentSession(currentVersion)) return;
            if (IsUnauthorized(exception))
            {
                model = model.HideEndConfirmation();
                MarkExpired();
                return;
            }

            model = model.EndFailed(ApiMessage(exception, "The chat could not be ended. Please try again."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!IsCurrentSession(currentVersion)) return;
            model = model.EndFailed("The chat could not be ended because the concierge service is unavailable.");
        }
        finally
        {
            if (IsCurrentSession(currentVersion))
            {
                if (isEnding)
                {
                    model = model.FinishEndAttempt();
                }
            }
        }
    }

    private async Task CompleteEndAsync()
    {
        model = model.CompleteEnd();
        verificationError = null;
        suggestionsExpanded = false;
        lastName = string.Empty;
        roomNumber = string.Empty;
        isWorking = false;
        await JavaScript.InvokeVoidAsync("conciergeChat.clearSession");
    }

    private void ToggleSuggestions() => suggestionsExpanded = !suggestionsExpanded;

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
        model = model.InvalidateOutstandingWork();
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
        DateOnly HotelDay);
}
