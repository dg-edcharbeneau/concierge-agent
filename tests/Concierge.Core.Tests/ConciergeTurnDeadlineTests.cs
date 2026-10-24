using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;
using Microsoft.Extensions.AI;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Worker.Interceptors;
using Temporalio.Workflows;

namespace Concierge.Core.Tests;

public sealed class ConciergeTurnDeadlineTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task QueuedToolsExpireAtWholeTurnDeadlineAndNeverExecuteAfterSlotRelease()
    {
        var cases = new[]
        {
            (Tool: ConciergeToolDefinitions.GetWeather, PairedWeather: false, Confirming: false, ExpectedTools: 1),
            (Tool: ConciergeToolDefinitions.GetWeather, PairedWeather: true, Confirming: false, ExpectedTools: 2),
            (Tool: ConciergeToolDefinitions.ConfirmRide, PairedWeather: false, Confirming: true, ExpectedTools: 1),
            (Tool: ConciergeToolDefinitions.ConfirmRideCancellation, PairedWeather: false, Confirming: true, ExpectedTools: 1),
        };
        var slots = new DeadlineSlotHarness(cases.Sum(item => item.ExpectedTools));
        await using var host = await ConciergeTestHost.StartAsync(
            postgreSql,
            configureWorker: options =>
            {
                options.MaxConcurrentActivities = 1;
                options.AddWorkflow<DeadlineSlotBlockerWorkflow>().AddAllActivities(slots);
                options.Interceptors =
                    [.. options.Interceptors ?? [], new StarveToolSlotInterceptor()];
            });
        slots.Client = host.Environment.Client;

        var scenarios = new List<QueuedToolScenario>(cases.Length);
        foreach (var (testCase, index) in cases.Select((item, index) => (item, index)))
        {
            var (stayId, handle) = await StartStayAsync(host, $"60{index + 1}");
            if (testCase.Confirming)
            {
                var cancellation = testCase.Tool == ConciergeToolDefinitions.ConfirmRideCancellation;
                // Stage the workflow-owned offer before holding the shared activity slot.
                slots.Bypass = true;
                host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
                    cancellation
                        ? ConciergeToolDefinitions.ProposeRideCancellation
                        : ConciergeToolDefinitions.ProposeRide,
                    cancellation
                        ? new() { ["rideId"] = Guid.NewGuid().ToString("D") }
                        : new() { ["dropoff"] = "South Beach", ["immediate"] = true }));
                var offer = await SubmitAsync(handle, stayId, "offer", "Please arrange my ride.");
                Assert.Contains(
                    cancellation ? "Would you like me to cancel" : "No ride has been booked",
                    offer.AssistantMessage,
                    StringComparison.Ordinal);
            }

            scenarios.Add(new QueuedToolScenario(
                testCase.Tool,
                testCase.PairedWeather,
                testCase.Confirming,
                testCase.ExpectedTools,
                stayId,
                handle));
        }

        slots.Bypass = false;
        var callsBefore = host.ActionTools.Calls.Count;
        var modelsBefore = host.ChatClient.Requests.Count;
        var pending = new List<Task<ConciergeTurnResult>>(scenarios.Count);
        var interceptedToolCount = 0;
        try
        {
            // The local interceptor activities hold each workflow before it schedules its tool
            // activity. This lets every model response run on the single shared worker before a
            // blocker occupies its only remote activity slot.
            foreach (var scenario in scenarios)
            {
                host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
                    scenario.Tool,
                    scenario.Confirming ? new() : new() { ["period"] = "today" }));
                pending.Add(SubmitAsync(
                    scenario.Handle,
                    scenario.StayId,
                    "queued",
                    scenario.Confirming
                        ? "Yes, go ahead."
                        : scenario.PairedWeather
                            ? "What is the weather today and tomorrow?"
                            : "What is the weather today?"));
                interceptedToolCount += scenario.ExpectedTools;
                await slots.WaitForInterceptedToolsAsync(interceptedToolCount)
                    .WaitAsync(TimeSpan.FromSeconds(20));
            }

            Assert.Equal(interceptedToolCount, slots.InterceptedToolCount);
            await slots.StartBlockerAsync();
            Assert.False(slots.Release.Task.IsCompleted);
            slots.AllowToolScheduling.TrySetResult();

            foreach (var scenario in scenarios)
            {
                var expectedScheduled = scenario.ExpectedTools + (scenario.Confirming ? 1 : 0);
                await WaitUntilAsync(async () =>
                    ScheduledTools(await scenario.Handle.FetchHistoryAsync()).Length == expectedScheduled);

                var history = await scenario.Handle.FetchHistoryAsync();
                var queued = ScheduledTools(history).TakeLast(scenario.ExpectedTools).ToArray();
                Assert.Equal(scenario.ExpectedTools, queued.Length);
                Assert.All(queued, scheduled => Assert.DoesNotContain(
                    history.Events,
                    e => e.ActivityTaskStartedEventAttributes?.ScheduledEventId == scheduled.EventId));
                // The configured deadline is the contract. Under load, the server may record its
                // timeout event late relative to that deadline.
                Assert.All(queued, scheduled => Assert.InRange(
                    scheduled.ActivityTaskScheduledEventAttributes.ScheduleToCloseTimeout.ToTimeSpan().TotalSeconds,
                    170, 180));
            }

            var results = await Task.WhenAll(
                pending.Select(turn => turn.WaitAsync(TimeSpan.FromMinutes(6))));
            Assert.Equal(modelsBefore + scenarios.Count, host.ChatClient.Requests.Count);
            Assert.Equal(callsBefore, host.ActionTools.Calls.Count);

            foreach (var (scenario, index) in scenarios.Select((item, index) => (item, index)))
            {
                var result = results[index];
                Assert.Equal(scenario.Confirming ? 1 : 0, result.Sequence);
                if (scenario.Confirming)
                {
                    Assert.Equal(scenario.Tool == ConciergeToolDefinitions.ConfirmRide
                            ? "The ride service did not confirm the outcome. Check ride history before trying again."
                            : "The ride service did not confirm cancellation. Check ride status before trying again.",
                        result.AssistantMessage);
                }
                else
                {
                    Assert.Contains("before the request deadline", result.AssistantMessage, StringComparison.Ordinal);
                }

                var history = await scenario.Handle.FetchHistoryAsync();
                var queued = ScheduledTools(history).TakeLast(scenario.ExpectedTools).ToArray();
                Assert.All(queued, scheduled =>
                {
                    var timedOut = Assert.Single(history.Events, e =>
                        e.ActivityTaskTimedOutEventAttributes?.ScheduledEventId == scheduled.EventId);
                    // ScheduleToStart may win when both timers expire at the same instant.
                    Assert.Contains(
                        timedOut.ActivityTaskTimedOutEventAttributes.Failure.TimeoutFailureInfo.TimeoutType,
                        new[] { TimeoutType.ScheduleToClose, TimeoutType.ScheduleToStart });
                    Assert.DoesNotContain(history.Events,
                        e => e.ActivityTaskStartedEventAttributes?.ScheduledEventId == scheduled.EventId);
                });
            }

            slots.Release.TrySetResult();
            await slots.Blocker!.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
            // A fresh activity after release proves the worker resumed polling, not merely that
            // the expired tool remained hidden behind our blocker when we checked.
            await host.Environment.Client.ExecuteWorkflowAsync(
                (DeadlineSlotBlockerWorkflow wf) => wf.RunAsync(),
                new($"sentinel-{Guid.NewGuid():N}", TemporalWorkerConnection.TaskQueue))
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(callsBefore, host.ActionTools.Calls.Count);

            foreach (var scenario in scenarios.Where(item => item.Confirming))
            {
                host.ChatClient.ScriptedResponses.Enqueue(ToolCall(scenario.Tool, new()));
                var retry = await SubmitAsync(
                        scenario.Handle, scenario.StayId, "unsafe-retry", "Yes, try that again.")
                    .WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Contains(scenario.Tool == ConciergeToolDefinitions.ConfirmRide
                        ? "no ride offer waiting for confirmation"
                        : "no ride cancellation offer waiting for confirmation",
                    retry.AssistantMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(callsBefore, host.ActionTools.Calls.Count);
            }

            foreach (var scenario in scenarios)
            {
                await AssertFollowingTurnAndEndAsync(
                    scenario.Handle, scenario.StayId, scenario.Confirming ? 3 : 1);
                Assert.Equal(callsBefore, host.ActionTools.Calls.Count);
                var drained = await scenario.Handle.FetchHistoryAsync();
                var queued = ScheduledTools(drained).TakeLast(scenario.ExpectedTools).ToArray();
                Assert.All(queued, scheduled => Assert.DoesNotContain(drained.Events,
                    e => e.ActivityTaskStartedEventAttributes?.ScheduledEventId == scheduled.EventId));
            }
        }
        finally
        {
            slots.AllowToolScheduling.TrySetResult();
            slots.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task ModelTimerWinsAndLateToolResponseCannotAffectFollowingTurnOrEnd()
    {
        await using var host = await ConciergeTestHost.StartAsync(
            postgreSql,
            timeSkipping: true,
            configureWorker: options => options.Interceptors =
                [.. options.Interceptors ?? [], new LateModelResponseInterceptor()]);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            ConciergeToolDefinitions.GetWeather, new() { ["period"] = "today" }));

        var pending = SubmitAsync(handle, stayId, "late-model", "What is the weather?");
        await WaitUntilAsync(async () =>
        {
            var events = (await handle.FetchHistoryAsync()).Events;
            var model = events.LastOrDefault(e => e.ActivityTaskScheduledEventAttributes?.ActivityType.Name ==
                LateModelResponseInterceptor.ModelActivity);
            return model is not null && events.Any(e =>
                e.ActivityTaskCompletedEventAttributes?.ScheduledEventId == model.EventId);
        });
        await host.Environment.DelayAsync(TimeSpan.FromSeconds(181));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Contains("before the request deadline", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Empty(host.ActionTools.Calls);
        Assert.Single(host.ChatClient.Requests);
        await WaitUntilAsync(async () =>
        {
            var events = (await handle.FetchHistoryAsync()).Events;
            return events.Any(e => e.TimerFiredEventAttributes is { } fired
                && events.Any(start => start.EventId == fired.StartedEventId
                    && start.TimerStartedEventAttributes?.StartToFireTimeout.ToTimeSpan() ==
                        TimeSpan.FromSeconds(181)));
        });
        var history = await handle.FetchHistoryAsync();
        Assert.Contains(history.Events, e => e.TimerFiredEventAttributes is { } fired
            && history.Events.Any(start => start.EventId == fired.StartedEventId
                && start.TimerStartedEventAttributes?.StartToFireTimeout.ToTimeSpan()
                    is { TotalSeconds: > 170 and <= 180 }));
        Assert.DoesNotContain(history.Events, e =>
            e.ActivityTaskScheduledEventAttributes?.ActivityType.Name == DeadlineSlotHarness.ToolActivity);

        await AssertFollowingTurnAndEndAsync(handle, stayId, 1);
        Assert.Empty(host.ActionTools.Calls);
    }

    private static async Task<(Guid, WorkflowHandle<ConciergeStayWorkflow>)> StartStayAsync(
        ConciergeTestHost host,
        string roomNumber = "606")
    {
        var stayId = await host.SeedStayAsync(
            "Deadline", roomNumber, ConciergeTestHost.HotelToday, ConciergeTestHost.HotelToday.AddDays(1));
        var now = await host.Environment.GetCurrentTimeAsync();
        return (stayId, await host.StartStayAsync(stayId, now.AddDays(2)));
    }

    private static Task<ConciergeTurnResult> SubmitAsync(
        WorkflowHandle<ConciergeStayWorkflow> handle, Guid stayId, string id, string text) =>
        handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(
            new ConciergeTurnSubmission(id, text, "web", ConciergeTestHost.VerifiedGuest(stayId))));

    private static async Task AssertFollowingTurnAndEndAsync(
        WorkflowHandle<ConciergeStayWorkflow> handle, Guid stayId, long sequence)
    {
        var next = await SubmitAsync(handle, stayId, "next", "When is breakfast?")
            .WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(sequence, next.Sequence);
        Assert.Equal("Answer: When is breakfast?", next.AssistantMessage);
        await handle.ExecuteUpdateAsync(wf => wf.EndInteractionAsync())
            .WaitAsync(TimeSpan.FromSeconds(15));
        var history = await handle.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        Assert.Equal(sequence + 1, history.Turns.Count);
    }

    private static ChatResponse ToolCall(string name, Dictionary<string, object?> arguments) =>
        new([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{name}", name, arguments)])])
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };

    private static Temporalio.Api.History.V1.HistoryEvent[] ScheduledTools(WorkflowHistory history) =>
        history.Events.Where(e =>
            e.ActivityTaskScheduledEventAttributes?.ActivityType.Name == DeadlineSlotHarness.ToolActivity).ToArray();

    private sealed record QueuedToolScenario(
        string Tool,
        bool PairedWeather,
        bool Confirming,
        int ExpectedTools,
        Guid StayId,
        WorkflowHandle<ConciergeStayWorkflow> Handle);

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await predicate())
        {
            await Task.Delay(25, timeout.Token);
        }
    }
}

