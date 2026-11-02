namespace Concierge.Web.Services;

public sealed record PendingTurnState(
    string TurnId,
    string UserMessage,
    string Channel,
    DateTimeOffset SubmittedAtUtc);

public sealed class ChatSessionState
{
    public Guid StayId { get; set; }

    public Guid SessionId { get; set; }

    public string? Credential { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public PendingTurnState? Pending { get; set; }

    public string Draft { get; set; } = string.Empty;
}
