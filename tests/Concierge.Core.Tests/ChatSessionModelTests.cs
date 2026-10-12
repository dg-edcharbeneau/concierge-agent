using System.Reflection;
using Concierge.Contracts;
using Concierge.Web.Components.Pages;
using Concierge.Web.Services;

namespace Concierge.Core.Tests;

public sealed class ChatSessionModelTests
{
    private static readonly Type ModelType = typeof(Home).Assembly.GetType(
        "Concierge.Web.Components.Pages.ChatSessionModel",
        throwOnError: true)!;

    [Fact]
    public void RestoreLoadsCredentialedSessionAndMarksExpiredSession()
    {
        var now = DateTimeOffset.Parse("2026-10-12T12:00:00Z");
        var stayId = Guid.NewGuid();
        var pending = Pending("retry-id");
        var stored = new ChatSessionState
        {
            StayId = stayId,
            Credential = "credential",
            ExpiresAtUtc = now,
            Pending = pending,
            Draft = "separate draft"
        };

        var restored = Transition(Initial(), "Restore", stored, now);

        Assert.Equal("credential", Get<ChatSessionState?>(restored, "Session")?.Credential);
        Assert.Equal(pending, Get<PendingTurnState?>(restored, "Pending"));
        Assert.Equal("separate draft", Get<string>(restored, "Draft"));
        Assert.Equal(stayId, Get<Guid>(restored, "RecoveryStayId"));
        Assert.True(Get<bool>(restored, "HasExpired"));
        Assert.True(Get<bool>(restored, "ShowExpiredStrip"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerificationRecoversPendingAndDraftOnlyForSameKnownStay(bool sameStay)
    {
        var stayId = Guid.NewGuid();
        var pending = Pending("retry-id");
        var restored = Transition(Initial(), "Restore", new ChatSessionState
        {
            StayId = stayId,
            Credential = "old-credential",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            Pending = pending,
            Draft = "separate draft"
        }, DateTimeOffset.UtcNow);

        var verifying = Transition(restored, "BeginVerification");
        Assert.Null(Get<ChatSessionState?>(verifying, "Session"));
        Assert.Equal(pending, Get<PendingTurnState?>(verifying, "Pending"));
        Assert.Equal("separate draft", Get<string>(verifying, "Draft"));
        Assert.Equal(Get<int>(restored, "Version") + 1, Get<int>(verifying, "Version"));

        var result = Transition(verifying, "VerificationSucceeded", new ConciergeSessionResponse(
            "new-credential",
            DateTimeOffset.UtcNow.AddHours(1),
            sameStay ? stayId : Guid.NewGuid()), DateTimeOffset.UtcNow);

        Assert.Equal("new-credential", Get<ChatSessionState?>(result, "Session")?.Credential);
        Assert.Equal(sameStay ? pending : null, Get<PendingTurnState?>(result, "Pending"));
        Assert.Equal(sameStay ? "separate draft" : string.Empty, Get<string>(result, "Draft"));
        Assert.Equal(Get<int>(verifying, "Version") + 1, Get<int>(result, "Version"));
        Assert.False(Get<bool>(result, "ChatEnded"));
        Assert.False(Get<bool>(result, "ShowEndConfirmation"));
    }

    [Fact]
    public void BeginStayVerificationDropsRecoveryDataAndInvalidatesOldWork()
    {
        var restored = Transition(Initial(), "Restore", new ChatSessionState
        {
            StayId = Guid.NewGuid(),
            Credential = "credential",
            Pending = Pending("retry-id"),
            Draft = "draft"
        }, DateTimeOffset.UtcNow);

        var cleared = Transition(restored, "BeginStayVerification");

        Assert.Null(Get<ChatSessionState?>(cleared, "Session"));
        Assert.Null(Get<PendingTurnState?>(cleared, "Pending"));
        Assert.Equal(string.Empty, Get<string>(cleared, "Draft"));
        Assert.Equal(Guid.Empty, Get<Guid>(cleared, "RecoveryStayId"));
        Assert.Empty(Get<List<ConciergeTranscriptTurn>>(cleared, "Turns"));
        Assert.Equal(Get<int>(restored, "Version") + 1, Get<int>(cleared, "Version"));
    }

    [Fact]
    public void HistoryReconciliationClearsCommittedRetryAndPreservesDraft()
    {
        var pending = Pending("committed-id");
        var restored = Transition(Initial(), "Restore", new ChatSessionState
        {
            StayId = Guid.NewGuid(),
            Credential = "credential",
            Pending = pending,
            Draft = "draft to keep"
        }, DateTimeOffset.UtcNow);
        restored = Transition(restored, "SetTurnError", "old retry error");
        var committedTurn = new ConciergeTranscriptTurn(
            pending.TurnId, 1, pending.Channel, pending.UserMessage, "Reply", DateTimeOffset.UtcNow);

        var reconciled = Transition(restored, "ReconcileHistory", (object)new[] { committedTurn });

        Assert.Null(Get<PendingTurnState?>(reconciled, "Pending"));
        Assert.Null(Get<PendingTurnState?>(Get<ChatSessionState?>(reconciled, "Session")!, "Pending"));
        Assert.Equal("draft to keep", Get<string>(reconciled, "Draft"));
        Assert.Null(Get<string?>(reconciled, "TurnError"));
        Assert.Equal(committedTurn, Assert.Single(Get<List<ConciergeTranscriptTurn>>(reconciled, "Turns")));
    }

    [Fact]
    public void HistoryWithoutPendingTurnKeepsSameIdRecoveryAndError()
    {
        var pending = Pending("not-committed-id");
        var restored = Transition(Initial(), "Restore", new ChatSessionState
        {
            StayId = Guid.NewGuid(),
            Credential = "credential",
            Pending = pending,
            Draft = "draft"
        }, DateTimeOffset.UtcNow);
        restored = Transition(restored, "SetTurnError", "retry needed");

        var reconciled = Transition(restored, "ReconcileHistory", (object)Array.Empty<ConciergeTranscriptTurn>());

        Assert.Equal(pending, Get<PendingTurnState?>(reconciled, "Pending"));
        Assert.Equal(pending, Get<PendingTurnState?>(Get<ChatSessionState?>(reconciled, "Session")!, "Pending"));
        Assert.Equal("retry needed", Get<string?>(reconciled, "TurnError"));
        Assert.Equal("draft", Get<string>(reconciled, "Draft"));
    }

    [Fact]
    public void StartingSuggestionPreservesDraftWhileSendingDraftClearsIt()
    {
        var model = Transition(Initial(), "Restore", new ChatSessionState
        {
            StayId = Guid.NewGuid(),
            Credential = "credential",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        }, DateTimeOffset.UtcNow);
        model = Transition(model, "SetDraft", "keep draft");

        var suggestion = Transition(model, "StartTurn", Pending("suggestion-id"), false);
        Assert.Equal("keep draft", Get<string>(suggestion, "Draft"));
        Assert.Equal("keep draft", Get<string>(Get<ChatSessionState?>(suggestion, "Session")!, "Draft"));

        model = Transition(model, "StartTurn", Pending("draft-id"), true);
        Assert.Equal(string.Empty, Get<string>(model, "Draft"));
        Assert.Equal("draft-id", Get<PendingTurnState?>(model, "Pending")?.TurnId);
        Assert.Equal(string.Empty, Get<string>(Get<ChatSessionState?>(model, "Session")!, "Draft"));
    }

    [Fact]
    public void FailedAndCompletedEndTransitionsPreserveThenClearRetryState()
    {
        var pending = Pending("same-id");
        var model = Transition(Initial(), "Restore", new ChatSessionState
        {
            StayId = Guid.NewGuid(),
            Credential = "credential",
            Pending = pending,
            Draft = "separate draft"
        }, DateTimeOffset.UtcNow);
        model = Transition(model, "RequestEndConfirmation");
        model = Transition(model, "BeginEnd");
        Assert.True(Get<bool>(model, "IsEnding"));
        Assert.Equal(1, Get<int>(model, "Version"));

        model = Transition(model, "EndFailed", "end unavailable");
        Assert.Equal("end unavailable", Get<string?>(model, "EndError"));
        Assert.Equal("The reply may still be processing. Retry to check the same message.",
            Get<string?>(model, "TurnError"));
        Assert.Equal(pending, Get<PendingTurnState?>(model, "Pending"));
        Assert.Equal("separate draft", Get<string>(model, "Draft"));

        model = Transition(model, "FinishEndAttempt");
        Assert.False(Get<bool>(model, "IsEnding"));
        model = Transition(model, "CancelEnd");
        Assert.False(Get<bool>(model, "ShowEndConfirmation"));

        model = Transition(model, "CompleteEnd");
        Assert.Null(Get<ChatSessionState?>(model, "Session"));
        Assert.Null(Get<PendingTurnState?>(model, "Pending"));
        Assert.Equal(string.Empty, Get<string>(model, "Draft"));
        Assert.Equal(Guid.Empty, Get<Guid>(model, "RecoveryStayId"));
        Assert.Null(Get<string?>(model, "TurnError"));
        Assert.Null(Get<string?>(model, "EndError"));
        Assert.True(Get<bool>(model, "ChatEnded"));
    }

    [Fact]
    public void ExpiryInvalidatesOutstandingCallbacksWithoutDiscardingConversation()
    {
        var pending = Pending("retry-id");
        var model = Transition(Initial(), "Restore", new ChatSessionState
        {
            StayId = Guid.NewGuid(),
            Credential = "credential",
            Pending = pending,
            Draft = "draft"
        }, DateTimeOffset.UtcNow);
        model = Transition(model, "SetTurnError", "old error");

        var expired = Transition(model, "MarkExpired");

        Assert.Equal(Get<int>(model, "Version") + 1, Get<int>(expired, "Version"));
        Assert.True(Get<bool>(expired, "HasExpired"));
        Assert.True(Get<bool>(expired, "ShowExpiredStrip"));
        Assert.Equal(pending, Get<PendingTurnState?>(expired, "Pending"));
        Assert.Equal("draft", Get<string>(expired, "Draft"));
        Assert.Null(Get<string?>(expired, "TurnError"));
    }

    private static object Initial() =>
        ModelType.GetProperty("Initial", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

    private static object Transition(object model, string name, params object[] arguments) =>
        ModelType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance)!.Invoke(model, arguments)!;

    private static T Get<T>(object model, string name) =>
        (T)model.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(model)!;

    private static PendingTurnState Pending(string turnId) =>
        new(turnId, $"Message for {turnId}", "web", DateTimeOffset.UtcNow);
}