internal sealed class LateModelResponseInterceptor : IWorkerInterceptor
{
    internal const string ModelActivity = "TemporalCommunity.Extensions.AI.GetResponse";

    public WorkflowInboundInterceptor InterceptWorkflow(WorkflowInboundInterceptor nextInterceptor) =>
        new Inbound(nextInterceptor);

    private sealed class Inbound(WorkflowInboundInterceptor next) : WorkflowInboundInterceptor(next)
    {
        public override void Init(WorkflowOutboundInterceptor outbound) =>
            base.Init(new Outbound(outbound));
    }

    private sealed class Outbound(WorkflowOutboundInterceptor next) : WorkflowOutboundInterceptor(next)
    {
        private bool delayed;

        public override async Task<TResult> ScheduleActivityAsync<TResult>(ScheduleActivityInput input)
        {
            if (input.Activity != ModelActivity || delayed)
            {
                return await base.ScheduleActivityAsync<TResult>(input);
            }

            delayed = true;
            var result = await base.ScheduleActivityAsync<TResult>(input);
            // The real activity completed; only delivery to the workflow's model awaiter loses.
            await Workflow.DelayAsync(TimeSpan.FromSeconds(181));
            return result;
        }
    }
}

internal sealed class DeadlineSlotHarness
{
    internal const string ToolActivity = "TemporalCommunity.Extensions.AI.InvokeFunction";
    private readonly TaskCompletionSource[] interceptedTools;
    private int interceptedToolCount;

