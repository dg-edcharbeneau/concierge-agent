namespace Concierge.Contracts;

/// <summary>
/// Stay workflow input. The server binds the stay and absolute eligibility deadline from a
/// verified session; Continue-As-New carries both unchanged with the bounded conversation context.
/// </summary>
public sealed record ConciergeStayWorkflowInput(
    Guid StayId,
    DateTimeOffset EligibilityDeadlineUtc,
    int TranscriptFlushIntervalSeconds,
    long NextSequence = 0,
    string? ContinuitySummary = null,
    IReadOnlyList<ConciergeTranscriptTurn>? RecentTurns = null,
    int TranscriptFlushBatchSize = ConciergePersistenceOptions.DefaultFlushBatchSize);

public sealed record ConciergeTurnSubmission(string TurnId, string UserMessage, string Channel);

/// <summary>A completed guest/assistant exchange; failed turns are never recorded.</summary>
public sealed record ConciergeTranscriptTurn(
    string TurnId,
    long Sequence,
    string Channel,
    string GuestMessage,
    string AssistantMessage,
    DateTimeOffset OccurredAtUtc,
    bool Interrupted = false);

public sealed record ConciergeTurnResult(string TurnId, string AssistantMessage, long Sequence);

public sealed record ConciergeTurnInterruption(string TurnId);

public sealed record ConciergeHistoryResult(IReadOnlyList<ConciergeTranscriptTurn> Turns);

public sealed record PersistTranscriptRequest(
    Guid StayId,
    IReadOnlyList<ConciergeTranscriptTurn> Turns);

public sealed record ReadTranscriptRequest(Guid StayId);

public sealed record FindTranscriptTurnRequest(Guid StayId, string TurnId);

public sealed record MarkTranscriptTurnInterruptedRequest(Guid StayId, string TurnId);
