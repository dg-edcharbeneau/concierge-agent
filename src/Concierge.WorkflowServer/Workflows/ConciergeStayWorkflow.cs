using System.Security.Cryptography;
using System.Text;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Activities;
using Concierge.WorkflowServer.Setup;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TemporalCommunity.Extensions.AI;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Concierge.WorkflowServer.Workflows;

/// <summary>
/// One durable workflow per eligible stay. Text and voice sessions for the stay share this
/// context; turns run one at a time in arrival order. Model, retrieval, and database work
/// run in activities. Completed turns are buffered here and flushed to PostgreSQL when the
/// configured batch size is reached or the configured interval expires, whichever comes first,
/// and also at interaction end, before Continue-As-New, and at stay expiry.
/// </summary>
[Workflow(ConciergeWorkflowNames.StayWorkflow)]
public sealed class ConciergeStayWorkflow
{
    public const string TurnConflictErrorType = ConciergeWorkflowNames.TurnConflictErrorType;
    public const string StayExpiredErrorType = ConciergeWorkflowNames.StayExpiredErrorType;
    public const string UnavailableErrorType = "ConciergeUnavailable";

    /// <summary>Previous complete turns sent verbatim; older turns are summarized.</summary>
    public const int RecentTurnWindow = 6;

    /// <summary>Provider-enforced output cap for the single continuity summary.</summary>
    public const int SummaryMaxOutputTokens = 1024;

    /// <summary>Provider-enforced output cap for each guest answer.</summary>
    public const int AnswerMaxOutputTokens = 1024;

    private const string TranscriptBatchSizePatch = "transcript-flush-batch-size";

    /// <summary>
    /// Allowance for the fixed instructions, prompt labels, evidence separators, and
    /// per-message chat-template tokens of one answer request.
    /// </summary>
    public const int FixedPromptAllowanceTokens = 1024;

    /// <summary>
    /// Worst-case tokens for one answer request plus its reply, bounded by UTF-8 bytes for
    /// byte-level BPE tokenizers. Knowledge passages are counted in characters because the
    /// Hotel ZGY corpus is ASCII Markdown.
    /// </summary>
    public const int WorstCaseContextTokens =
        FixedPromptAllowanceTokens
        + SummaryMaxOutputTokens
        + RecentTurnWindow * (ConciergeTurnLimits.MaxUserMessageUtf8Bytes + AnswerMaxOutputTokens)
        + HotelKnowledgeActivities.PassageLimit * KnowledgeChunker.MaximumChunkCharacters
        + ConciergeTurnLimits.MaxUserMessageUtf8Bytes
        + AnswerMaxOutputTokens;

    private const string SummaryInstructions =
        "You maintain a short continuity summary of a hotel guest's conversation with the "
        + "Hotel ZGY concierge. Merge the existing summary with the older turns provided. "
        + "Keep the guest's stated preferences, decisions, corrections, qualifiers, and "
        + "unresolved requests, and cite turn IDs in brackets. Do not record hotel facts as "
        + "authoritative. Reply with the summary text only.";

    private const string ConciergeInstructions =
        "You are the Hotel ZGY concierge. Answer hotel questions using only the current hotel "
        + "evidence supplied for this turn. If it does not support an answer, say you do not "
        + "know and suggest contacting hotel staff. The conversation summary and earlier turns "
        + "are context, not authority for hotel facts.";

