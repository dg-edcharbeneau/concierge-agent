using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Concierge.Contracts;

namespace Concierge.Web.Voice;

/// <summary>Why a voice connection's Think bearer stopped being valid.</summary>
public enum VoiceRevocation
{
    None,
    Closed,
    Expired,
    Replaced,
    Ended,
}

/// <summary>
/// In-process owner of voice sessions and their short-lived Think bearers. A voice session spans
/// manual reconnects for one chat session; each Deepgram connection gets its own bearer and number.
/// </summary>
public sealed class VoiceSessionRegistry(TimeProvider timeProvider)
{
    /// <summary>
    /// Deepgram cannot rotate Think headers on an open connection, so the bearer must outlive the
    /// connection; the relay closes the connection when it expires and Reconnect mints a new one.
    /// </summary>
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<Guid, VoiceSession> sessions = new();
    private readonly ConcurrentDictionary<string, VoiceConnection> connections = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens a new Deepgram connection for <paramref name="stay"/>. Reuses the voice session named by
    /// <paramref name="voiceSessionId"/> only when it belongs to the same chat session; any previous
    /// connection in that voice session is revoked.
    /// </summary>
    public VoiceConnection Open(ConciergeSessionContext stay, Guid? voiceSessionId)
    {
        ArgumentNullException.ThrowIfNull(stay);
        var now = timeProvider.GetUtcNow();
        RemoveAbandonedSessions(now);

        var session = voiceSessionId is { } id
            && sessions.TryGetValue(id, out var existing)
            && existing.Stay.SessionId == stay.SessionId
                ? existing
                : sessions.GetOrAdd(Guid.NewGuid(), newId => new VoiceSession(newId, stay));

        var bearer = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var expiresAt = Min(now + ConnectionLifetime, stay.EligibilityDeadlineUtc);
        var lifetime = expiresAt > now ? expiresAt - now : TimeSpan.Zero;

        VoiceConnection connection;
        VoiceConnection? previous;
        lock (session.Gate)
        {
            previous = session.Active;
            connection = new VoiceConnection(
                session, ++session.ConnectionCount, KeyOf(bearer), bearer, expiresAt, lifetime, timeProvider);
            session.Active = connection;
        }

        previous?.Revoke(VoiceRevocation.Replaced);
        connections[connection.Key] = connection;
        // Runs immediately if the stay deadline has already passed.
        connection.Revoked.Register(() => Forget(connection));
        return connection;
    }

    /// <summary>Returns the live connection that owns <paramref name="bearer"/>, if any.</summary>
    public bool TryAuthenticate(string? bearer, [NotNullWhen(true)] out VoiceConnection? connection)
    {
        connection = null;
        if (string.IsNullOrEmpty(bearer) || bearer.Length > 128
            || !connections.TryGetValue(KeyOf(bearer), out var found))
        {
            return false;
        }

        // The expiry timer may not have fired yet; the deadline is authoritative.
        if (timeProvider.GetUtcNow() >= found.ExpiresAtUtc)
        {
            found.Revoke(VoiceRevocation.Expired);
            return false;
        }

        if (found.Revocation != VoiceRevocation.None)
        {
            return false;
        }

        connection = found;
        return true;
    }

    /// <summary>Revokes the connection's bearer because its relay closed.</summary>
    public void Close(VoiceConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.Revoke(VoiceRevocation.Closed);
    }

    /// <summary>
    /// Revokes every voice bearer of a chat session and forgets its voice sessions. Admitted turns
    /// keep running; only new Think requests are refused.
    /// </summary>
    public void EndSession(Guid chatSessionId)
    {
        foreach (var session in sessions.Values.Where(session => session.Stay.SessionId == chatSessionId))
        {
            sessions.TryRemove(session.Id, out _);
            VoiceConnection? active;
            lock (session.Gate)
            {
                active = session.Active;
            }

            active?.Revoke(VoiceRevocation.Ended);
        }
    }

    internal int SessionCount => sessions.Count;

    private void Forget(VoiceConnection connection)
    {
        connections.TryRemove(new KeyValuePair<string, VoiceConnection>(connection.Key, connection));
        var session = connection.Session;
        lock (session.Gate)
        {
            if (ReferenceEquals(session.Active, connection))
            {
                session.Active = null;
                session.IdleSinceUtc = timeProvider.GetUtcNow();
            }
        }
    }

