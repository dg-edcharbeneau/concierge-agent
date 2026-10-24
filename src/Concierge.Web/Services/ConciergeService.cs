using System.Buffers.Text;
using System.Security.Cryptography;
using Concierge.Contracts;
using Concierge.Web.Voice;
using Microsoft.Extensions.Options;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;

namespace Concierge.Web.Services;

/// <summary>Authorizes guest sessions and delegates conversation operations to Temporal.</summary>
public sealed class ConciergeService(
    ITemporalClient temporalClient,
    IOptions<ConciergePersistenceOptions> persistenceOptions,
    VoiceSessionRegistry voiceSessions,
    ILogger<ConciergeService> logger) : IConciergeService
{
    /// <inheritdoc/>
    public async Task<ConciergeSessionResponse> VerifyAsync(
        ConciergeVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw InvalidRequest("Provide a verification request with LastName and RoomNumber.");
        }

        if (string.IsNullOrWhiteSpace(request.LastName) || request.LastName.Trim().Length > ConciergeVerifiedGuest.MaxNameLength)
        {
            throw InvalidRequest($"Enter LastName (up to {ConciergeVerifiedGuest.MaxNameLength} characters).");
        }

        if (!RoomNumberFormat.IsValid(request.RoomNumber))
        {
            throw InvalidRequest("Enter RoomNumber as 1–4 ASCII digits with no leading zero.");
        }

        var bearerBytes = RandomNumberGenerator.GetBytes(32);
        var credential = Base64Url.EncodeToString(bearerBytes);
        var credentialSha256 = SHA256.HashData(bearerBytes);
        var sessionId = Guid.NewGuid();
        var verified = await ExecuteSessionActivityAsync<ConciergeSessionContext?>(
            ConciergeActivityNames.VerifySession,
            SessionOperation.Verify,
            sessionId,
            new VerifyConciergeSessionActivityRequest(
                sessionId,
                credentialSha256,
                request.LastName,
                request.RoomNumber),
            cancellationToken);
        if (verified is null)
        {
            throw new ConciergeApiException(
                "guest_not_found",
                "Guest not found",
                StatusCodes.Status404NotFound);
        }

        return new ConciergeSessionResponse(
            credential, verified.EligibilityDeadlineUtc, verified.StayId);
    }

    /// <inheritdoc/>
    public async Task<ConciergeTurnResult> SubmitTurnAsync(
        string? bearerCredential,
        ConciergeTurnRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateTurn(request);
        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        return await ExecuteTurnAsync(session, request, updateId: null, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ConciergeVoiceAuthorization> AuthorizeVoiceAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default)
    {
        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        // Every voice connection only submits turns, so refuse it before it starts the stay workflow.
        RequireVerifiedGuest(session);
        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            var history = await workflow.ExecuteUpdateAsync<ConciergeHistoryResult>(
                ConciergeWorkflowNames.HistoryUpdate,
                [],
                UpdateOptions(cancellationToken));
            return new ConciergeVoiceAuthorization(session, history.Turns.Count > 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapTemporalFailure(exception, nameof(AuthorizeVoiceAsync));
        }
    }

    /// <inheritdoc/>
    public async Task<ConciergeTurnResult> SubmitVoiceTurnAsync(
        ConciergeSessionContext session,
        string turnId,
        string userMessage,
        CancellationToken cancellationToken = default,
        bool enableEndChatFunction = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var request = new ConciergeTurnRequest(turnId, userMessage, "voice");
        ValidateTurn(request);
        // Deepgram retries reuse the payload, so the TurnId is also the Temporal update ID: a retry
        // after a lost response attaches to the admitted update, even while it is still running.
        return await ExecuteTurnAsync(session, request, updateId: turnId, cancellationToken, enableEndChatFunction);
    }

    /// <inheritdoc/>
    public async Task MarkVoiceTurnInterruptedAsync(
        ConciergeSessionContext session,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(turnId) || turnId.Length > ConciergeTurnLimits.MaxTurnIdLength)
        {
            throw new ConciergeApiException(
                "invalid_request",
                $"Provide a nonblank TurnId of at most {ConciergeTurnLimits.MaxTurnIdLength} characters.",
                StatusCodes.Status400BadRequest);
        }

        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            await workflow.ExecuteUpdateAsync(
                ConciergeWorkflowNames.MarkTurnInterruptedUpdate,
                [new ConciergeTurnInterruption(turnId)],
                UpdateOptions(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapTemporalFailure(exception, nameof(MarkVoiceTurnInterruptedAsync));
        }
    }

    private async Task<ConciergeTurnResult> ExecuteTurnAsync(
        ConciergeSessionContext session,
        ConciergeTurnRequest request,
        string? updateId,
        CancellationToken cancellationToken,
        bool enableEndChatFunction = false)
    {
        // Outside the try below, which would map this rejection to 503.
        RequireVerifiedGuest(session);
        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            var options = UpdateOptions(cancellationToken);
            options.Id = updateId;
            // Text turns keep a generated update ID so a reused TurnId with different text still
            // reaches the workflow's fingerprint check and is rejected as turn_conflict.
            return await workflow.ExecuteUpdateAsync<ConciergeTurnResult>(
                ConciergeWorkflowNames.SubmitTurnUpdate,
                [new ConciergeTurnSubmission(
                request.TurnId, request.UserMessage, request.Channel, session.VerifiedGuest, enableEndChatFunction)],
                options);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapTemporalFailure(exception, nameof(SubmitTurnAsync));
        }
    }

    /// <summary>
    /// Every turn requires a verified guest profile before the stay workflow is called.
    /// </summary>
    private static void RequireVerifiedGuest(ConciergeSessionContext session)
    {
        if (session.VerifiedGuest is null)
        {
            throw new ConciergeApiException(
                "unauthorized",
                "Your guest profile is missing from this session. Sign in again with your last name and room number to continue.",
                StatusCodes.Status401Unauthorized);
        }
    }

    private static void ValidateTurn(ConciergeTurnRequest? request)
    {
        if (request is null)
        {
            throw InvalidRequest("Provide a turn request with TurnId, UserMessage, and Channel.");
        }

        if (string.IsNullOrWhiteSpace(request.TurnId)
            || request.TurnId.Length > ConciergeTurnLimits.MaxTurnIdLength)
        {
            throw InvalidRequest(
                $"Provide a nonblank TurnId of at most {ConciergeTurnLimits.MaxTurnIdLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.UserMessage)
            || !ConciergeTurnLimits.IsUserMessageWithinLimit(request.UserMessage))
        {
            throw InvalidRequest(
                $"Enter a nonblank UserMessage of at most {ConciergeTurnLimits.MaxUserMessageUtf8Bytes} UTF-8 bytes.");
        }

        if (!ConciergeTurnLimits.IsChannel(request.Channel))
        {
            throw InvalidRequest("Set Channel to 'web' or 'voice' (case-sensitive).");
        }
    }

    /// <inheritdoc/>
    public async Task<ConciergeHistoryResult> ReadHistoryAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default)
    {
        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            return await workflow.ExecuteUpdateAsync<ConciergeHistoryResult>(
                ConciergeWorkflowNames.HistoryUpdate,
                [],
                UpdateOptions(cancellationToken));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapTemporalFailure(exception, nameof(ReadHistoryAsync));
        }
    }

    /// <inheritdoc/>
    public async Task EndAsync(
        string? bearerCredential,
        CancellationToken cancellationToken = default)
    {
        var session = await AuthenticateAsync(bearerCredential, cancellationToken);
        // Voice stops admitting new turns as soon as End starts, even if the flush below fails.
        voiceSessions.EndSession(session.SessionId);
        try
        {
            var workflow = await GetStayWorkflowAsync(session, cancellationToken);
            await workflow.ExecuteUpdateAsync(
                ConciergeWorkflowNames.EndInteractionUpdate,
                [],
                UpdateOptions(cancellationToken));

            // The update is idempotent, so a retry after an ambiguous response is safe.
            await ExecuteSessionActivityAsync(
                ConciergeActivityNames.InvalidateSession,
                SessionOperation.Invalidate,
                session.SessionId,
                new InvalidateConciergeSessionActivityRequest(session.SessionId, session.StayId),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not ConciergeApiException)
        {
            throw MapTemporalFailure(exception, nameof(EndAsync));
        }
    }

    private async Task<ConciergeSessionContext> AuthenticateAsync(
        string? bearerCredential,
        CancellationToken cancellationToken)
    {
        if (!TryHashCredential(bearerCredential, out var credentialSha256))
        {
            throw Unauthorized();
        }

        // The ID only correlates this request; the credential digest selects the session.
        var requestId = Guid.NewGuid();
        return await ExecuteSessionActivityAsync<ConciergeSessionContext?>(
                ConciergeActivityNames.AuthenticateSession,
                SessionOperation.Authenticate,
                requestId,
                new AuthenticateConciergeSessionActivityRequest(credentialSha256),
                cancellationToken)
            ?? throw Unauthorized();
    }

    /// <summary>Runs a session activity that returns <typeparamref name="TResult"/>.</summary>
    private Task<TResult> ExecuteSessionActivityAsync<TResult>(
        string activityName,
        SessionOperation operation,
        Guid sessionId,
        object request,
        CancellationToken cancellationToken) =>
        RunSessionActivityAsync(
            activityName,
            operation,
            sessionId,
            request,
            (handle, rpcOptions) => handle.GetResultAsync<TResult>(rpcOptions),
            cancellationToken);

    /// <summary>Runs a session activity that has no result.</summary>
    private Task ExecuteSessionActivityAsync(
        string activityName,
        SessionOperation operation,
        Guid sessionId,
        object request,
        CancellationToken cancellationToken) =>
        RunSessionActivityAsync(
            activityName,
            operation,
            sessionId,
            request,
            async (handle, rpcOptions) =>
            {
                await handle.GetResultAsync(rpcOptions);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Runs one session operation as a Temporal Standalone Activity and waits for its result.
    /// Every call gets a unique activity ID, so the default ID reuse/conflict policies never attach
    /// one request to another request's execution.
    /// </summary>
    private async Task<TResult> RunSessionActivityAsync<TResult>(
        string activityName,
        SessionOperation operation,
        Guid sessionId,
        object request,
        Func<ActivityHandle, RpcOptions, Task<TResult>> getResultAsync,
        CancellationToken cancellationToken)
    {
        var operationName = operation.ToString();
        var activityId =
            $"concierge-session-{sessionId:N}-{operationName.ToLowerInvariant()}-{Guid.NewGuid():N}";
        try
        {
            var handle = await temporalClient.StartActivityAsync(
                activityName,
                [request],
                new StartActivityOptions(activityId, TemporalWorkerConnection.TaskQueue)
                {
                    StartToCloseTimeout = TimeSpan.FromSeconds(20),
                    RetryPolicy = new RetryPolicy
                    {
                        InitialInterval = TimeSpan.FromMilliseconds(500),
                        MaximumInterval = TimeSpan.FromSeconds(2),
                        MaximumAttempts = 3,
                    },
                    Rpc = new RpcOptions { CancellationToken = cancellationToken },
                });

            return await getResultAsync(handle, new RpcOptions { CancellationToken = cancellationToken });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw MapTemporalFailure(exception, operationName);
        }
    }

    private async Task<WorkflowHandle> GetStayWorkflowAsync(
        ConciergeSessionContext session,
        CancellationToken cancellationToken)
    {
        var workflowId = $"concierge-stay-{session.StayId:N}";
        // Start (or join, via UseExisting) only to guarantee the stay workflow exists. Do not use the
        // returned handle: it pins FirstExecutionRunId to the *current* run ID, and after Continue-As-New
        // the current run's first-execution run ID is the chain's original run, so updates sent through
        // that handle fail with "workflow execution not found". An unpinned handle targets the latest run.
        await temporalClient.StartWorkflowAsync(
            ConciergeWorkflowNames.StayWorkflow,
            [
                new ConciergeStayWorkflowInput(
                    session.StayId,
                    session.EligibilityDeadlineUtc,
                    persistenceOptions.Value.FlushIntervalSeconds,
                    TranscriptFlushBatchSize: persistenceOptions.Value.FlushBatchSize,
                    VerifiedGuest: session.VerifiedGuest),
            ],
            new WorkflowOptions(workflowId, TemporalWorkerConnection.TaskQueue)
            {
                IdReusePolicy = WorkflowIdReusePolicy.AllowDuplicate,
                IdConflictPolicy = WorkflowIdConflictPolicy.UseExisting,
                Rpc = new RpcOptions { CancellationToken = cancellationToken },
            });
        return temporalClient.GetWorkflowHandle(workflowId);
    }

    private static WorkflowUpdateOptions UpdateOptions(CancellationToken cancellationToken) =>
        new() { Rpc = new RpcOptions { CancellationToken = cancellationToken } };

    private static bool TryHashCredential(string? credential, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 128)
        {
            return false;
        }

        try
        {
            var bytes = Base64Url.DecodeFromChars(credential);
            if (bytes.Length != 32)
            {
                return false;
            }

            hash = SHA256.HashData(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private ConciergeApiException MapTemporalFailure(Exception exception, string operation)
    {
        // Answer "which operation failed?" and "what exception types caused it?"
        // without logging exception messages, stacks, payloads, or guest/session identifiers.
        logger.LogWarning(
            "Concierge Temporal operation {Operation} failed. Cause types: {CauseTypes}.",
            operation,
            ConciergeDiagnostics.ExceptionTypeChain(exception));

        return (exception as WorkflowUpdateFailedException)?.InnerException is ApplicationFailureException failure
            ? failure.ErrorType switch
            {
                ConciergeWorkflowNames.TurnConflictErrorType => new ConciergeApiException(
                    "turn_conflict",
                    "The turn identifier was already used.",
                    StatusCodes.Status409Conflict),
                ConciergeWorkflowNames.StayExpiredErrorType => Unauthorized(),
                _ => Unavailable(),
            }
            : Unavailable();
    }

    private enum SessionOperation
    {
        Verify,
        Authenticate,
        Invalidate,
    }

    private static ConciergeApiException InvalidRequest(string message) =>
        new("invalid_request", message, StatusCodes.Status400BadRequest);

    private static ConciergeApiException Unauthorized() =>
        new("unauthorized", "Authenticate again to continue.", StatusCodes.Status401Unauthorized);

    private static ConciergeApiException Unavailable() =>
        new(
            "unavailable",
            "The concierge is temporarily unavailable. Please try again.",
            StatusCodes.Status503ServiceUnavailable);
}
