using Concierge.Contracts;
using Concierge.Web.Voice;
using Microsoft.Extensions.Time.Testing;

namespace Concierge.Core.Tests;

public sealed class VoiceSessionRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider time = new(Now);
    private readonly VoiceSessionRegistry registry;

    public VoiceSessionRegistryTests() => registry = new VoiceSessionRegistry(time);

    [Fact]
    public void BearerExpiresAfterTheConnectionLifetime()
    {
        var connection = registry.Open(Stay(), null);
        time.Advance(VoiceSessionRegistry.ConnectionLifetime - TimeSpan.FromSeconds(1));
        Assert.True(registry.TryAuthenticate(connection.Bearer, out _));

        time.Advance(TimeSpan.FromSeconds(1));

        Assert.False(registry.TryAuthenticate(connection.Bearer, out _));
        Assert.True(connection.Revoked.IsCancellationRequested);
        Assert.Equal(VoiceRevocation.Expired, connection.Revocation);
    }

    [Fact]
    public void BearerNeverOutlivesTheStayEligibilityDeadline()
    {
        var connection = registry.Open(Stay(deadline: Now.AddMinutes(5)), null);
        Assert.Equal(Now.AddMinutes(5), connection.ExpiresAtUtc);

        time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(registry.TryAuthenticate(connection.Bearer, out _));
        Assert.Equal(VoiceRevocation.Expired, connection.Revocation);
    }

    [Fact]
    public void BearerForAnAlreadyIneligibleStayIsBornRevoked()
    {
        var connection = registry.Open(Stay(deadline: Now.AddMinutes(-1)), null);

        Assert.False(registry.TryAuthenticate(connection.Bearer, out _));
        Assert.Equal(VoiceRevocation.Expired, connection.Revocation);
    }

    [Fact]
    public void ReconnectReusesTheVoiceSessionAndRevokesThePreviousBearer()
    {
        var stay = Stay();
        var first = registry.Open(stay, null);

        var second = registry.Open(stay, first.Session.Id);

        Assert.Same(first.Session, second.Session);
        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.NotEqual(first.Bearer, second.Bearer);
        Assert.Equal(VoiceRevocation.Replaced, first.Revocation);
        Assert.False(registry.TryAuthenticate(first.Bearer, out _));
        Assert.True(registry.TryAuthenticate(second.Bearer, out var authenticated));
        Assert.Same(second, authenticated);
    }

    [Fact]
    public void VoiceSessionIdFromAnotherChatSessionStartsANewVoiceSession()
    {
        var other = registry.Open(Stay(), null);

        var connection = registry.Open(Stay(), other.Session.Id);

        Assert.NotSame(other.Session, connection.Session);
        Assert.Equal(1, connection.Number);
        Assert.Equal(VoiceRevocation.None, other.Revocation);
    }

    [Fact]
    public void CloseRevokesOnlyThatConnection()
    {
        var closed = registry.Open(Stay(), null);
        var unrelated = registry.Open(Stay(), null);

        registry.Close(closed);

        Assert.Equal(VoiceRevocation.Closed, closed.Revocation);
        Assert.False(registry.TryAuthenticate(closed.Bearer, out _));
        Assert.True(registry.TryAuthenticate(unrelated.Bearer, out _));
    }

    [Fact]
    public void IdleDeadlineResetsOnSpeechAndWaitsForAnInProgressReply()
    {
        var connection = registry.Open(Stay(), null);
        var idleTimeout = TimeSpan.FromMinutes(2);
        time.Advance(idleTimeout);
        Assert.True(connection.IsIdle(idleTimeout));

        connection.RecordSpeech();
        Assert.False(connection.IsIdle(idleTimeout));
        time.Advance(idleTimeout);
        connection.RecordReplyStarted();
        Assert.False(connection.IsIdle(idleTimeout));

        connection.RecordReplyCompleted();
        Assert.True(connection.IsIdle(idleTimeout));
    }

    [Fact]
    public void ReconnectRestartsTheIdleClock()
    {
        var idleTimeout = TimeSpan.FromMinutes(2);
        var stay = Stay();
        var first = registry.Open(stay, null);
        first.RecordSpeech();

        time.Advance(TimeSpan.FromSeconds(110));
        var reconnected = registry.Open(stay, first.Session.Id);

        Assert.Same(first.Session, reconnected.Session);
        time.Advance(TimeSpan.FromSeconds(20)); // T0 + 130 s: past the old deadline.
        Assert.False(reconnected.IsIdle(idleTimeout));

        time.Advance(TimeSpan.FromSeconds(99)); // Reconnect + 119 s.
        Assert.False(reconnected.IsIdle(idleTimeout));
        time.Advance(TimeSpan.FromSeconds(1)); // Reconnect + 120 s.
        Assert.True(reconnected.IsIdle(idleTimeout));
    }

    [Fact]
    public void ReconnectDoesNotRestartTheIdleClockOfAnotherChatSession()
    {
        var idleTimeout = TimeSpan.FromMinutes(2);
        var unrelated = registry.Open(Stay(), null);
        time.Advance(TimeSpan.FromSeconds(110));
        var stay = Stay();
        var first = registry.Open(stay, null);

        registry.Open(stay, first.Session.Id);
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(unrelated.IsIdle(idleTimeout));
    }

    [Fact]
    public void ReplyInProgressOnReplacedConnectionIsNotTreatedAsIdleAfterReconnect()
    {
        var stay = Stay();
        var first = registry.Open(stay, null);
        first.RecordSpeech();
        first.RecordReplyStarted();
        time.Advance(TimeSpan.FromMinutes(3));

        var reconnected = registry.Open(stay, first.Session.Id);

        Assert.True(reconnected.ReplyInProgress);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(reconnected.IsIdle(TimeSpan.FromMinutes(2)));

        reconnected.RecordReplyCompleted();
        Assert.True(reconnected.IsIdle(TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void DeferredEndChatCancellationCannotBeConfirmedOrReturnedAsAResult()
    {
        var connection = registry.Open(Stay(), null);
        Assert.True(connection.RegisterEndChatCall("call-cancelled"));
        Assert.True(connection.CancelEndChatCall("call-cancelled"));

        Assert.False(connection.ConfirmEndChatCall("call-cancelled"));
        Assert.False(connection.AcceptEndChatToolResult("call-cancelled"));
        Assert.Null(connection.EndChatResultReceivedAtUtc);
        Assert.False(connection.RegisterEndChatCall("call-cancelled"));
        Assert.True(connection.RegisterEndChatCall("call-next"));
        Assert.True(connection.TryClaimEndChatFlow());
    }

    [Fact]
    public void EndChatFlowCanOnlyBeClaimedOnce()
    {
        var connection = registry.Open(Stay(), null);

        Assert.True(connection.TryClaimEndChatFlow());
        Assert.False(connection.TryClaimEndChatFlow());
    }

    [Fact]
    public void EndSessionRevokesTheChatSessionsBearerAndForgetsItsVoiceSession()
    {
        var stay = Stay();
        var ended = registry.Open(stay, null);
        var unrelated = registry.Open(Stay(), null);

        registry.EndSession(stay.SessionId);

        Assert.Equal(VoiceRevocation.Ended, ended.Revocation);
        Assert.False(registry.TryAuthenticate(ended.Bearer, out _));
        Assert.True(registry.TryAuthenticate(unrelated.Bearer, out _));
        Assert.NotSame(ended.Session, registry.Open(stay, ended.Session.Id).Session);
    }

    [Fact]
    public void AbandonedVoiceSessionsAreDroppedAfterTheLifetime()
    {
        var abandoned = registry.Open(Stay(), null);
        registry.Close(abandoned);
        Assert.Equal(1, registry.SessionCount);

        time.Advance(VoiceSessionRegistry.ConnectionLifetime + TimeSpan.FromSeconds(1));
        registry.Open(Stay(), null);

        Assert.Equal(1, registry.SessionCount);
    }

    [Fact]
    public void ClosingTheActiveConnectionNotifiesSubscribersOnce()
    {
        var connection = registry.Open(Stay(), null);
        var closed = new List<int>();
        using var subscription = registry.SubscribeConnectionClosed(connection.Session.Id, closed.Add);

        registry.Close(connection);
        registry.Close(connection);

        Assert.Equal([1], closed);
    }

    [Fact]
    public void ExpiryOfTheActiveConnectionNotifiesSubscribers()
    {
        var connection = registry.Open(Stay(), null);
        var closed = new List<int>();
        using var subscription = registry.SubscribeConnectionClosed(connection.Session.Id, closed.Add);

        time.Advance(VoiceSessionRegistry.ConnectionLifetime);

        Assert.Equal(VoiceRevocation.Expired, connection.Revocation);
        Assert.Equal([1], closed);
    }

    [Fact]
    public void ReconnectDoesNotReportTheReplacedConnectionButReportsTheNewOne()
    {
        var stay = Stay();
        var first = registry.Open(stay, null);
        var closed = new List<int>();
        using var subscription = registry.SubscribeConnectionClosed(first.Session.Id, closed.Add);

        var second = registry.Open(stay, first.Session.Id);
        registry.Close(first);
        Assert.Equal(VoiceRevocation.Replaced, first.Revocation);
        Assert.Empty(closed);

        registry.Close(second);
        Assert.Equal([2], closed);
    }

    [Fact]
    public void EndingTheChatSessionDoesNotNotifyAndDropsSubscribers()
    {
        var stay = Stay();
        var connection = registry.Open(stay, null);
        var closed = new List<int>();
        using var subscription = registry.SubscribeConnectionClosed(connection.Session.Id, closed.Add);
        Assert.Equal(1, registry.ClosedSubscriberCount(connection.Session.Id));

        registry.EndSession(stay.SessionId);
        registry.Close(connection);

        Assert.Equal(VoiceRevocation.Ended, connection.Revocation);
        Assert.Empty(closed);
        Assert.Empty(connection.Session.ClosedSubscribers);
        Assert.Equal(0, registry.ClosedSubscriberCount(connection.Session.Id));
    }

    [Fact]
    public void AbandonedSessionRemovalDropsSubscribers()
    {
        var abandoned = registry.Open(Stay(), null);
        using var subscription = registry.SubscribeConnectionClosed(abandoned.Session.Id, _ => { });
        registry.Close(abandoned);

        time.Advance(VoiceSessionRegistry.ConnectionLifetime + TimeSpan.FromSeconds(1));
        registry.Open(Stay(), null);

        Assert.Empty(abandoned.Session.ClosedSubscribers);
    }

    [Fact]
    public void DisposedSubscriptionIsNotCalledAndIsRemoved()
    {
        var connection = registry.Open(Stay(), null);
        var called = false;
        var subscription = registry.SubscribeConnectionClosed(connection.Session.Id, _ => called = true);

        subscription.Dispose();
        subscription.Dispose();
        registry.Close(connection);

        Assert.False(called);
        Assert.Equal(0, registry.ClosedSubscriberCount(connection.Session.Id));
    }

    [Fact]
    public void SubscribingToAnUnknownVoiceSessionIsANoOp()
    {
        using var subscription = registry.SubscribeConnectionClosed(Guid.NewGuid(), _ => throw new InvalidOperationException());

        Assert.Equal(0, registry.SessionCount);
    }

    [Fact]
    public void ThrowingSubscriberDoesNotBreakRevocationOrOtherSubscribers()
    {
        var stay = Stay();
        var connection = registry.Open(stay, null);
        var closed = new List<int>();
        using var bad = registry.SubscribeConnectionClosed(connection.Session.Id, _ => throw new InvalidOperationException("bad"));
        using var good = registry.SubscribeConnectionClosed(connection.Session.Id, closed.Add);

        registry.Close(connection);

        Assert.Equal(VoiceRevocation.Closed, connection.Revocation);
        Assert.False(registry.TryAuthenticate(connection.Bearer, out _));
        Assert.Equal([1], closed);
        // Forget completed: the session is idle and reconnect gets a fresh connection.
        Assert.Equal(2, registry.Open(stay, connection.Session.Id).Number);
    }

    [Fact]
    public async Task SameTurnIdJoinsTheInFlightSubmission()
    {
        var session = registry.Open(Stay(), null).Session;
        var release = new TaskCompletionSource<ConciergeTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var submissions = 0;
        Task<ConciergeTurnResult> Submit()
        {
            Interlocked.Increment(ref submissions);
            return release.Task;
        }

        var first = session.JoinOrStart("v:a:1:1", Submit);
        var duplicate = session.JoinOrStart("v:a:1:1", Submit);
        var next = session.JoinOrStart("v:a:1:2", Submit);
        release.SetResult(new ConciergeTurnResult("v:a:1:1", "Answer", 1));
        await Task.WhenAll(first, duplicate, next);

        Assert.Same(first, duplicate);
        Assert.NotSame(first, next);
        Assert.Equal(2, submissions);
        Assert.Equal(0, session.InFlightCount);
    }

    private static ConciergeSessionContext Stay(DateTimeOffset? deadline = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), deadline ?? Now.AddDays(2));
}