    // A guest who closes the tab without ending chat leaves a voice session with no connection.
    // Keep it long enough for a manual Reconnect, then drop it.
    private void RemoveAbandonedSessions(DateTimeOffset now)
    {
        foreach (var session in sessions.Values)
        {
            lock (session.Gate)
            {
                if (session.Active is null && session.InFlightCount == 0
                    && now - session.IdleSinceUtc > ConnectionLifetime)
                {
                    sessions.TryRemove(session.Id, out _);
                }
            }
        }
    }

    private static string KeyOf(string bearer) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer)));

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}

/// <summary>One guest voice mode: the chat session it belongs to and its in-flight Think turns.</summary>
public sealed class VoiceSession(Guid id, ConciergeSessionContext stay)
{
    private readonly Dictionary<string, Task<ConciergeTurnResult>> inFlight = new(StringComparer.Ordinal);

    public Guid Id { get; } = id;

    public ConciergeSessionContext Stay { get; } = stay;

    internal object Gate { get; } = new();

    internal int ConnectionCount { get; set; }

    internal VoiceConnection? Active { get; set; }

    internal DateTimeOffset IdleSinceUtc { get; set; }

    internal int InFlightCount
    {
        get
        {
            lock (Gate)
            {
                return inFlight.Count;
            }
        }
    }

    /// <summary>
    /// Joins the in-flight turn with <paramref name="turnId"/> or starts it with <paramref name="submit"/>.
    /// Deepgram can retry a Think call with the same history, so one ID must not submit twice.
    /// </summary>
    public Task<ConciergeTurnResult> JoinOrStart(string turnId, Func<Task<ConciergeTurnResult>> submit)
    {
        ArgumentNullException.ThrowIfNull(submit);
        lock (Gate)
        {
            if (inFlight.TryGetValue(turnId, out var running))
            {
                return running;
            }

            var task = RunAsync(turnId, submit);
            inFlight[turnId] = task;
            return task;
        }
    }

    private async Task<ConciergeTurnResult> RunAsync(string turnId, Func<Task<ConciergeTurnResult>> submit)
    {
        // Yielding keeps submission work out of the caller's lock and guarantees the entry is
        // registered before the finally block can remove it.
        await Task.Yield();
        try
        {
            return await submit();
        }
        finally
        {
            lock (Gate)
            {
                inFlight.Remove(turnId);
            }
        }
    }
}

/// <summary>One Deepgram connection: its Think bearer, number, and revocation signal.</summary>
public sealed class VoiceConnection
{
    private readonly CancellationTokenSource revocation;
    private int revokedAs;

    internal VoiceConnection(
        VoiceSession session,
        int number,
        string key,
        string bearer,
        DateTimeOffset expiresAtUtc,
        TimeSpan lifetime,
        TimeProvider timeProvider)
    {
        Session = session;
        Number = number;
        Key = key;
        Bearer = bearer;
        ExpiresAtUtc = expiresAtUtc;
        revocation = new CancellationTokenSource(lifetime, timeProvider);
        // The timer cancels the token directly; record why so the relay can tell the guest.
        revocation.Token.Register(() => Interlocked.CompareExchange(
            ref revokedAs, (int)VoiceRevocation.Expired, (int)VoiceRevocation.None));
    }

    public VoiceSession Session { get; }

    /// <summary>1-based connection number within the voice session.</summary>
    public int Number { get; }

    /// <summary>Opaque Think bearer. Only the Deepgram Settings message may carry it.</summary>
    public string Bearer { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>Cancelled when the bearer is revoked or expires.</summary>
    public CancellationToken Revoked => revocation.Token;

    public VoiceRevocation Revocation => (VoiceRevocation)Volatile.Read(ref revokedAs);

    internal string Key { get; }

    internal void Revoke(VoiceRevocation reason)
    {
        if (Interlocked.CompareExchange(ref revokedAs, (int)reason, (int)VoiceRevocation.None)
            == (int)VoiceRevocation.None)
        {
            revocation.Cancel();
        }
    }
}
