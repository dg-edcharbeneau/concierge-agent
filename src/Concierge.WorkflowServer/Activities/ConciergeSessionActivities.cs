using Concierge.Contracts;
using Concierge.WorkflowServer.Setup;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace Concierge.WorkflowServer.Activities;

/// <summary>
/// Session operations started by Web as Temporal Standalone Activities (no wrapping workflow).
/// Each activity validates its own input because it is the first worker-side code to see the payload.
/// </summary>
public sealed class ConciergeSessionActivities(ConciergeSessionService sessions)
{
    /// <summary>
    /// Binds the credential digest to the guest's eligible stay; returns <see langword="null"/> when no stay matches.
    /// </summary>
    /// <exception cref="ApplicationFailureException">Non-retryable when the request is malformed.</exception>
    [Activity(ConciergeActivityNames.VerifySession)]
    public Task<ConciergeSessionContext?> VerifyAsync(
        VerifyConciergeSessionActivityRequest request)
    {
        if (request is null
            || request.SessionId == Guid.Empty
            || request.CredentialSha256 is not { Length: 32 }
            || string.IsNullOrWhiteSpace(request.LastName)
            || request.LastName.Trim().Length > 100
            || !RoomNumberFormat.IsValid(request.RoomNumber))
        {
            throw InvalidRequest();
        }

        return sessions.VerifyAsync(request, ActivityExecutionContext.Current.CancellationToken);
    }

    /// <summary>
    /// Resolves an active, unexpired session from a credential digest; returns <see langword="null"/> when none exists.
    /// </summary>
    /// <exception cref="ApplicationFailureException">Non-retryable when the request is malformed.</exception>
    [Activity(ConciergeActivityNames.AuthenticateSession)]
    public Task<ConciergeSessionContext?> AuthenticateAsync(
        AuthenticateConciergeSessionActivityRequest request)
    {
        if (request?.CredentialSha256 is not { Length: 32 })
        {
            throw InvalidRequest();
        }

        return sessions.AuthenticateAsync(
            request.CredentialSha256,
            ActivityExecutionContext.Current.CancellationToken);
    }

    /// <summary>
    /// Ends the session so its credential can no longer authenticate. Safe to retry.
    /// </summary>
    /// <exception cref="ApplicationFailureException">Non-retryable when the request is malformed.</exception>
    [Activity(ConciergeActivityNames.InvalidateSession)]
    public Task InvalidateAsync(InvalidateConciergeSessionActivityRequest request)
    {
        if (request is null || request.SessionId == Guid.Empty || request.StayId == Guid.Empty)
        {
            throw InvalidRequest();
        }

        return sessions.InvalidateAsync(request, ActivityExecutionContext.Current.CancellationToken);
    }

    // The message names no field values, so guest data never reaches failure details.
    private static ApplicationFailureException InvalidRequest() =>
        new(
            "Invalid session request.",
            errorType: ConciergeActivityNames.SessionInvalidRequestErrorType,
            nonRetryable: true);
}
