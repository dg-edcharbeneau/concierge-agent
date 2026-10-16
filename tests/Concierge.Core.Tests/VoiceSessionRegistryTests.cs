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
