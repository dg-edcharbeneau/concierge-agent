using System.Text;

namespace Concierge.Contracts;

public sealed record ConciergeVerificationRequest(string LastName, string RoomNumber);

public sealed record ConciergeSessionResponse(string Credential, DateTimeOffset ExpiresAtUtc, Guid StayId = default);

public sealed record ConciergeTurnRequest(string TurnId, string UserMessage, string Channel);

public sealed record ConciergeApiError(string Code, string Message);

public sealed class ConciergeApiException(
    string code,
    string message,
    int statusCode) : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;
}

public static class ConciergeTurnLimits
{
    public const int MaxTurnIdLength = 128;
    public const int MaxUserMessageUtf8Bytes = 2000;

    /// <summary>
    /// Returns whether <paramref name="message"/> fits the UTF-8 byte budget enforced by the API and the stay workflow.
    /// </summary>
    public static bool IsUserMessageWithinLimit(string message) =>
        Encoding.UTF8.GetByteCount(message) <= MaxUserMessageUtf8Bytes;

    /// <summary>
    /// Returns whether <paramref name="channel"/> is a supported channel (<c>web</c> or <c>voice</c>); matching is case-sensitive.
    /// </summary>
    public static bool IsChannel(string? channel) =>
        StringComparer.Ordinal.Equals(channel, "web")
        || StringComparer.Ordinal.Equals(channel, "voice");
}

public sealed record ConciergeSessionContext(
    Guid SessionId,
    Guid StayId,
    DateTimeOffset EligibilityDeadlineUtc);

public sealed record VerifyConciergeSessionActivityRequest(
    Guid SessionId,
    byte[] CredentialSha256,
    string LastName,
    string RoomNumber);

public sealed record AuthenticateConciergeSessionActivityRequest(byte[] CredentialSha256);

public sealed record InvalidateConciergeSessionActivityRequest(Guid SessionId, Guid StayId);

/// <summary>
/// Session activity type names. Web starts these as Temporal Standalone Activities by name because it
/// cannot reference the WorkflowServer activity types.
/// </summary>
public static class ConciergeActivityNames
{
    public const string VerifySession = "VerifyAsync";
    public const string AuthenticateSession = "AuthenticateAsync";
    public const string InvalidateSession = "InvalidateAsync";
    public const string SessionInvalidRequestErrorType = "ConciergeSessionInvalidRequest";
}

public static class ConciergeWorkflowNames
{
    public const string TaskQueue = "Concierge.WorkflowServer-tq";
    public const string StayWorkflow = "Concierge.Stay";
    public const string SubmitTurnUpdate = "SubmitTurn";
    public const string MarkTurnInterruptedUpdate = "MarkTurnInterrupted";
    public const string HistoryUpdate = "History";
    public const string EndInteractionUpdate = "EndInteraction";
    public const string TurnConflictErrorType = "ConciergeTurnConflict";
    public const string StayExpiredErrorType = "ConciergeStayExpired";
}

public sealed class ConciergePersistenceOptions
{
    public const string SectionName = "Concierge:Persistence";

    public const int DefaultFlushBatchSize = 5;
    public const int MaxFlushBatchSize = 100;

    public int FlushIntervalSeconds { get; set; } = 30;

    public int FlushBatchSize { get; set; } = DefaultFlushBatchSize;
}
