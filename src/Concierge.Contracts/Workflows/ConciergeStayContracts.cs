namespace Concierge.Contracts;

/// <summary>Stable contract values for the one voice-only end-chat function.</summary>
public static class ConciergeEndChat
{
    public const string FunctionName = "end_chat";
    public const string ModelOptionKey = "concierge.voice.end_chat";
    public const string Farewell = "Goodbye, and thank you for staying with us.";
}

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
    int TranscriptFlushBatchSize = ConciergePersistenceOptions.DefaultFlushBatchSize,
    IReadOnlyList<ConciergeStayMemoryItem>? StayMemory = null,
    ConciergeVerifiedGuest? VerifiedGuest = null);

/// <summary>
/// A bounded, guest-reported statement retained for one stay. It is linked to the stable
/// transcript sequence; a correction replaces the active item while the transcript keeps both.
/// </summary>
public sealed record ConciergeStayMemoryItem(
    string Category,
    string Subject,
    string Statement,
    long SourceSequence,
    long? SupersedesSequence = null);

/// <summary>VerifiedGuest is supplied by Web authentication, never bound from guest request JSON.</summary>
public sealed record ConciergeTurnSubmission(
    string TurnId,
    string UserMessage,
    string Channel,
    ConciergeVerifiedGuest? VerifiedGuest = null,
    bool EnableEndChatFunction = false);

/// <summary>A completed guest/assistant exchange; failed turns are never recorded.</summary>
public sealed record ConciergeTranscriptTurn(
    string TurnId,
    long Sequence,
    string Channel,
    string GuestMessage,
    string AssistantMessage,
    DateTimeOffset OccurredAtUtc,
    bool Interrupted = false);

public sealed record ConciergeTurnResult(
    string TurnId,
    string AssistantMessage,
    long Sequence,
    ConciergeEndChatFunctionCall? EndChatFunctionCall = null);

/// <summary>The model-generated client-side end-chat call forwarded to Deepgram.</summary>
public sealed record ConciergeEndChatFunctionCall(string Id, string Name, string Arguments);

public sealed record ConciergeTurnInterruption(string TurnId);

public sealed record ConciergeHistoryResult(IReadOnlyList<ConciergeTranscriptTurn> Turns);

public sealed record PersistTranscriptRequest(
    Guid StayId,
    IReadOnlyList<ConciergeTranscriptTurn> Turns);

public sealed record ReadTranscriptRequest(Guid StayId);

public sealed record ReadNextTranscriptSequenceRequest(Guid StayId);

public sealed record FindTranscriptTurnRequest(Guid StayId, string TurnId);

public sealed record MarkTranscriptTurnInterruptedRequest(Guid StayId, string TurnId);