    // Guest-facing work is retried briefly so a failed turn is reported promptly.
    private static readonly ActivityOptions TurnActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(1),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            MaximumInterval = TimeSpan.FromSeconds(5),
            BackoffCoefficient = 2,
            MaximumAttempts = 3,
        },
    };

    // A flush that exhausts its retries keeps its turns pending for the next attempt.
    private static readonly ActivityOptions TranscriptActivityOptions = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(1),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            MaximumInterval = TimeSpan.FromSeconds(30),
            BackoffCoefficient = 2,
            MaximumAttempts = 5,
        },
    };

    private readonly Dictionary<string, AcceptedTurn> acceptedTurns = new(StringComparer.Ordinal);
    private readonly List<ConciergeTranscriptTurn> runTurns = [];
    private readonly List<ConciergeTranscriptTurn> recentTurns = [];
    private readonly List<ConciergeTranscriptTurn> pendingPersistence = [];
    private readonly Guid stayId;
    private readonly DateTimeOffset eligibilityDeadlineUtc;
    private readonly TimeSpan flushInterval;
    private readonly int flushBatchSize;
    private readonly bool hasEarlierRuns;
    private string? continuitySummary;
    private long nextSequence;
    private long nextArrival;
    private long nextToProcess;
    private bool flushInProgress;

    /// <summary>Restores the stay deadline, transcript sequence, and carried conversation context.</summary>
    [WorkflowInit]
    public ConciergeStayWorkflow(ConciergeStayWorkflowInput input)
    {
        stayId = input.StayId;
        eligibilityDeadlineUtc = input.EligibilityDeadlineUtc;
        flushInterval = TimeSpan.FromSeconds(Math.Max(1, input.TranscriptFlushIntervalSeconds));
        flushBatchSize = Math.Clamp(
            input.TranscriptFlushBatchSize, 1, ConciergePersistenceOptions.MaxFlushBatchSize);
        nextSequence = input.NextSequence;
        hasEarlierRuns = input.NextSequence > 0;
        continuitySummary = input.ContinuitySummary;
        if (input.RecentTurns is not null)
        {
            recentTurns.AddRange(input.RecentTurns);
        }
    }

    /// <summary>
    /// Keeps the stay's conversation alive until its eligibility deadline, flushing transcript batches and
    /// continuing as new when history grows. All pending turns are persisted before the workflow completes.
    /// </summary>
    [WorkflowRun]
    public async Task RunAsync(ConciergeStayWorkflowInput input)
    {
        while (Workflow.UtcNow < eligibilityDeadlineUtc)
        {
            if (pendingPersistence.Count == 0 && !Workflow.ContinueAsNewSuggested)
            {
                await Workflow.WaitConditionAsync(
                    () => pendingPersistence.Count > 0 || Workflow.ContinueAsNewSuggested,
                    RemainingUntilDeadline());
                continue;
            }

            if (pendingPersistence.Count > 0)
            {
                // Persist a partial batch at most one interval after it starts waiting; flush early
                // when the batch-size threshold is reached or Continue-As-New is suggested.
                // The patch keeps replay of histories recorded before the batch-size trigger.
                var batchSizeApplies = Workflow.Patched(TranscriptBatchSizePatch);
                bool FlushDue() =>
                    Workflow.ContinueAsNewSuggested
                    || (batchSizeApplies && pendingPersistence.Count >= flushBatchSize);

                await Workflow.WaitConditionAsync(
                    FlushDue,
                    Min(flushInterval, RemainingUntilDeadline()));

                // After a failed flush, back off for an interval instead of retrying immediately.
                if (!await FlushPendingAsync() && FlushDue())
                {
                    await Workflow.DelayAsync(flushInterval);
                }
            }

            if (Workflow.ContinueAsNewSuggested && pendingPersistence.Count == 0)
            {
                // Continue between turns, only after every admitted handler and flush finishes.
                await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
                if (pendingPersistence.Count == 0)
                {
                    throw Workflow.CreateContinueAsNewException(
                        (ConciergeStayWorkflow workflow) => workflow.RunAsync(
                            new ConciergeStayWorkflowInput(
                                stayId,
                                eligibilityDeadlineUtc,
                                (int)flushInterval.TotalSeconds,
                                nextSequence,
                                continuitySummary,
                                recentTurns.ToArray(),
                                flushBatchSize)));
                }
            }
        }

        // Stay expiry: let admitted handlers finish, then never drop an unpersisted turn.
        await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
        while (!await FlushPendingAsync())
        {
            await Workflow.DelayAsync(flushInterval);
        }
    }

    /// <summary>
    /// Rejects malformed turns and turns submitted after the stay's eligibility deadline before they enter history.
    /// </summary>
    [WorkflowUpdateValidator(nameof(SubmitTurnAsync))]
    public void ValidateSubmitTurn(ConciergeTurnSubmission request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserMessage);
        if (request.TurnId.Length > ConciergeTurnLimits.MaxTurnIdLength
            || !ConciergeTurnLimits.IsUserMessageWithinLimit(request.UserMessage)
            || !ConciergeTurnLimits.IsChannel(request.Channel))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid turn.");
        }

        if (Workflow.UtcNow >= eligibilityDeadlineUtc)
        {
            throw new ApplicationFailureException(
                "The stay is no longer eligible.",
                errorType: StayExpiredErrorType,
                nonRetryable: true);
        }
    }

    /// <summary>
    /// Marks completed voice playback as interrupted without canceling the admitted answer work.
    /// The turn remains in the transcript with its complete answer text.
    /// </summary>
    [WorkflowUpdateValidator(nameof(MarkTurnInterruptedAsync))]
    public void ValidateMarkTurnInterrupted(ConciergeTurnInterruption request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TurnId);
        if (request.TurnId.Length > ConciergeTurnLimits.MaxTurnIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid turn identifier.");
        }
    }

    [WorkflowUpdate(ConciergeWorkflowNames.MarkTurnInterruptedUpdate)]
    public async Task MarkTurnInterruptedAsync(ConciergeTurnInterruption request)
    {
        // Serialize the database update against a flush so a marker cannot land before an
        // insert of the same turn and then be overwritten by that insert.
        await Workflow.WaitConditionAsync(() => !flushInProgress);
        flushInProgress = true;
        try
        {
            await Workflow.ExecuteActivityAsync(
                (ConciergeTranscriptActivities activities) =>
                    activities.MarkInterruptedAsync(
                        new MarkTranscriptTurnInterruptedRequest(stayId, request.TurnId)),
                TranscriptActivityOptions);
            MarkInterrupted(runTurns, request.TurnId);
            MarkInterrupted(pendingPersistence, request.TurnId);
        }
        finally
        {
            flushInProgress = false;
        }
    }

    /// <summary>
    /// Answers one guest turn using retrieved hotel evidence. Turns are processed in arrival order; a repeated
    /// turn ID returns the original answer, or fails with a conflict when the message differs.
    /// </summary>
    [WorkflowUpdate(ConciergeWorkflowNames.SubmitTurnUpdate)]
    public async Task<ConciergeTurnResult> SubmitTurnAsync(ConciergeTurnSubmission request)
    {
        var fingerprint = Fingerprint(request.Channel, request.UserMessage);
        if (acceptedTurns.TryGetValue(request.TurnId, out var accepted))
        {
            return MatchOrReject(accepted, fingerprint);
        }

        // Handlers start in update-delivery order; each waits for its arrival slot.
        var arrival = nextArrival++;
        await Workflow.WaitConditionAsync(() => arrival == nextToProcess);
        try
        {
            if (acceptedTurns.TryGetValue(request.TurnId, out accepted))
            {
                return MatchOrReject(accepted, fingerprint);
            }

            if (hasEarlierRuns)
            {
                // Earlier runs flushed every turn before Continue-As-New.
                var persisted = await Workflow.ExecuteActivityAsync(
                    (ConciergeTranscriptActivities activities) =>
                        activities.FindAsync(new FindTranscriptTurnRequest(stayId, request.TurnId)),
                    TranscriptActivityOptions);
                if (persisted is not null)
                {
                    accepted = new AcceptedTurn(
                        Fingerprint(persisted.Channel, persisted.GuestMessage),
                        new ConciergeTurnResult(
                            persisted.TurnId,
                            persisted.AssistantMessage,
                            persisted.Sequence));
                    acceptedTurns.Add(request.TurnId, accepted);
                    return MatchOrReject(accepted, fingerprint);
                }
            }

            var chatClient = new ChatClientBuilder(new WorkflowOnlyChatClient())
                .UseDurableExecution(options => options.TaskQueue = Workflow.Info.TaskQueue)
                .Build();
            await CompactContextAsync(chatClient);

            var retrieval = await Workflow.ExecuteActivityAsync(
                (HotelKnowledgeActivities activities) =>
                    activities.RetrieveAsync(new HotelRetrievalRequest(request.UserMessage)),
                TurnActivityOptions);

            // A reply cut off at the cap (FinishReason.Length) is kept as returned.
            var response = await chatClient.GetResponseAsync(
                BuildTurnMessages(request.UserMessage, retrieval),
                new ChatOptions { MaxOutputTokens = AnswerMaxOutputTokens });
            var answer = response.Text;
            if (string.IsNullOrWhiteSpace(answer))
            {
                throw new ApplicationFailureException(
                    "The model returned an empty response.",
                    errorType: UnavailableErrorType,
                    nonRetryable: true);
            }

            var turn = new ConciergeTranscriptTurn(
                request.TurnId,
                nextSequence++,
                request.Channel,
                request.UserMessage,
                answer,
                Workflow.UtcNow);
            var result = new ConciergeTurnResult(turn.TurnId, answer, turn.Sequence);
            acceptedTurns.Add(turn.TurnId, new AcceptedTurn(fingerprint, result));
            runTurns.Add(turn);
            recentTurns.Add(turn);
            pendingPersistence.Add(turn);
            return result;
        }
        finally
        {
            nextToProcess++;
        }
    }

    /// <summary>Complete stay transcript: persisted turns merged with pending turns by turn ID.</summary>
    [WorkflowUpdate(ConciergeWorkflowNames.HistoryUpdate)]
    public async Task<ConciergeHistoryResult> GetHistoryAsync()
    {
        var persisted = await Workflow.ExecuteActivityAsync(
            (ConciergeTranscriptActivities activities) =>
                activities.ReadAsync(new ReadTranscriptRequest(stayId)),
            TranscriptActivityOptions);
        var turnsById = persisted.ToDictionary(turn => turn.TurnId, StringComparer.Ordinal);
        foreach (var turn in runTurns)
        {
            turnsById[turn.TurnId] = turn;
        }

        return new ConciergeHistoryResult(
            turnsById.Values.OrderBy(turn => turn.Sequence).ToArray());
    }

    /// <summary>Waits for already-admitted turns and flushes their replies; stay memory remains.</summary>
    [WorkflowUpdate(ConciergeWorkflowNames.EndInteractionUpdate)]
    public async Task EndInteractionAsync()
    {
        if (Workflow.Patched("end-interaction-await-admitted-turns"))
        {
            // A fixed arrival boundary avoids waiting for this handler (or later interactions).
            // The patch preserves replay of histories where End flushed immediately.
            var admittedTurns = nextArrival;
            await Workflow.WaitConditionAsync(() => nextToProcess >= admittedTurns);
        }

        if (!await FlushPendingAsync())
        {
            throw new ApplicationFailureException(
                "The transcript could not be flushed.",
                errorType: UnavailableErrorType,
                nonRetryable: true);
        }
    }

    internal static string Fingerprint(string channel, string userMessage) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{channel}\n{userMessage}")));

    private List<ChatMessage> BuildTurnMessages(
        string userMessage,
        HotelRetrievalResult retrieval)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, ConciergeInstructions) };
        if (!string.IsNullOrWhiteSpace(continuitySummary))
        {
            messages.Add(new ChatMessage(
                ChatRole.System,
                $"Conversation summary (continuity only, not hotel authority):\n{continuitySummary}"));
        }

        foreach (var previous in recentTurns)
        {
            messages.Add(new ChatMessage(ChatRole.User, previous.GuestMessage));
            messages.Add(new ChatMessage(ChatRole.Assistant, previous.AssistantMessage));
        }

        var evidence = retrieval.HasEvidence
            ? string.Join("\n\n", retrieval.Evidence.Select(item => item.Passage))
            : "No current hotel evidence was found for this question.";
        messages.Add(new ChatMessage(
            ChatRole.System,
            $"Current Hotel ZGY evidence for this question:\n{evidence}"));
        messages.Add(new ChatMessage(ChatRole.User, userMessage));
        return messages;
    }

    /// <summary>
    /// Folds turns that must leave the recent window into the single continuity summary.
    /// A failed summary keeps the previous summary; evicted turns remain in the transcript.
    /// </summary>
    private async Task CompactContextAsync(IChatClient chatClient)
    {
        if (recentTurns.Count <= RecentTurnWindow)
        {
            return;
        }

        var evicted = recentTurns.Take(recentTurns.Count - RecentTurnWindow).ToArray();
        var source = new StringBuilder()
            .Append("Existing summary:\n")
            .Append(string.IsNullOrWhiteSpace(continuitySummary) ? "(none)" : continuitySummary)
            .Append("\n\nOlder turns:\n");
        foreach (var turn in evicted)
        {
            source.Append('[').Append(turn.TurnId).Append("] Guest: ").Append(turn.GuestMessage)
                .Append('\n').Append("Concierge: ").Append(turn.AssistantMessage).Append('\n');
        }

        try
        {
            var summary = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, SummaryInstructions),
                    new ChatMessage(ChatRole.User, source.ToString()),
                ],
                new ChatOptions { MaxOutputTokens = SummaryMaxOutputTokens });
            if (!string.IsNullOrWhiteSpace(summary.Text))
            {
                continuitySummary = summary.Text.Trim();
            }
            else
            {
                Workflow.Logger.LogWarning(
                    "Concierge operation {Operation} returned an empty summary; keeping the previous summary.",
                    nameof(CompactContextAsync));
            }
        }
        catch (ActivityFailureException exception)
        {
            Workflow.Logger.LogWarning(
                "Concierge operation {Operation} failed. Cause types: {CauseTypes}.",
                nameof(CompactContextAsync),
                ConciergeDiagnostics.ExceptionTypeChain(exception));
        }

        recentTurns.RemoveRange(0, evicted.Length);
    }

    private async Task<bool> FlushPendingAsync()
    {
        await Workflow.WaitConditionAsync(() => !flushInProgress);
        if (pendingPersistence.Count == 0)
        {
            return true;
        }

        flushInProgress = true;
        var batch = pendingPersistence.ToArray();
        try
        {
            await Workflow.ExecuteActivityAsync(
                (ConciergeTranscriptActivities activities) =>
                    activities.PersistAsync(new PersistTranscriptRequest(stayId, batch)),
                TranscriptActivityOptions);

            // Turns are only appended, and only this guarded method removes them.
            pendingPersistence.RemoveRange(0, batch.Length);
            return true;
        }
        catch (ActivityFailureException exception)
        {
            Workflow.Logger.LogWarning(
                "Concierge operation {Operation} failed. Cause types: {CauseTypes}.",
                nameof(FlushPendingAsync),
                ConciergeDiagnostics.ExceptionTypeChain(exception));
            return false;
        }
        finally
        {
            flushInProgress = false;
        }
    }

    private TimeSpan RemainingUntilDeadline()
    {
        var remaining = eligibilityDeadlineUtc - Workflow.UtcNow;
        return remaining > TimeSpan.FromMilliseconds(1) ? remaining : TimeSpan.FromMilliseconds(1);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left < right ? left : right;

    private static ConciergeTurnResult MatchOrReject(AcceptedTurn accepted, string fingerprint)
    {
        if (!StringComparer.Ordinal.Equals(accepted.Fingerprint, fingerprint))
        {
            throw new ApplicationFailureException(
                "The turn identifier conflicts with an earlier request.",
                errorType: TurnConflictErrorType,
                nonRetryable: true);
        }

        return accepted.Result;
    }

    private static void MarkInterrupted(List<ConciergeTranscriptTurn> turns, string turnId)
    {
        var index = turns.FindIndex(turn =>
            StringComparer.Ordinal.Equals(turn.TurnId, turnId)
            && StringComparer.Ordinal.Equals(turn.Channel, "voice"));
        if (index >= 0 && !turns[index].Interrupted)
        {
            turns[index] = turns[index] with { Interrupted = true };
        }
    }

    private sealed record AcceptedTurn(string Fingerprint, ConciergeTurnResult Result);
}
