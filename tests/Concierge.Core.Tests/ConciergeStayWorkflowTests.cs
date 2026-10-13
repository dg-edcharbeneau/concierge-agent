using Concierge.WorkflowServer.Workflows;
using Concierge.Contracts;
using Microsoft.Extensions.AI;
using Npgsql;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Worker;
using TemporalCommunity.Extensions.AI;

namespace Concierge.Core.Tests;

public sealed class ConciergeStayWorkflowTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task TurnsAreIdempotentSharedAcrossChannelsAndFlushedOnEnd()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var firstRequest = new ConciergeTurnSubmission("shared-1", "Where is breakfast?", "web");

        var first = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(firstRequest));
        var replay = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(firstRequest));
        Assert.Equal(first, replay);
        Assert.Single(host.ChatClient.Requests);

        var changed = firstRequest with { UserMessage = "Different question" };
        var conflict = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
            handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(changed)));
        Assert.Equal(
            ConciergeStayWorkflow.TurnConflictErrorType,
            Assert.IsType<ApplicationFailureException>(conflict.InnerException).ErrorType);

        var second = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("shared-2", "And what time?", "voice")));
        Assert.Equal(0, first.Sequence);
        Assert.Equal(1, second.Sequence);

        var voiceRequest = host.ChatClient.AnswerRequests[^1];
        Assert.Contains(firstRequest.UserMessage, voiceRequest.UserTexts);
        Assert.Contains(
            voiceRequest.Messages,
            message => message.Text.Contains("Current Hotel ZGY evidence", StringComparison.Ordinal));

        // Replies and history are available before any flush.
        var pending = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        Assert.Equal(["shared-1", "shared-2"], pending.Turns.Select(turn => turn.TurnId));
        Assert.Empty(await host.ReadPersistedTurnIdsAsync(stayId));

        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(["shared-1", "shared-2"], await host.ReadPersistedTurnIdsAsync(stayId));

        var combined = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        Assert.Equal(["shared-1", "shared-2"], combined.Turns.Select(turn => turn.TurnId));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(2, (await host.ReadPersistedTurnIdsAsync(stayId)).Length);
    }

    [Fact]
    public async Task InterruptionPreservesAnswerAndMarksPendingAndAlreadyFlushedVoiceTurns()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (stayId, handle) = await StartStayAsync(host);

        var pendingAnswer = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("voice-pending", "Where is the pool?", "voice")));
        await handle.ExecuteUpdateAsync(wf => wf.MarkTurnInterruptedAsync(
            new ConciergeTurnInterruption("voice-pending")));

        var pendingHistory = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        var pendingTurn = Assert.Single(pendingHistory.Turns);
        Assert.Equal(pendingAnswer.AssistantMessage, pendingTurn.AssistantMessage);
        Assert.True(pendingTurn.Interrupted);
        Assert.Null(await ReadPersistedInterruptionAsync(host, stayId, "voice-pending"));

        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.True(await ReadPersistedInterruptionAsync(host, stayId, "voice-pending"));
        Assert.Equal(
            pendingAnswer.AssistantMessage,
            await ReadPersistedAnswerAsync(host, stayId, "voice-pending"));

        var flushedAnswer = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("voice-flushed", "Where is breakfast?", "voice")));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.False(await ReadPersistedInterruptionAsync(host, stayId, "voice-flushed"));

        // A late/retried interruption report updates the existing row instead of relying on a flush.
        await handle.ExecuteUpdateAsync(wf => wf.MarkTurnInterruptedAsync(
            new ConciergeTurnInterruption("voice-flushed")));
        await handle.ExecuteUpdateAsync(wf => wf.MarkTurnInterruptedAsync(
            new ConciergeTurnInterruption("voice-flushed")));
        var history = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        var flushedTurn = history.Turns.Single(turn => turn.TurnId == "voice-flushed");
        Assert.Equal(flushedAnswer.AssistantMessage, flushedTurn.AssistantMessage);
        Assert.True(flushedTurn.Interrupted);
        Assert.True(await ReadPersistedInterruptionAsync(host, stayId, "voice-flushed"));
        Assert.Equal(
            flushedAnswer.AssistantMessage,
            await ReadPersistedAnswerAsync(host, stayId, "voice-flushed"));

        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("web-turn", "Is parking available?", "web")));
        await handle.ExecuteUpdateAsync(wf => wf.MarkTurnInterruptedAsync(
            new ConciergeTurnInterruption("web-turn")));
        var webTurn = (await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync()))
            .Turns.Single(turn => turn.TurnId == "web-turn");
        Assert.False(webTurn.Interrupted);
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.False(await ReadPersistedInterruptionAsync(host, stayId, "web-turn"));
    }

    [Fact]
    public async Task InterruptionUpdateDoesNotCancelAnAdmittedModelTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (stayId, handle) = await StartStayAsync(host);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatClient.Gate = gate;
        var submitted = await handle.StartUpdateAsync(
            wf => wf.SubmitTurnAsync(new ConciergeTurnSubmission("admitted-voice", "Still working?", "voice")),
            new(WorkflowUpdateStage.Accepted));
        await host.ChatClient.WaitForCallAsync().WaitAsync(TimeSpan.FromSeconds(15));

        // The report cannot cancel or truncate work already admitted to the workflow.
        await handle.ExecuteUpdateAsync(wf => wf.MarkTurnInterruptedAsync(
            new ConciergeTurnInterruption("admitted-voice")));
        Assert.False(submitted.GetResultAsync().IsCompleted);
        gate.TrySetResult();

        var result = await submitted.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("Answer: Still working?", result.AssistantMessage);
        var history = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        var turn = Assert.Single(history.Turns);
        Assert.Equal(result.AssistantMessage, turn.AssistantMessage);
        Assert.False(turn.Interrupted);
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.False(await ReadPersistedInterruptionAsync(host, stayId, "admitted-voice"));
    }

    [Fact]
    public async Task AddingInterruptionColumnPreservesExistingTranscriptRows()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var result = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("legacy-turn", "Is breakfast served?", "voice")));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());

        // Recreate a pre-Step-8 transcript table shape with a persisted row.
        await host.ExecuteAsync("ALTER TABLE transcript_turns DROP COLUMN interrupted;");
        await new Concierge.WorkflowServer.Data.DatabaseMigrator(
            host.DataSource,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                Concierge.WorkflowServer.Data.DatabaseMigrator>.Instance).MigrateAsync();

        Assert.False(await ReadPersistedInterruptionAsync(host, stayId, "legacy-turn"));
        Assert.Equal(result.AssistantMessage, await ReadPersistedAnswerAsync(host, stayId, "legacy-turn"));
    }

    [Fact]
    public async Task CompletedTurnsFlushOnConfiguredInterval()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 1);
        var (stayId, handle) = await StartStayAsync(host);
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("timed-1", "Where is the pool?", "web")));

        await WaitUntilAsync(
            async () => (await host.ReadPersistedTurnIdsAsync(stayId)).Length > 0,
            TimeSpan.FromSeconds(15));
        Assert.Equal(["timed-1"], await host.ReadPersistedTurnIdsAsync(stayId));
    }

    [Fact]
    public async Task CompletedTurnsFlushWhenBatchSizeIsReachedBeforeInterval()
    {
        await using var host = await ConciergeTestHost.StartAsync(
            postgreSql, flushIntervalSeconds: 600, flushBatchSize: 2);
        var (stayId, handle) = await StartStayAsync(host);

        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("batch-1", "Where is the pool?", "web")));
        // Below the threshold the batch waits for the 600-second interval.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Empty(await host.ReadPersistedTurnIdsAsync(stayId));

        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("batch-2", "Where is breakfast?", "voice")));
        await WaitUntilAsync(
            async () => (await host.ReadPersistedTurnIdsAsync(stayId)).Length == 2,
            TimeSpan.FromSeconds(15));
        Assert.Equal(["batch-1", "batch-2"], await host.ReadPersistedTurnIdsAsync(stayId));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(ConciergePersistenceOptions.MaxFlushBatchSize, true)]
    [InlineData(ConciergePersistenceOptions.MaxFlushBatchSize + 1, false)]
    public void FlushBatchSizeIsValidatedWithinBounds(int batchSize, bool valid)
    {
        var result = new Concierge.WorkflowServer.Setup.ConciergePersistenceOptionsValidator().Validate(
            null, new ConciergePersistenceOptions { FlushIntervalSeconds = 30, FlushBatchSize = batchSize });

        Assert.Equal(valid, result.Succeeded);
        if (!valid)
        {
            Assert.Contains("FlushBatchSize must be between 1 and 100", result.FailureMessage);
        }
    }

    /// <summary>End waits for already-admitted turns, persists their replies, and remains safe to repeat.</summary>
    [Fact]
    public async Task EndWaitsForAdmittedTurnsBeforeFlushing()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql, flushIntervalSeconds: 600);
        var (stayId, handle) = await StartStayAsync(host);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatClient.Gate = gate;
        var first = await handle.StartUpdateAsync(
            wf => wf.SubmitTurnAsync(new ConciergeTurnSubmission("end-1", "First?", "web")),
            new(WorkflowUpdateStage.Accepted));
        await host.ChatClient.WaitForCallAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var second = await handle.StartUpdateAsync(
            wf => wf.SubmitTurnAsync(new ConciergeTurnSubmission("end-2", "Second?", "voice")),
            new(WorkflowUpdateStage.Accepted));
        var end = await handle.StartUpdateAsync(
            wf => wf.EndInteractionAsync(), new(WorkflowUpdateStage.Accepted));
        var endResult = end.GetResultAsync();
        var repeatedEnd = await handle.StartUpdateAsync(
            wf => wf.EndInteractionAsync(), new(WorkflowUpdateStage.Accepted));
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() =>
                endResult.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            gate.TrySetResult();
        }

        await first.GetResultAsync();
        await second.GetResultAsync();
        await endResult.WaitAsync(TimeSpan.FromSeconds(15));
        await repeatedEnd.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(["end-1", "end-2"], await host.ReadPersistedTurnIdsAsync(stayId));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(["end-1", "end-2"], await host.ReadPersistedTurnIdsAsync(stayId));

        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        }.AddWorkflow<ConciergeStayWorkflow>());
        await replayer.ReplayWorkflowAsync(await handle.FetchHistoryAsync());
    }

    [Fact]
    public async Task SimultaneousTurnsRunInArrivalOrderWithSharedContext()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (_, handle) = await StartStayAsync(host);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatClient.Gate = gate;

        var firstUpdate = await handle.StartUpdateAsync(
            wf => wf.SubmitTurnAsync(new ConciergeTurnSubmission("arrive-1", "First?", "web")),
            new(WorkflowUpdateStage.Accepted));
        await host.ChatClient.WaitForCallAsync();
        var secondUpdate = await handle.StartUpdateAsync(
            wf => wf.SubmitTurnAsync(new ConciergeTurnSubmission("arrive-2", "Second?", "voice")),
            new(WorkflowUpdateStage.Accepted));

        // The second turn waits for the first; it has not reached the model.
        Assert.Single(host.ChatClient.Requests);
        gate.SetResult();

        var first = await firstUpdate.GetResultAsync();
        var second = await secondUpdate.GetResultAsync();
        Assert.Equal(0, first.Sequence);
        Assert.Equal(1, second.Sequence);
        Assert.Equal(["First?", "Second?"], host.ChatClient.AnswerRequests.Select(r => r.LastUserText));
        Assert.Contains("First?", host.ChatClient.AnswerRequests[1].UserTexts);
    }

    [Fact]
    public async Task OlderTurnsFoldIntoOneProviderBoundedSummary()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var turnCount = ConciergeStayWorkflow.RecentTurnWindow + 2;
        for (var index = 0; index < turnCount; index++)
        {
            await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                new ConciergeTurnSubmission($"t-{index}", $"Question {index}", "web")));
        }

        var summaries = host.ChatClient.Requests.Where(request => request.IsSummary).ToArray();
        Assert.Single(summaries);
        Assert.Equal(1024, summaries[0].MaxOutputTokens);
        Assert.Equal(ConciergeStayWorkflow.SummaryMaxOutputTokens, summaries[0].MaxOutputTokens);
        Assert.Contains("[t-0]", summaries[0].LastUserText, StringComparison.Ordinal);

        var last = host.ChatClient.AnswerRequests[^1];
        Assert.Contains(
            last.Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("Summary 1", StringComparison.Ordinal));
        Assert.DoesNotContain("Question 0", last.UserTexts);
        Assert.Equal(
            ConciergeStayWorkflow.RecentTurnWindow + 1,
            last.Messages.Count(message => message.Role == ChatRole.User));

        // Compaction never removes turns from the complete transcript.
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(turnCount, (await host.ReadPersistedTurnIdsAsync(stayId)).Length);
    }

    [Fact]
    public async Task ContinueAsNewKeepsDeadlineTranscriptAndTurnIdempotency()
    {
        await using var host = await ConciergeTestHost.StartAsync(
            postgreSql,
            devServerArgs:
            [
                "--dynamic-config-value",
                "limit.historyCount.suggestContinueAsNew=30",
            ],
            flushBatchSize: 7);
        var (stayId, handle) = await StartStayAsync(host);
        var deadline = (await ReadRunInputAsync(host, handle)).EligibilityDeadlineUtc;
        var firstRunId = (await handle.DescribeAsync()).RunId;

        var submitted = new List<ConciergeTurnResult>();
        for (var index = 0; index < 12 && (await handle.DescribeAsync()).RunId == firstRunId; index++)
        {
            submitted.Add(await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                new ConciergeTurnSubmission($"can-{index}", $"Question {index}", "web"))));
            await WaitUntilAsync(
                async () => (await handle.DescribeAsync()).RunId != firstRunId,
                TimeSpan.FromSeconds(2),
                throwOnTimeout: false);
        }

        Assert.NotEqual(firstRunId, (await handle.DescribeAsync()).RunId);
        var continued = await ReadRunInputAsync(host, handle);
        Assert.Equal(deadline, continued.EligibilityDeadlineUtc);
        Assert.Equal(stayId, continued.StayId);
        Assert.Equal(submitted.Count, continued.NextSequence);
        Assert.Equal(7, continued.TranscriptFlushBatchSize);

        // Every earlier-run turn was flushed before Continue-As-New.
        var expectedIds = submitted.Select(turn => turn.TurnId).ToArray();
        Assert.Equal(expectedIds, await host.ReadPersistedTurnIdsAsync(stayId));

        // A retried turn from the earlier run returns its original result without a model call.
        var modelCalls = host.ChatClient.Requests.Count;
        var replay = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("can-0", "Question 0", "web")));
        Assert.Equal(submitted[0], replay);
        var conflict = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
            handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                new ConciergeTurnSubmission("can-0", "Changed", "web"))));
        Assert.Equal(
            ConciergeStayWorkflow.TurnConflictErrorType,
            Assert.IsType<ApplicationFailureException>(conflict.InnerException).ErrorType);
        Assert.Equal(modelCalls, host.ChatClient.Requests.Count);

        var next = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("after-can", "Anything else?", "voice")));
        Assert.Equal(submitted.Count, next.Sequence);
        // Recent context crossed Continue-As-New with the carried input.
        Assert.Contains(
            $"Question {submitted.Count - 1}",
            host.ChatClient.AnswerRequests[^1].UserTexts);

        string[] allIds = [.. expectedIds, "after-can"];
        var history = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        Assert.Equal(allIds, history.Turns.Select(turn => turn.TurnId));
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(allIds, await host.ReadPersistedTurnIdsAsync(stayId));
    }

    [Fact]
    public async Task StayExpiryFlushesPendingTurnsAndEndsWithoutGrace()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var stayId = await SeedStayAsync(host);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(6);
        var handle = await host.StartStayAsync(stayId, deadline);
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("expiring", "Late checkout?", "web")));

        await handle.GetResultAsync();
        Assert.True(DateTimeOffset.UtcNow >= deadline);
        Assert.Equal(["expiring"], await host.ReadPersistedTurnIdsAsync(stayId));
    }

    [Fact]
    public async Task FailedTurnIsNotRecordedAndCanBeRetried()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.Fail = true;
        var request = new ConciergeTurnSubmission("retry-me", "Is the spa open?", "web");

        await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
            handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(request)));
        Assert.Empty((await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns);

        host.ChatClient.Fail = false;
        var result = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(request));
        Assert.Equal(0, result.Sequence);
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(["retry-me"], await host.ReadPersistedTurnIdsAsync(stayId));
    }

    [Fact]
    public async Task AnswerRequestsCarryTheOutputCapAndLengthCutoffKeepsReturnedText()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFinishReason = ChatFinishReason.Length;

        var result = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("cut-off", "Describe every amenity.", "web")));

        var answerRequest = Assert.Single(host.ChatClient.AnswerRequests);
        Assert.Equal(1024, answerRequest.MaxOutputTokens);
        Assert.Equal(ConciergeStayWorkflow.AnswerMaxOutputTokens, answerRequest.MaxOutputTokens);
        Assert.Equal("Answer: Describe every amenity.", result.AssistantMessage);
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        Assert.Equal(["cut-off"], await host.ReadPersistedTurnIdsAsync(stayId));
    }

    [Fact]
    public async Task MessageOverTheUtf8ByteLimitIsRejectedBeforeTheModel()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (_, handle) = await StartStayAsync(host);

        // 700 three-byte characters: 700 chars passes a character count, 2,100 bytes does not.
        var multiByte = new string('\u20AC', 700);
        await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() =>
            handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
                new ConciergeTurnSubmission("too-big", multiByte, "web"))));
        Assert.Empty(host.ChatClient.Requests);

        var atLimit = new string('a', ConciergeTurnLimits.MaxUserMessageUtf8Bytes);
        await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission("at-limit", atLimit, "web")));
        Assert.Single(host.ChatClient.AnswerRequests);
    }

    private static async Task<(Guid StayId, WorkflowHandle<ConciergeStayWorkflow> Handle)> StartStayAsync(
        ConciergeTestHost host)
    {
        var stayId = await SeedStayAsync(host);
        var deadline = ConciergeTestHost.HotelMidnightUtc(
            ConciergeTestHost.HotelToday.AddDays(2));
        return (stayId, await host.StartStayAsync(stayId, deadline));
    }

    private static Task<Guid> SeedStayAsync(ConciergeTestHost host) =>
        host.SeedStayAsync(
            "Guest",
            "101",
            ConciergeTestHost.HotelToday,
            ConciergeTestHost.HotelToday.AddDays(1));

    private static async Task<bool?> ReadPersistedInterruptionAsync(
        ConciergeTestHost host,
        Guid stayId,
        string turnId)
    {
        await using var command = host.DataSource.CreateCommand(
            "SELECT interrupted FROM transcript_turns WHERE stay_id = $1 AND turn_id = $2;");
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue(turnId);
        return await command.ExecuteScalarAsync() as bool?;
    }

    private static async Task<string?> ReadPersistedAnswerAsync(
        ConciergeTestHost host,
        Guid stayId,
        string turnId)
    {
        await using var command = host.DataSource.CreateCommand(
            "SELECT assistant_message FROM transcript_turns WHERE stay_id = $1 AND turn_id = $2;");
        command.Parameters.AddWithValue(stayId);
        command.Parameters.AddWithValue(turnId);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<ConciergeStayWorkflowInput> ReadRunInputAsync(
        ConciergeTestHost host,
        WorkflowHandle<ConciergeStayWorkflow> handle)
    {
        var history = await handle.FetchHistoryAsync();
        var started = history.Events.First().WorkflowExecutionStartedEventAttributes;
        return await host.Environment.Client.Options.DataConverter
            .ToSingleValueAsync<ConciergeStayWorkflowInput>(started.Input.Payloads_);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        bool throwOnTimeout = true)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                if (throwOnTimeout)
                {
                    throw new TimeoutException("Condition was not met in time.");
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }
    }
}
