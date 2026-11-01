using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Concierge.Contracts;

/// <summary>Server-authenticated utterance provenance, separate from the exact-text retry ID.</summary>
public sealed record VoiceUtterance(Guid SessionId, int Connection, int Ordinal)
{
    public string Family => FormattableString.Invariant($"v:{SessionId:N}:{Connection}:{Ordinal}");

    public string TurnId(string text) =>
        $"{Family}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))}";

    public static VoiceUtterance? FromTurnId(string turnId, string text)
    {
        var parts = turnId.Split(':');
        if (parts.Length != 5 || parts[0] != "v"
            || !Guid.TryParseExact(parts[1], "N", out var session)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var connection)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal)
            || connection <= 0 || ordinal <= 0)
        {
            return null;
        }

        var identity = new VoiceUtterance(session, connection, ordinal);
        return StringComparer.Ordinal.Equals(identity.TurnId(text), turnId) ? identity : null;
    }
}

/// <summary>
/// Bounded revision authority carried across runs. Superseded IDs never become current on retry.
/// An action receipt is retained even when its conversational reply is replaced.
/// </summary>
public sealed record ConciergeVoiceRevision(
    string Family,
    string LatestTurnId,
    IReadOnlyList<string> SupersededTurnIds,
    string Fingerprint,
    string? ActionReceipt = null,
    ConciergeEndChatFunctionCall? EndChatFunctionCall = null);

/// <summary>Only the last reversible commit can be withdrawn, never a later interaction.</summary>
public sealed record ConciergeVoiceRevisionCheckpoint(
    string TurnId,
    long Sequence,
    IReadOnlyList<ConciergeStayMemoryItem> StayMemory,
    IReadOnlyList<ConciergeTranscriptTurn> RecentTurns,
    ConciergeRideProposal? RideProposal,
    ConciergeRideCancellationProposal? CancellationProposal,
    ConciergePlacePresentation? NearbyPlaces,
    ConciergePlacePresentation? PlaceClarification);
