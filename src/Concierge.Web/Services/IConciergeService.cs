using Concierge.Contracts;

namespace Concierge.Web.Services;

/// <summary>
/// In-process concierge operations shared by the HTTP routes and Interactive Server UI.
/// Throws <see cref="ConciergeApiException"/> for expected API failures.
/// </summary>
public interface IConciergeService
{
    /// <summary>
    /// Verifies a guest by last name and room number and issues a new bearer credential for that stay.
    /// Throws <see cref="ConciergeApiException"/> with 400 for invalid input and 404 when no eligible stay matches.
    /// </summary>
    Task<ConciergeSessionResponse> VerifyAsync(
        ConciergeVerificationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a guest message to the stay workflow and returns the concierge reply.
    /// Reusing <see cref="ConciergeTurnRequest.TurnId"/> with the same message returns the original reply;
    /// reusing it with a different message throws a 409 <see cref="ConciergeApiException"/>.
    /// Throws a 401 <see cref="ConciergeApiException"/> when the session has no verified guest profile.
    /// </summary>
    Task<ConciergeTurnResult> SubmitTurnAsync(
        string? bearerCredential,
        ConciergeTurnRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stay transcript, including turns not yet flushed to PostgreSQL.
    /// Throws a 401 <see cref="ConciergeApiException"/> when the credential is invalid or expired.
    /// </summary>
    Task<ConciergeHistoryResult> ReadHistoryAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes pending transcript turns and invalidates the credential. Stay memory is kept, so the
    /// guest can verify again to continue the same conversation. Also revokes every voice bearer of the session.
    /// </summary>
    Task EndAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticates a chat credential for voice and reports whether the stay already has turns.
    /// Throws a 401 <see cref="ConciergeApiException"/> when the credential is invalid or expired, or
    /// when the session has no verified guest profile.
    /// </summary>
    Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits a voice turn for an already authorized session through the same stay workflow update as text.
    /// The same <paramref name="turnId"/> and message return the original reply.
    /// Throws a 401 <see cref="ConciergeApiException"/> when the session has no verified guest profile.
    /// </summary>
    Task<ConciergeTurnResult> SubmitVoiceTurnAsync(
        ConciergeSessionContext session,
        string turnId,
        string userMessage,
        CancellationToken cancellationToken = default,
        bool enableEndChatFunction = false);

    /// <summary>
    /// Marks a confirmed browser-rendered voice interruption through the stay workflow.
    /// The operation is idempotent and never cancels the admitted turn.
    /// </summary>
    Task MarkVoiceTurnInterruptedAsync(
        ConciergeSessionContext session,
        string turnId,
        CancellationToken cancellationToken = default);
}

/// <summary>A chat session authorized for voice; <paramref name="HasTurns"/> selects the greeting.</summary>
public sealed record ConciergeVoiceAuthorization(ConciergeSessionContext Session, bool HasTurns);
