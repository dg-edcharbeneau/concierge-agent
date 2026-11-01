using System.Text.Json;
using System.Text;
using Concierge.Contracts;
using Concierge.Web.Voice;
using Concierge.WorkflowServer;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using TemporalCommunity.Extensions.AI;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Exceptions;
using Temporalio.Worker;

namespace Concierge.Core.Tests;

public sealed class VoiceRevisionWorkflowTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Theory]
    [InlineData(1, 400)]
    [InlineData(2, 200)]
    public async Task ThinkCallbackRejectsRevisedFamilyButPreservesAnotherOrdinal(int revisionOrdinal, int expectedStatus)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var connection = host.App.Services.GetRequiredService<VoiceSessionRegistry>().Open(
            new(Guid.NewGuid(), stay,
                ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)),
                ConciergeTestHost.VerifiedGuest(stay)), null);
        await using var scope = host.App.Services.CreateAsyncScope();
        host.ChatClient.ResponseFactory = messages => LastUser(messages) == "Goodbye"
            ? Tool(ConciergeEndChat.FunctionName)
            : Answer(LastUser(messages));
        var original = ThinkContext(scope.ServiceProvider, connection, "Goodbye");
        await ThinkEndpoint.HandleAsync(original);
        Assert.Equal(200, original.Response.StatusCode);
        Assert.Contains("call-end_chat", Body(original), StringComparison.Ordinal);
        Assert.True(DeepgramFunctionProtocol.TryCreateEndChatResponse(
            """{"type":"FunctionCallRequest","functions":[{"id":"call-end_chat","name":"end_chat","client_side":true,"arguments":"{}"}]}"""u8,
            connection, out _));
        var retry = ThinkContext(scope.ServiceProvider, connection, "Goodbye");
        await ThinkEndpoint.HandleAsync(retry);
        Assert.Equal(200, retry.Response.StatusCode);

        var revision = ThinkContext(scope.ServiceProvider, connection, "Where is breakfast?", revisionOrdinal);
        await ThinkEndpoint.HandleAsync(revision);
        Assert.Equal(200, revision.Response.StatusCode);
        var callback = ThinkContext(scope.ServiceProvider, connection, "Goodbye", resultCallId: "call-end_chat");
        await ThinkEndpoint.HandleAsync(callback);

        Assert.Equal(expectedStatus, callback.Response.StatusCode);
        if (expectedStatus == 400)
        {
            using var error = JsonDocument.Parse(Body(callback));
            Assert.Equal("invalid_request_error", error.RootElement.GetProperty("error").GetProperty("type").GetString());
            Assert.Equal("invalid_request", error.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.DoesNotContain("data:", Body(callback), StringComparison.Ordinal);
            Assert.DoesNotContain(ConciergeEndChat.Farewell, Body(callback), StringComparison.Ordinal);
            Assert.False(connection.ConfirmEndChatCall("call-end_chat"));
        }
        else
        {
            Assert.Contains(ConciergeEndChat.Farewell, Body(callback), StringComparison.Ordinal);
        }
        Assert.Equal(2, host.ChatClient.Requests.Count);
        await ReplayAsync(workflow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealThinkPathReplacesTheAttemptAndRejectsAnOldHashRetry(bool callerDisconnects)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var registry = host.App.Services.GetRequiredService<VoiceSessionRegistry>();
        var connection = registry.Open(new(Guid.NewGuid(), stay,
            ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)),
            ConciergeTestHost.VerifiedGuest(stay)), null);
        await using var firstScope = host.App.Services.CreateAsyncScope();
        await using var revisedScope = host.App.Services.CreateAsyncScope();
        var old = ThinkContext(firstScope.ServiceProvider, connection, "You said");
        var revised = ThinkContext(revisedScope.ServiceProvider, connection, "Where is breakfast?");
        var entered = Source();
        var release = Source();
        host.ChatClient.BeforeResponse = async messages =>
        {
            if (LastUser(messages) != "You said") return;
            entered.TrySetResult();
            await release.Task;
        };
        using var abort = new CancellationTokenSource();
        old.RequestAborted = abort.Token;
        var obsolete = ThinkEndpoint.HandleAsync(old);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        if (callerDisconnects)
        {
            abort.Cancel();
            await obsolete.WaitAsync(TimeSpan.FromSeconds(15));
        }
        try
        {
            await ThinkEndpoint.HandleAsync(revised).WaitAsync(TimeSpan.FromSeconds(15));
            await obsolete.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(200, revised.Response.StatusCode);
            Assert.Contains("Answer: Where is breakfast?", Body(revised), StringComparison.Ordinal);
            if (!callerDisconnects)
            {
                Assert.Equal(503, old.Response.StatusCode);
                Assert.DoesNotContain("data:", Body(old), StringComparison.Ordinal);
            }
            // A new HTTP delivery must consult workflow authority, not a cached successful RPC.
            var retryOld = ThinkContext(firstScope.ServiceProvider, connection, "You said");
            await ThinkEndpoint.HandleAsync(retryOld);
            Assert.Equal(503, retryOld.Response.StatusCode);
            Assert.Contains("unavailable", Body(retryOld), StringComparison.Ordinal);
            var retryCurrent = ThinkContext(revisedScope.ServiceProvider, connection, "Where is breakfast?");
            await ThinkEndpoint.HandleAsync(retryCurrent);
            Assert.Equal(200, retryCurrent.Response.StatusCode);
            Assert.Contains("Answer: Where is breakfast?", Body(retryCurrent), StringComparison.Ordinal);
            Assert.Equal(2, host.ChatClient.Requests.Count);
            Assert.Equal("Where is breakfast?",
                Assert.Single((await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns).GuestMessage);
        }
        finally
        {
            release.TrySetResult();
        }
        await ReplayAsync(workflow);
    }

    [Fact]
    public async Task ThinkDisconnectAloneDoesNotSupersedeOrCancelAdmittedWork()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var registry = host.App.Services.GetRequiredService<VoiceSessionRegistry>();
        var connection = registry.Open(new(Guid.NewGuid(), stay,
            ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)),
            ConciergeTestHost.VerifiedGuest(stay)), null);
        await using var scope = host.App.Services.CreateAsyncScope();
        var request = ThinkContext(scope.ServiceProvider, connection, "Where is breakfast?");
        using var abort = new CancellationTokenSource();
        request.RequestAborted = abort.Token;
        var entered = Source();
        var release = Source();
        host.ChatClient.BeforeResponse = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var disconnected = ThinkEndpoint.HandleAsync(request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        abort.Cancel();
        await disconnected.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Empty((await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns);
        release.TrySetResult();
        await workflow.ExecuteUpdateAsync(wf => wf.EndInteractionAsync()).WaitAsync(TimeSpan.FromSeconds(15));
        var turn = Assert.Single((await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns);
        Assert.Equal("Answer: Where is breakfast?", turn.AssistantMessage);
        Assert.False(turn.Superseded);
        Assert.Single(host.ChatClient.Requests);
        await ReplayAsync(workflow);
    }

    [Fact]
    public async Task PendingModelIsReplacedBeforeItReturnsAndExactRetriesNeverReactivateIt()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var utterance = new VoiceUtterance(Guid.NewGuid(), 1, 6);
        var old = Turn(stay, utterance, "I am allergic to shellfish.");
        var revised = Turn(stay, utterance, "Where is breakfast?");
        var entered = Source();
        var release = Source();
        var returned = Source();
        host.ChatClient.BeforeResponse = async messages =>
        {
            if (LastUser(messages) != old.UserMessage) return;
            entered.TrySetResult();
            await release.Task;
            returned.TrySetResult();
        };
        // A late response trying to book must not even schedule its proposal tool.
        host.ChatClient.ResponseFactory = messages => LastUser(messages) == old.UserMessage
            ? Tool(ConciergeToolDefinitions.ProposeRide, ("dropoff", "Airport"), ("immediate", true))
            : Answer(LastUser(messages));
        var first = await StartTurnAsync(workflow, old);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var joined = await StartTurnAsync(workflow, old);
        try
        {
            var result = await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(revised))
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, result.Sequence);
            await AssertSupersededAsync(first.GetResultAsync());
            await AssertSupersededAsync(joined.GetResultAsync());
            await AssertSupersededAsync(workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old)));
            Assert.Equal(result, await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(revised)));
            Assert.Equal(2, host.ChatClient.Requests.Count);
            Assert.DoesNotContain(host.ChatClient.Requests[^1].Messages,
                message => message.Text.Contains("shellfish", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            release.TrySetResult();
        }
        await returned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var history = await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        Assert.Equal(revised.TurnId, Assert.Single(history.Turns).TurnId);
        Assert.Empty(host.ActionTools.Calls);
        await ReplayAsync(workflow);
    }

    [Fact]
    public async Task QueuedFragmentIsSkippedButAnotherOrdinalAndItsExactRetryStillJoin()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var session = Guid.NewGuid();
        var first = Turn(stay, new(session, 1, 1), "First unrelated question");
        var old = Turn(stay, new(session, 1, 2), "You said");
        var revised = Turn(stay, old.VoiceUtterance!, "You said breakfast starts when?");
        var entered = Source();
        var release = Source();
        host.ChatClient.BeforeResponse = async messages =>
        {
            if (LastUser(messages) != first.UserMessage) return;
            entered.TrySetResult();
            await release.Task;
        };
        var active = await StartTurnAsync(workflow, first);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var retry = await StartTurnAsync(workflow, first);
        var queued = await StartTurnAsync(workflow, old);
        var replacement = await StartTurnAsync(workflow, revised);
        release.TrySetResult();
        Assert.Equal(await active.GetResultAsync(), await retry.GetResultAsync());
        await AssertSupersededAsync(queued.GetResultAsync());
        await replacement.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal([first.UserMessage, revised.UserMessage], host.ChatClient.Requests.Select(call => call.LastUserText));
        Assert.Equal([first.TurnId, revised.TurnId],
            (await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns.Select(turn => turn.TurnId));
        await ReplayAsync(workflow);
    }

    [Theory]
    [InlineData(ConciergeToolDefinitions.GetWeather)]
    [InlineData(ConciergeToolDefinitions.ProposeRide)]
    public async Task PendingReadOrProposalCannotCommitAfterRevision(string tool)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var old = Turn(stay, new(Guid.NewGuid(), 1, 7), "Old fragment");
        var revised = Turn(stay, old.VoiceUtterance!, "Where is the pool?");
        host.ChatClient.ResponseFactory = messages => LastUser(messages) == old.UserMessage
            ? tool == ConciergeToolDefinitions.GetWeather
                ? Tool(tool, ("period", "today"))
                : Tool(tool, ("dropoff", "Airport"), ("immediate", true))
            : Answer(LastUser(messages));
        var entered = Source();
        var release = Source();
        host.ActionTools.BeforeResult = async name =>
        {
            if (name != tool) return;
            entered.TrySetResult();
            await release.Task;
        };
        var first = await StartTurnAsync(workflow, old);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(revised))
                .WaitAsync(TimeSpan.FromSeconds(15));
            await AssertSupersededAsync(first.GetResultAsync());
        }
        finally
        {
            release.TrySetResult();
        }
        host.ChatClient.ResponseFactory = _ => Tool(ConciergeToolDefinitions.ConfirmRide);
        var confirm = new ConciergeTurnSubmission("confirm-after-revision", "Yes", "web",
            ConciergeTestHost.VerifiedGuest(stay));
        var refused = await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(confirm));
        Assert.Contains("no ride offer", refused.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tool, Assert.Single(host.ActionTools.Calls).Name);
        Assert.DoesNotContain(
            (await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns,
            turn => turn.TurnId == old.TurnId);
        await ReplayAsync(workflow);
    }

    [Fact]
    public async Task IntermediateToolStateAndFutureToolRoundsAreWithdrawn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var old = Turn(stay, new(Guid.NewGuid(), 1, 7), "Find a pharmacy for a ride");
        var revised = Turn(stay, old.VoiceUtterance!, "Tell me about breakfast");
        var entered = Source();
        var release = Source();
        host.ChatClient.BeforeResponse = async messages =>
        {
            if (LastUser(messages) == old.UserMessage && messages.Any(message => message.Role == ChatRole.Tool))
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        host.ChatClient.ResponseFactory = messages => LastUser(messages) != old.UserMessage
            ? Answer(LastUser(messages))
            : messages.Any(message => message.Role == ChatRole.Tool)
                ? Tool(ConciergeToolDefinitions.ProposeRide, ("dropoff", "Scripted Pharmacy"), ("immediate", true))
                : Tool(ConciergeToolDefinitions.FindNearbyPlaces, ("category", "pharmacy"), ("forRideProposal", true));
        var first = await StartTurnAsync(workflow, old);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(revised)).WaitAsync(TimeSpan.FromSeconds(15));
            await AssertSupersededAsync(first.GetResultAsync());
            Assert.DoesNotContain(host.ChatClient.Requests[^1].Messages,
                message => message.Text.Contains("Scripted Pharmacy", StringComparison.Ordinal));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(ConciergeToolDefinitions.FindNearbyPlaces, Assert.Single(host.ActionTools.Calls).Name);
        await ReplayAsync(workflow);
    }

    [Fact]
    public async Task CompletedReversibleTurnIsWithdrawnWithoutRewritingItsPersistedText()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var old = Turn(stay, new(Guid.NewGuid(), 1, 6), "I am allergic to shellfish.");
        var revised = Turn(stay, old.VoiceUtterance!, "Where is breakfast?");
        var first = await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old));
        await workflow.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        var result = await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(revised));
        Assert.Equal(first.Sequence + 1, result.Sequence);
        Assert.DoesNotContain(host.ChatClient.Requests[^1].Messages,
            message => message.Text.Contains("shellfish", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(revised.TurnId,
            Assert.Single((await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns).TurnId);
        await workflow.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        await using var command = host.DataSource.CreateCommand(
            "SELECT guest_message, assistant_message, superseded FROM transcript_turns WHERE stay_id = $1 AND turn_id = $2;");
        command.Parameters.AddWithValue(stay);
        command.Parameters.AddWithValue(old.TurnId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(old.UserMessage, reader.GetString(0));
        Assert.Equal(first.AssistantMessage, reader.GetString(1));
        Assert.True(reader.GetBoolean(2));
        await AssertSupersededAsync(workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old)));
        await ReplayAsync(workflow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedProposalCanBeWithdrawnButCannotBeConfirmedAfterItsRevision(bool cancellation)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var old = Turn(stay, new(Guid.NewGuid(), 1, 6), "Prepare the action");
        host.ChatClient.ScriptedResponses.Enqueue(cancellation
            ? Tool(ConciergeToolDefinitions.ProposeRideCancellation, ("rideId", Guid.NewGuid().ToString("D")))
            : Tool(ConciergeToolDefinitions.ProposeRide, ("dropoff", "Airport"), ("immediate", true)));
        await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old));
        var revised = Turn(stay, old.VoiceUtterance!, "Tell me about breakfast");
        await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(revised));
        var action = cancellation ? ConciergeToolDefinitions.ConfirmRideCancellation : ConciergeToolDefinitions.ConfirmRide;
        host.ChatClient.ScriptedResponses.Enqueue(Tool(action));
        var confirm = new ConciergeTurnSubmission("later-confirmation", "Yes", "web",
            ConciergeTestHost.VerifiedGuest(stay));
        var refused = await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(confirm));
        Assert.Contains(cancellation ? "no ride cancellation offer" : "no ride offer",
            refused.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(host.ActionTools.Calls, call => call.Name == action);
        await ReplayAsync(workflow);
    }

    [Fact]
    public async Task TextShapedTurnIdWithoutAuthenticatedVoiceProvenanceCannotSupersede()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var old = Turn(stay, new(Guid.NewGuid(), 1, 1), "Original");
        await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old));
        var notAuthenticatedVoice = Turn(stay, old.VoiceUtterance!, "Different text") with { VoiceUtterance = null };
        await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(notAuthenticatedVoice));
        Assert.Equal([old.TurnId, notAuthenticatedVoice.TurnId],
            (await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns.Select(turn => turn.TurnId));
        Assert.Contains(old.UserMessage, host.ChatClient.Requests[^1].UserTexts);
        await ReplayAsync(workflow);
    }

    [Fact]
    public async Task RevisionCapacityFailsClosedWithoutSupersedingTheLastAdmittedVersion()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var stay = await host.SeedStayAsync("Revision", "301",
            ConciergeTestHost.HotelToday, ConciergeTestHost.HotelToday.AddDays(1));
        var latest = Turn(stay, new(Guid.NewGuid(), 1, 6), "Last admitted text");
        var state = new ConciergeVoiceRevision(latest.VoiceUtterance!.Family, latest.TurnId,
            Enumerable.Range(0, ConciergeStayWorkflow.MaxRevisionsPerUtterance - 1)
                .Select(index => latest.VoiceUtterance.TurnId($"Earlier text {index}")).ToArray(),
            ConciergeStayWorkflow.Fingerprint(latest.Channel, latest.UserMessage, latest.EnableEndChatFunction));
        var input = new ConciergeStayWorkflowInput(stay,
            ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)), 30,
            VoiceRevisions: [state]);
        var workflow = await host.Environment.Client.StartWorkflowAsync(
            (ConciergeStayWorkflow wf) => wf.RunAsync(input),
            new($"voice-capacity-{stay:N}", TemporalWorkerConnection.TaskQueue));
        var tooMany = Turn(stay, latest.VoiceUtterance, "One too many revisions");
        var failure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(
            () => workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(tooMany)));
        Assert.Contains("too many revisions", Assert.IsType<ApplicationFailureException>(failure.InnerException).Message);
        Assert.Empty(host.ChatClient.Requests);
        await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(latest));
        Assert.Equal(latest.TurnId,
            Assert.Single((await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns).TurnId);
        await ReplayAsync(workflow);
    }

    [Fact]
    public async Task HistoricRevisionNeverRollsBackALaterUnrelatedInteraction()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var session = Guid.NewGuid();
        var old = Turn(stay, new(session, 1, 1), "I prefer tea.");
        var later = Turn(stay, new(session, 1, 2), "I am allergic to peanuts.");
        await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old));
        await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(later));
        var revised = Turn(stay, old.VoiceUtterance!, "Where is breakfast?");
        await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(revised));
        Assert.Contains(host.ChatClient.Requests[^1].Messages,
            message => message.Text.Contains("peanuts", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(later.UserMessage, host.ChatClient.Requests[^1].UserTexts);
        Assert.DoesNotContain(old.UserMessage, host.ChatClient.Requests[^1].UserTexts);
        await ReplayAsync(workflow);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RevisionAfterDispatchWaitsForOneAuthoritativeBookingOrCancellation(bool cancellation, bool uncertain)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var ride = Guid.NewGuid();
        var proposalTool = cancellation ? ConciergeToolDefinitions.ProposeRideCancellation : ConciergeToolDefinitions.ProposeRide;
        var actionTool = cancellation ? ConciergeToolDefinitions.ConfirmRideCancellation : ConciergeToolDefinitions.ConfirmRide;
        host.ChatClient.ScriptedResponses.Enqueue(cancellation
            ? Tool(proposalTool, ("rideId", ride.ToString("D")))
            : Tool(proposalTool, ("dropoff", "Airport"), ("immediate", true)));
        var offer = new ConciergeTurnSubmission("offer", "Please prepare the action", "web",
            ConciergeTestHost.VerifiedGuest(stay));
        await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(offer));
        var old = Turn(stay, new(Guid.NewGuid(), 1, 8), "Yes");
        var revised = Turn(stay, old.VoiceUtterance!, "Yes, that one please");
        host.ChatClient.ScriptedResponses.Enqueue(Tool(actionTool));
        if (uncertain)
        {
            host.ActionTools.QueueResult(actionTool, JsonSerializer.Serialize(
                new ConciergeRideActionResult(true, "The service did not confirm the outcome. Check ride history before trying again.", null, []),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        var entered = Source();
        var release = Source();
        host.ActionTools.BeforeResult = async name =>
        {
            if (name != actionTool) return;
            entered.TrySetResult();
            await release.Task;
        };
        var first = await StartTurnAsync(workflow, old);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var replacement = await StartTurnAsync(workflow, revised);
        Assert.False(first.GetResultAsync().IsCompleted);
        Assert.False(replacement.GetResultAsync().IsCompleted);
        release.TrySetResult();
        await AssertSupersededAsync(first.GetResultAsync());
        var result = await replacement.GetResultAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(host.ActionTools.Calls, call => call.Name == actionTool);
        Assert.Equal(2, host.ChatClient.Requests.Count);
        Assert.Contains(uncertain ? "did not confirm" : cancellation ? "canceled" : "You're all set",
            result.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        var effective = await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync());
        Assert.Equal([offer.TurnId, revised.TurnId], effective.Turns.Select(turn => turn.TurnId));
        await AssertSupersededAsync(workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old)));
        Assert.Equal(result, await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(revised)));
        Assert.Single(host.ActionTools.Calls, call => call.Name == actionTool);
        await ReplayAsync(workflow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevisionBeforeDispatchCannotBookOrCancel(bool cancellation)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var proposal = cancellation
            ? Tool(ConciergeToolDefinitions.ProposeRideCancellation, ("rideId", Guid.NewGuid().ToString("D")))
            : Tool(ConciergeToolDefinitions.ProposeRide, ("dropoff", "Airport"), ("immediate", true));
        host.ChatClient.ScriptedResponses.Enqueue(proposal);
        var offer = new ConciergeTurnSubmission("offer", "Prepare an action", "web", ConciergeTestHost.VerifiedGuest(stay));
        await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(offer));
        var old = Turn(stay, new(Guid.NewGuid(), 1, 3), "Yes");
        var revised = Turn(stay, old.VoiceUtterance!, "Wait, no");
        var entered = Source();
        var release = Source();
        host.ChatClient.BeforeResponse = async messages =>
        {
            if (LastUser(messages) != old.UserMessage) return;
            entered.TrySetResult();
            await release.Task;
        };
        host.ChatClient.ResponseFactory = messages => LastUser(messages) == old.UserMessage
            ? Tool(cancellation ? ConciergeToolDefinitions.ConfirmRideCancellation : ConciergeToolDefinitions.ConfirmRide)
            : Answer(LastUser(messages));
        var first = await StartTurnAsync(workflow, old);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(revised)).WaitAsync(TimeSpan.FromSeconds(15));
            await AssertSupersededAsync(first.GetResultAsync());
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.DoesNotContain(host.ActionTools.Calls,
            call => call.Name is ConciergeToolDefinitions.ConfirmRide or ConciergeToolDefinitions.ConfirmRideCancellation);
        await ReplayAsync(workflow);
    }

    [Fact]
    public async Task DifferentSessionConnectionOrdinalOrStayDoesNotSupersede()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stay, workflow) = await StartAsync(host);
        var identity = new VoiceUtterance(Guid.NewGuid(), 1, 4);
        var requests = new[]
        {
            Turn(stay, identity, "First"),
            Turn(stay, identity with { Ordinal = 5 }, "Second"),
            Turn(stay, identity with { Connection = 2 }, "Third"),
            Turn(stay, identity with { SessionId = Guid.NewGuid() }, "Fourth"),
        };
        foreach (var request in requests)
        {
            await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(request));
        }
        Assert.Equal(requests.Select(request => request.TurnId),
            (await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns.Select(turn => turn.TurnId));
        var (otherStay, otherWorkflow) = await StartAsync(host, "302");
        var other = Turn(otherStay, identity, "Different stay");
        await otherWorkflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(other));
        Assert.Equal(4, (await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns.Count);
        await ReplayAsync(workflow);
    }

    [Theory]
    [InlineData("reversible", false)]
    [InlineData("booking", false)]
    [InlineData("booking", true)]
    [InlineData("cancellation", false)]
    [InlineData("cancellation", true)]
    public async Task ContinueAsNewRetainsRevisionCheckpointRetriesAndAuthoritativeReceipts(string kind, bool uncertain)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql,
            devServerArgs: ["--dynamic-config-value", "limit.historyCount.suggestContinueAsNew=160"]);
        var (stay, workflow) = await StartAsync(host);
        var firstRun = (await workflow.DescribeAsync()).RunId;
        var deadline = ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2));
        var cancellation = kind == "cancellation";
        var actionTool = cancellation ? ConciergeToolDefinitions.ConfirmRideCancellation : ConciergeToolDefinitions.ConfirmRide;
        if (kind != "reversible")
        {
            host.ChatClient.ScriptedResponses.Enqueue(cancellation
                ? Tool(ConciergeToolDefinitions.ProposeRideCancellation, ("rideId", Guid.NewGuid().ToString("D")))
                : Tool(ConciergeToolDefinitions.ProposeRide, ("dropoff", "Airport"), ("immediate", true)));
            var offer = new ConciergeTurnSubmission("offer", "Prepare an action", "web",
                ConciergeTestHost.VerifiedGuest(stay));
            await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(offer));
            host.ChatClient.ScriptedResponses.Enqueue(Tool(actionTool));
            if (uncertain)
            {
                host.ActionTools.QueueResult(actionTool, JsonSerializer.Serialize(
                    new ConciergeRideActionResult(true, "The service did not confirm the outcome. Check ride history before trying again.", null, []),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
        }
        var old = Turn(stay, new(Guid.NewGuid(), 1, 6), kind == "reversible" ? "I am allergic to shellfish." : "Yes");
        var first = await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old));
        await workflow.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());

        // Server history events, not wall-clock sleeps, trigger the production CAN path.
        // Read-cursor updates leave the last conversational checkpoint intact.
        for (var index = 0; index < 100 && (await workflow.DescribeAsync()).RunId == firstRun; index++)
        {
            await workflow.ExecuteUpdateAsync(wf => wf.AdvanceRideActivityReadOffsetAsync(new(0)));
        }
        Assert.NotEqual(firstRun, (await workflow.DescribeAsync()).RunId);
        var history = await workflow.FetchHistoryAsync();
        var carried = await host.Environment.Client.Options.DataConverter
            .ToSingleValueAsync<ConciergeStayWorkflowInput>(
                history.Events.First().WorkflowExecutionStartedEventAttributes.Input.Payloads_);
        Assert.Equal(deadline, carried.EligibilityDeadlineUtc);
        var revision = Assert.Single(carried.VoiceRevisions!);
        Assert.Equal(old.TurnId, revision.LatestTurnId);
        if (kind == "reversible") Assert.Equal(old.TurnId, carried.VoiceRevisionCheckpoint?.TurnId);
        else Assert.Equal(first.AssistantMessage, revision.ActionReceipt);

        var modelCalls = host.ChatClient.Requests.Count;
        Assert.Equal(first, await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old)));
        Assert.Equal(modelCalls, host.ChatClient.Requests.Count);
        var changed = Turn(stay, old.VoiceUtterance!, "Where is breakfast?");
        var next = await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(changed));
        if (kind == "reversible")
        {
            Assert.DoesNotContain(host.ChatClient.Requests[^1].Messages,
                message => message.Text.Contains("shellfish", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Equal(first.AssistantMessage, next.AssistantMessage);
            Assert.Equal(modelCalls, host.ChatClient.Requests.Count);
            Assert.Single(host.ActionTools.Calls, call => call.Name == actionTool);
        }
        await AssertSupersededAsync(workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old)));
        Assert.DoesNotContain((await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns,
            turn => turn.TurnId == old.TurnId);
        await ReplayAsync(workflow);
        await ReplayAsync(host.Environment.Client.GetWorkflowHandle<ConciergeStayWorkflow>(workflow.Id, firstRun));
    }

    [Fact]
    public async Task SupersededUncommittedRetryRemainsTerminalAcrossContinueAsNew()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql,
            devServerArgs: ["--dynamic-config-value", "limit.historyCount.suggestContinueAsNew=160"]);
        var (stay, workflow) = await StartAsync(host);
        var firstRun = (await workflow.DescribeAsync()).RunId;
        var old = Turn(stay, new(Guid.NewGuid(), 1, 7), "Fragment");
        var revised = Turn(stay, old.VoiceUtterance!, "Where is the pool?");
        var entered = Source();
        var release = Source();
        host.ChatClient.BeforeResponse = async messages =>
        {
            if (LastUser(messages) != old.UserMessage) return;
            entered.TrySetResult();
            await release.Task;
        };
        var obsolete = await StartTurnAsync(workflow, old);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            await workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(revised));
            await AssertSupersededAsync(obsolete.GetResultAsync());
        }
        finally
        {
            release.TrySetResult();
        }
        await workflow.ExecuteUpdateAsync(wf => wf.EndInteractionAsync());
        for (var index = 0; index < 100 && (await workflow.DescribeAsync()).RunId == firstRun; index++)
        {
            await workflow.ExecuteUpdateAsync(wf => wf.AdvanceRideActivityReadOffsetAsync(new(0)));
        }
        Assert.NotEqual(firstRun, (await workflow.DescribeAsync()).RunId);
        var calls = host.ChatClient.Requests.Count;
        await AssertSupersededAsync(workflow.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(old)));
        Assert.Equal(calls, host.ChatClient.Requests.Count);
        Assert.Equal(revised.TurnId,
            Assert.Single((await workflow.ExecuteUpdateAsync(wf => wf.GetHistoryAsync())).Turns).TurnId);
        await ReplayAsync(workflow);
        await ReplayAsync(host.Environment.Client.GetWorkflowHandle<ConciergeStayWorkflow>(workflow.Id, firstRun));
    }

    private static TaskCompletionSource Source() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static DefaultHttpContext ThinkContext(
        IServiceProvider services, VoiceConnection connection, string text, int ordinal = 1, string? resultCallId = null)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Headers.Authorization = $"Bearer {connection.Bearer}";
        var messages = Enumerable.Range(1, ordinal)
            .Select(index => (object)new { role = "user", content = index == ordinal ? text : "Earlier question" })
            .ToList();
        if (resultCallId is not null)
        {
            messages.Add(new
            {
                role = "assistant",
                tool_calls = new[] { new
                {
                    id = resultCallId, type = "function",
                    function = new { name = "end_chat", arguments = "{}" },
                } },
            });
            messages.Add(new { role = "tool", tool_call_id = resultCallId, name = "end_chat", content = "End chat confirmed." });
        }
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            model = "test",
            stream = true,
            // These untrusted fields must not affect callback or turn authority.
            turn_id = "client-controlled",
            family = "client-controlled",
            messages,
        })));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string Body(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static string LastUser(IReadOnlyList<ChatMessage> messages) =>
        messages.Last(message => message.Role == ChatRole.User).Text;

    private static ChatResponse Answer(string text) => new(new ChatMessage(ChatRole.Assistant, $"Answer: {text}"));

    private static ChatResponse Tool(string name, params (string Key, object? Value)[] arguments) =>
        new(new ChatMessage(ChatRole.Assistant, [
            new FunctionCallContent($"call-{name}", name, arguments.ToDictionary(pair => pair.Key, pair => pair.Value)),
        ])) { FinishReason = ChatFinishReason.ToolCalls };

    private static ConciergeTurnSubmission Turn(Guid stay, VoiceUtterance identity, string text) =>
        new(identity.TurnId(text), text, "voice", ConciergeTestHost.VerifiedGuest(stay),
            EnableEndChatFunction: true, VoiceUtterance: identity);

    private static Task<WorkflowUpdateHandle<ConciergeTurnResult>> StartTurnAsync(
        WorkflowHandle<ConciergeStayWorkflow> workflow, ConciergeTurnSubmission turn) =>
        workflow.StartUpdateAsync(wf => wf.SubmitTurnAsync(turn), new(WorkflowUpdateStage.Accepted));

    private static async Task AssertSupersededAsync(Task<ConciergeTurnResult> result)
    {
        var failure = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(
            () => result.WaitAsync(TimeSpan.FromSeconds(15)));
        var cause = Assert.IsType<ApplicationFailureException>(failure.InnerException);
        Assert.Equal(ConciergeStayWorkflow.UnavailableErrorType, cause.ErrorType);
        Assert.Contains("replaced by revised speech", cause.Message, StringComparison.Ordinal);
    }

    private static async Task<(Guid, WorkflowHandle<ConciergeStayWorkflow>)> StartAsync(ConciergeTestHost host, string room = "301")
    {
        var stay = await host.SeedStayAsync("Revision", room,
            ConciergeTestHost.HotelToday, ConciergeTestHost.HotelToday.AddDays(1));
        return (stay, await host.StartStayAsync(stay,
            ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2))));
    }

    private static async Task ReplayAsync(WorkflowHandle<ConciergeStayWorkflow> workflow)
    {
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions
        {
            DataConverter = DurableAIDataConverter.Instance,
        }.AddWorkflow<ConciergeStayWorkflow>());
        await replayer.ReplayWorkflowAsync(await workflow.FetchHistoryAsync());
    }
}
