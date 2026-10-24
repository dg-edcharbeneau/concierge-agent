using Temporalio.Extensions.WorkflowStreams;

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
    IReadOnlyList<ConciergeTranscriptTurn>? RecentTurns = null,
    int TranscriptFlushBatchSize = ConciergePersistenceOptions.DefaultFlushBatchSize,
    IReadOnlyList<ConciergeStayMemoryItem>? StayMemory = null,
    ConciergeVerifiedGuest? VerifiedGuest = null,
    WorkflowStreamState? RideStreamState = null,
    long RideReadOffset = 0,
    ConciergeRideProposal? PendingRideProposal = null,
    ConciergeRideCancellationProposal? PendingRideCancellationProposal = null,
    ConciergeRideCancellationOutcome? LastConfirmedCancellation = null,
    ConciergeRideProposal? ConsumedRideOffer = null,
    long ConsumedRideOfferSequence = -1);

/// <summary>
/// A route-reviewed ride offer that stays open for the next two guest turns, so one pause does not
/// expire it. All fields come from trusted session state and the route activity, not confirmation arguments.
/// </summary>
public sealed record ConciergeRideProposal(
    Guid ProposalId,
    long ProposedSequence,
    ConciergeRidePlace Pickup,
    ConciergeRidePlace Dropoff,
    bool Immediate,
    DateTimeOffset? PickupAtUtc,
    double RouteDurationSeconds);

/// <summary>
/// A specific, verified ride offered for cancellation. The workflow keeps it open for the next two
/// guest turns; model arguments cannot substitute another ride.
/// </summary>
public sealed record ConciergeRideCancellationProposal(
    Guid RideId,
    long ProposedSequence,
    string Status,
    string Pickup,
    string Dropoff,
    DateTimeOffset PickupAt);

/// <summary>
/// One confirmed, terminal cancellation retained for this stay. Turn provenance is for durable
/// bookkeeping, not evidence that two voice callbacks belong to the same utterance.
/// </summary>
public sealed record ConciergeRideCancellationOutcome(
    Guid RideId,
    string Dropoff,
    DateTimeOffset PickupAt,
    string TurnId,
    long Sequence);

public sealed record ConciergeRidePlace(
    string Name,
    double Latitude,
    double Longitude);

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