    internal DeadlineSlotHarness(int expectedToolCount)
    {
        interceptedTools = Enumerable.Range(0, expectedToolCount)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
    }

    public ITemporalClient Client { get; set; } = null!;
    public bool Bypass { get; set; }
    public TaskCompletionSource AllowToolScheduling { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WorkflowHandle<DeadlineSlotBlockerWorkflow>? Blocker { get; private set; }
    public int InterceptedToolCount => Volatile.Read(ref interceptedToolCount);

    public Task WaitForInterceptedToolsAsync(int count) => interceptedTools[count - 1].Task;

    public async Task StartBlockerAsync()
    {
        Blocker = await Client.StartWorkflowAsync(
            (DeadlineSlotBlockerWorkflow wf) => wf.RunAsync(),
            new($"slot-blocker-{Guid.NewGuid():N}", TemporalWorkerConnection.TaskQueue));
        await Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Activity]
    public async Task OccupySlotAsync()
    {
        if (Bypass || AllowToolScheduling.Task.IsCompleted)
        {
            return;
        }

        var count = Interlocked.Increment(ref interceptedToolCount);
        if (count > interceptedTools.Length)
        {
            throw new InvalidOperationException("More tool activities reached the slot barrier than expected.");
        }

        interceptedTools[count - 1].TrySetResult();
        await AllowToolScheduling.Task.WaitAsync(ActivityExecutionContext.Current.CancellationToken);
    }

    [Activity]
    public async Task HoldSlotAsync()
    {
        Entered.TrySetResult();
        await Release.Task.WaitAsync(ActivityExecutionContext.Current.CancellationToken);
    }
}

[Workflow]
internal sealed class DeadlineSlotBlockerWorkflow
{
    [WorkflowRun]
    public Task RunAsync() => Workflow.ExecuteActivityAsync(
        (DeadlineSlotHarness activity) => activity.HoldSlotAsync(),
        // The test's wait watchdog releases the blocker; its Temporal timeout must not expire first.
        new() { StartToCloseTimeout = TimeSpan.FromDays(1), RetryPolicy = new() { MaximumAttempts = 1 } });
}

internal sealed class StarveToolSlotInterceptor : IWorkerInterceptor
{
    public WorkflowInboundInterceptor InterceptWorkflow(WorkflowInboundInterceptor nextInterceptor) =>
        new Inbound(nextInterceptor);

    private sealed class Inbound(WorkflowInboundInterceptor next) : WorkflowInboundInterceptor(next)
    {
        public override void Init(WorkflowOutboundInterceptor outbound) =>
            base.Init(new Outbound(outbound));
    }

    private sealed class Outbound(WorkflowOutboundInterceptor next) : WorkflowOutboundInterceptor(next)
    {
        public override async Task<TResult> ScheduleActivityAsync<TResult>(ScheduleActivityInput input)
        {
            if (input.Activity == DeadlineSlotHarness.ToolActivity)
            {
                // Local activities have separate slots. Wait for a real remote activity to occupy
                // the only remote slot before scheduling the unchanged production tool activity.
                await Workflow.ExecuteLocalActivityAsync(
                    (DeadlineSlotHarness activity) => activity.OccupySlotAsync(),
                    new() { StartToCloseTimeout = TimeSpan.FromSeconds(20), RetryPolicy = new() { MaximumAttempts = 1 } });
            }
            return await base.ScheduleActivityAsync<TResult>(input);
        }
    }
}
