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
    /// guest can verify again to continue the same conversation.
    /// </summary>
    Task EndAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default);
}
