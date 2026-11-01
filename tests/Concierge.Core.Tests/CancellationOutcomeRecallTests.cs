using System.Text.Json;
using Concierge.Contracts;
using Concierge.Web.Voice;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;
using Microsoft.Extensions.AI;
using Temporalio.Client;
using Temporalio.Converters;
using Temporalio.Worker;

namespace Concierge.Core.Tests;

public sealed class CancellationOutcomeRecallTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RevisedSpokenConfirmationCannotReopenConsumedBooking(bool confirmed, bool continueAsNew)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql,
            devServerArgs: continueAsNew
                ? ["--dynamic-config-value", "limit.historyCount.suggestContinueAsNew=50"] : null,
            flushBatchSize: 1);
        var (stayId, handle) = await StartAsync(host);
        var firstRun = (await handle.DescribeAsync()).RunId;
        var session = Guid.NewGuid();
        Select(host, ConciergeToolDefinitions.ProposeRide,
            new() { ["dropoff"] = "South Beach", ["immediate"] = true });
        await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 10, "Offer"), "A ride to South Beach.");
        if (!confirmed)
            host.ActionTools.QueueResult(ConciergeToolDefinitions.ConfirmRide, JsonSerializer.Serialize(
                new ConciergeRideActionResult(true, "The booking outcome is unknown.", null, []), JsonOptions));
        Select(host, ConciergeToolDefinitions.ConfirmRide);
        const string partial = "Okay. Can you book the ride?";
        var original = await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 11, partial), partial);
        if (continueAsNew)
        {
            for (var wait = 0; wait < 50 && (await handle.DescribeAsync()).RunId == firstRun; wait++)
                await Task.Delay(100);
            Assert.NotEqual(firstRun, (await handle.DescribeAsync()).RunId);
        }

        // The live failure: revised text has a different hash but the same spoken ordinal.
        const string revised = "Okay. Can you book the ride? To arrive in about five minutes?";
        Select(host, ConciergeToolDefinitions.ProposeRide,
            new() { ["dropoff"] = "South Beach", ["immediate"] = true });
        var response = await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 11, revised), revised);
        if (confirmed)
        {
            const string pickupTime = "Tuesday, October 20 at 12:00 PM hotel time";
            Assert.Contains("Hotel ZGY", original.AssistantMessage, StringComparison.Ordinal);
            Assert.Contains("South Beach, Miami Beach", original.AssistantMessage, StringComparison.Ordinal);
            Assert.Contains("at Hotel ZGY now", original.AssistantMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(pickupTime, original.AssistantMessage, StringComparison.Ordinal);
            Assert.Equal(
                $"Your ride from Hotel ZGY to South Beach, Miami Beach is booked for pickup on {pickupTime} (status: Booked).",
                response.AssistantMessage);
        }
        else
        {
            Assert.Equal("The booking outcome is unknown.", original.AssistantMessage);
            Assert.Equal(original.AssistantMessage, response.AssistantMessage);
            Assert.DoesNotContain("is booked", response.AssistantMessage, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("No ride has been booked", response.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("You're all set", response.AssistantMessage, StringComparison.Ordinal);

        // Another revision cannot turn the consumed utterance into a scheduled replacement either.
        const string laterRevision = "Book the ride for ten minutes from now.";
        Select(host, ConciergeToolDefinitions.ProposeRide, new()
        {
            ["dropoff"] = "South Beach", ["immediate"] = false,
            ["pickupAt"] = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
        });
        var laterResponse = await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 11, laterRevision), laterRevision);
        Assert.Equal(response.AssistantMessage, laterResponse.AssistantMessage);
        Select(host, ConciergeToolDefinitions.ConfirmRide);
        await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 12, "Book it"), "Book it");
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ProposeRide);

        // A genuinely new spoken request can still book the same route after fresh confirmation.
        Select(host, ConciergeToolDefinitions.ProposeRide,
            new() { ["dropoff"] = "South Beach", ["immediate"] = true });
        await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 13, "A new ride"), "Please book a new ride.");
        Select(host, ConciergeToolDefinitions.ConfirmRide);
        await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 14, "Yes"), "Yes");
        Assert.Equal(2, host.ActionTools.Calls.Count(call => call.Name == ConciergeToolDefinitions.ConfirmRide));
        await ReplayAsync(handle);
        if (continueAsNew)
            await ReplayAsync(host.Environment.Client.GetWorkflowHandle<ConciergeStayWorkflow>(handle.Id, firstRun));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewSpokenRequestToBookedDestinationWarnsAndCanBookAfterFreshConfirmation(bool continueAsNew)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql,
            devServerArgs: continueAsNew
                ? ["--dynamic-config-value", "limit.historyCount.suggestContinueAsNew=80"] : null,
            flushBatchSize: 1);
        var (stayId, handle) = await StartAsync(host);
        var firstRun = (await handle.DescribeAsync()).RunId;
        var session = Guid.NewGuid();
        Select(host, ConciergeToolDefinitions.ProposeRide,
            new() { ["dropoff"] = "Wayan Miami", ["immediate"] = true });
        await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 10, "Offer"), "A ride to Wayan Miami.");
        Select(host, ConciergeToolDefinitions.ConfirmRide);
        await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 11, "Book"), "Okay. Can you book the ride?");

        // Even an exact overlapping request is a new offer, not an edit to the earlier booking.
        var pickupAt = new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);
        host.ActionTools.QueueResult(ConciergeToolDefinitions.ProposeRide, JsonSerializer.Serialize(
            new ConciergeRideProposalToolResult(
                "Available", null, [],
                new ConciergeRideProposalDraft(
                    new ConciergeRidePlace("Hotel ZGY", 25.7984634, -80.1956016),
                    new ConciergeRidePlace("Wayan Miami, 50 NW 24th St", 25.8003, -80.1957),
                    false, pickupAt, 720),
                null, null, null,
                ExistingRides: [
                    new RideSummary(Guid.NewGuid(), "Booked", "Hotel ZGY", "Wayan Miami, 50 NW 24th St",
                        pickupAt, null, null, null),
                ]), JsonOptions));
        Select(host, ConciergeToolDefinitions.ProposeRide, new()
        {
            ["dropoff"] = "Wayan Miami", ["immediate"] = false,
            ["pickupAt"] = pickupAt.ToString("O"),
        });
        const string again = "Can you book the ride to arrive in ten minutes?";
        var offered = await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 12, again), again);
        Assert.Contains("ADDITIONAL ride, not a change", offered.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("confirmation on a later turn", offered.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("Shall I book it", offered.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("No ride has been booked.", offered.AssistantMessage, StringComparison.Ordinal);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);

        if (continueAsNew)
        {
            for (var wait = 0; wait < 50 && (await handle.DescribeAsync()).RunId == firstRun; wait++)
                await Task.Delay(100);
            Assert.NotEqual(firstRun, (await handle.DescribeAsync()).RunId);
            var history = await handle.FetchHistoryAsync();
            var input = await host.Environment.Client.Options.DataConverter
                .ToSingleValueAsync<ConciergeStayWorkflowInput>(
                    history.Events.First().WorkflowExecutionStartedEventAttributes.Input.Payloads_);
            Assert.Equal(pickupAt, input.PendingRideProposal?.PickupAtUtc);
            Assert.Equal("Wayan Miami, 50 NW 24th St", input.PendingRideProposal?.Dropoff.Name);
        }

        Select(host, ConciergeToolDefinitions.ConfirmRide);
        var bookNow = await SubmitAsync(handle, stayId, ThinkRequest.TurnId(session, 1, 13, "Book it now"),
            "Book it now, please.");
        Assert.Contains("You're all set", bookNow.AssistantMessage, StringComparison.Ordinal);
        var bookings = host.ActionTools.Calls.Where(call => call.Name == ConciergeToolDefinitions.ConfirmRide).ToArray();
        Assert.Equal(2, bookings.Length);
        Assert.NotEqual(bookings[0].Arguments["proposalId"], bookings[1].Arguments["proposalId"]);
        var additional = JsonSerializer.Deserialize<ConciergeRideProposal>(
            bookings[1].Arguments[ConciergeToolDefinitions.TrustedProposalKey]!.ToString()!, JsonOptions)!;
        Assert.Equal(pickupAt, additional.PickupAtUtc);
        var proposals = host.ActionTools.Calls.Where(call => call.Name == ConciergeToolDefinitions.ProposeRide).ToArray();
        Assert.Equal(2, proposals.Length);
        Assert.All(proposals, call => Assert.Equal(
            stayId.ToString("D"), call.Arguments[ConciergeToolDefinitions.TrustedStayIdKey]?.ToString()));
        await ReplayAsync(handle);
        if (continueAsNew)
            await ReplayAsync(host.Environment.Client.GetWorkflowHandle<ConciergeStayWorkflow>(handle.Id, firstRun));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConsumedBookingGuardSurvivesImmediateContinueAsNew(bool confirmed)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql,
            devServerArgs: ["--dynamic-config-value", "limit.historyCount.suggestContinueAsNew=50"],
            flushBatchSize: 1);
        var (stayId, handle) = await StartAsync(host);
        var firstRun = (await handle.DescribeAsync()).RunId;
        Select(host, ConciergeToolDefinitions.ProposeRide,
            new() { ["dropoff"] = "South Beach", ["immediate"] = true });
        await SubmitAsync(handle, stayId, "offer", "Please book me a ride to South Beach.");
        Assert.Equal(firstRun, (await handle.DescribeAsync()).RunId);
        if (!confirmed)
            host.ActionTools.QueueResult(ConciergeToolDefinitions.ConfirmRide, JsonSerializer.Serialize(
                new ConciergeRideActionResult(true, "The booking outcome is unknown.", null, []), JsonOptions));
        Select(host, ConciergeToolDefinitions.ConfirmRide);
        await SubmitAsync(handle, stayId, "book", "Yes, book it.");

        // No filler turn: the guard applies only to the first turn after the consumed offer.
        for (var wait = 0; wait < 50 && (await handle.DescribeAsync()).RunId == firstRun; wait++)
            await Task.Delay(100);
        Assert.NotEqual(firstRun, (await handle.DescribeAsync()).RunId);
        var rolloverHistory = await handle.FetchHistoryAsync();

        Select(host, ConciergeToolDefinitions.ProposeRide,
            new() { ["dropoff"] = "South Beach", ["immediate"] = true });
        var acknowledgement = await SubmitAsync(handle, stayId, "ack", "Yes, please.");
        Select(host, ConciergeToolDefinitions.ConfirmRide);
        await SubmitAsync(handle, stayId, "repeat-confirm", "Yes.");

        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ProposeRide);
        Assert.Contains("already closed", acknowledgement.AssistantMessage, StringComparison.Ordinal);
        var payloads = rolloverHistory.Events.First().WorkflowExecutionStartedEventAttributes.Input.Payloads_;
        var input = await host.Environment.Client.Options.DataConverter.ToSingleValueAsync<JsonElement>(payloads);
        Assert.Equal(2, input.GetProperty("nextSequence").GetInt64());
        Assert.Equal(1, input.GetProperty("consumedRideOfferSequence").GetInt64());
        var original = Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);
        Assert.Equal(original.Arguments["proposalId"]?.ToString(),
            input.GetProperty("consumedRideOffer").GetProperty("proposalId").GetString());

        Select(host, ConciergeToolDefinitions.ProposeRide,
            new() { ["dropoff"] = "South Beach", ["immediate"] = true });
        await SubmitAsync(handle, stayId, "explicit-new", "Please book a new ride to South Beach.");
        Select(host, ConciergeToolDefinitions.ConfirmRide);
        await SubmitAsync(handle, stayId, "explicit-confirm", "Book the new ride.");
        var bookings = host.ActionTools.Calls.Where(call => call.Name == ConciergeToolDefinitions.ConfirmRide).ToArray();
        Assert.Equal(2, bookings.Length);
        Assert.NotEqual(bookings[0].Arguments["proposalId"]?.ToString(), bookings[1].Arguments["proposalId"]?.ToString());
        await ReplayAsync(handle);
        await ReplayAsync(host.Environment.Client.GetWorkflowHandle<ConciergeStayWorkflow>(handle.Id, firstRun));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UncertainDifferentRideCancellationDoesNotRecallEarlierSuccess(bool continueAsNew, bool malformed)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql,
            devServerArgs: ["--dynamic-config-value", "limit.historyCount.suggestContinueAsNew=160"],
            flushBatchSize: 1);
        var (stayId, handle) = await StartAsync(host);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await StageAsync(host, handle, stayId, first, "South Beach", "offer-a");
        QueueCancellation(host, first);
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "cancel-a", "Yes.");
        await StageAsync(host, handle, stayId, second, "Airport", "offer-b");
        if (malformed)
            host.ActionTools.QueueResult(ConciergeToolDefinitions.ConfirmRideCancellation, "not-json");
        else
            QueueCancellation(host, null, "The cancellation outcome is unknown.");
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        var uncertain = await SubmitAsync(handle, stayId, "cancel-b", "Yes, cancel the airport ride.");
        Assert.Equal(malformed
            ? "The ride service did not confirm cancellation. Check ride status before trying again."
            : "The cancellation outcome is unknown.", uncertain.AssistantMessage);
        // This prompt is built after B was staged, before its uncertain dispatch clears A's fallback.
        Assert.Contains("South Beach", Assert.Single(host.ChatClient.Requests[^1].Messages, IsOutcomeNote).Text,
            StringComparison.Ordinal);

        if (continueAsNew)
        {
            var run = (await handle.DescribeAsync()).RunId;
            for (var turn = 0; turn < 15 && (await handle.DescribeAsync()).RunId == run; turn++)
            {
                await SubmitAsync(handle, stayId, $"filler-{turn}", "What can you help with?");
                for (var wait = 0; wait < 20 && (await handle.DescribeAsync()).RunId == run; wait++)
                    await Task.Delay(100);
            }
            Assert.NotEqual(run, (await handle.DescribeAsync()).RunId);
            var history = await handle.FetchHistoryAsync();
            var input = await host.Environment.Client.Options.DataConverter.ToSingleValueAsync<ConciergeStayWorkflowInput>(
                history.Events.First().WorkflowExecutionStartedEventAttributes.Input.Payloads_);
            Assert.Null(input.LastConfirmedCancellation);
        }

        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        var repeated = await SubmitAsync(handle, stayId, "repeat-b", "Yes, cancel that airport ride.");
        Assert.DoesNotContain("South Beach", repeated.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was cancelled", repeated.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("No new cancellation was submitted", repeated.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("status", repeated.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(host.ChatClient.Requests[^1].Messages, IsOutcomeNote);
        var cancellations = host.ActionTools.Calls.Where(call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation).ToArray();
        Assert.Equal(new[] { first.ToString("D"), second.ToString("D") },
            cancellations.Select(call => call.Arguments["rideId"]?.ToString()));

        host.ActionTools.QueueResult(ConciergeToolDefinitions.GetRideStatus, JsonSerializer.Serialize(
            new ConciergeRideActionResult(true, null,
                new RideSummary(second, "Cancelled", "Hotel ZGY", "Airport",
                    DateTimeOffset.Parse("2026-10-24T10:00:00-04:00"), null, null, null), []), JsonOptions));
        Select(host, ConciergeToolDefinitions.GetRideStatus, new() { ["rideId"] = second.ToString("D") });
        var recovered = await SubmitAsync(handle, stayId, "status-b", "Check the airport ride's current status.");
        Assert.Contains("Airport", recovered.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("was canceled", recovered.AssistantMessage, StringComparison.Ordinal);
        Assert.Equal(second.ToString("D"), host.ActionTools.Calls.Last().Arguments["rideId"]?.ToString());
        Assert.Equal(2, host.ActionTools.Calls.Count(call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation));
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task UncertainSameRideAttemptPreservesEstablishedCancellation()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartAsync(host);
        var rideId = Guid.NewGuid();
        await StageAsync(host, handle, stayId, rideId, "South Beach", "offer");
        QueueCancellation(host, rideId);
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "cancel", "Yes.");
        // A stale read may offer the same ride again; uncertainty cannot undo known success.
        await StageAsync(host, handle, stayId, rideId, "South Beach", "stale-offer");
        QueueCancellation(host, null, "The cancellation outcome is unknown.");
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "uncertain", "Yes.");
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        var repeated = await SubmitAsync(handle, stayId, "repeat", "Yes, cancel it.");
        Assert.Contains("South Beach", repeated.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("already cancelled", repeated.AssistantMessage, StringComparison.Ordinal);
        Assert.Single(host.ChatClient.Requests[^1].Messages, IsOutcomeNote);
        Assert.Equal(2, host.ActionTools.Calls.Count(call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation));
        await ReplayAsync(handle);
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("missing")]
    [InlineData("different-ride")]
    public async Task OnlyMatchingCancellationConfirmationHandsOffItsSourceRequest(string outcome)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartAsync(host);
        var rideId = Guid.NewGuid();
        await StageAsync(host, handle, stayId, rideId, "South Beach", "cancel-request",
            "Please arrange cancellation of my ride to South Beach.");
        QueueCancellation(host, outcome switch
        {
            "confirmed" => rideId,
            "different-ride" => Guid.NewGuid(),
            _ => null,
        });
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "confirm", "Yes.");

        await SubmitAsync(handle, stayId, "recall", "What happened to my request?");

        var memory = Assert.Single(host.ChatClient.Requests[^1].Messages, message =>
            message.Role == ChatRole.System && message.Text.StartsWith("Guest-reported stay continuity", StringComparison.Ordinal)).Text;
        var expectedCategory = outcome == "confirmed" ? "request-history" : "unresolved-request";
        Assert.Contains($"Guest-reported {expectedCategory} [transcript sequence 0]", memory, StringComparison.Ordinal);
        Assert.Contains("guest requested cancellation of my ride to South Beach", memory, StringComparison.Ordinal);
        if (outcome != "confirmed")
            Assert.DoesNotContain("request-history", memory, StringComparison.Ordinal);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ConfirmedRequestHistorySurvivesActualContinueAsNew()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql,
            devServerArgs: ["--dynamic-config-value", "limit.historyCount.suggestContinueAsNew=160"],
            flushBatchSize: 1);
        var (stayId, handle) = await StartAsync(host);
        var firstRun = (await handle.DescribeAsync()).RunId;
        Select(host, ConciergeToolDefinitions.ProposeRide,
            new() { ["dropoff"] = "South Beach", ["immediate"] = true });
        await SubmitAsync(handle, stayId, "book", "Please book me a ride to South Beach.");
        Select(host, ConciergeToolDefinitions.ConfirmRide);
        await SubmitAsync(handle, stayId, "confirm", "Yes, please.");
        for (var turn = 0; turn < 15 && (await handle.DescribeAsync()).RunId == firstRun; turn++)
        {
            await SubmitAsync(handle, stayId, $"filler-{turn}", "What can you help with?");
            for (var wait = 0; wait < 20 && (await handle.DescribeAsync()).RunId == firstRun; wait++)
                await Task.Delay(100);
        }
        Assert.NotEqual(firstRun, (await handle.DescribeAsync()).RunId);

        await SubmitAsync(handle, stayId, "after-can", "What did I request?");
        Assert.Contains(host.ChatClient.Requests[^1].Messages, message =>
            message.Role == ChatRole.System
            && message.Text.Contains("Guest-reported request-history [transcript sequence 0]", StringComparison.Ordinal));
        var history = await handle.FetchHistoryAsync();
        var input = await host.Environment.Client.Options.DataConverter.ToSingleValueAsync<ConciergeStayWorkflowInput>(
            history.Events.First().WorkflowExecutionStartedEventAttributes.Input.Payloads_);
        var remembered = Assert.Single(input.StayMemory!);
        Assert.Equal("request-history", remembered.Category);
        Assert.Equal(0, remembered.SourceSequence);
        Assert.Equal("guest requested me a ride to South Beach", remembered.Statement);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);
        await ReplayAsync(handle);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyConfirmedBookingHandsOffItsSourceRequestFromUnresolvedMemory(bool confirmed)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartAsync(host);
        await SubmitAsync(handle, stayId, "towels", "Please send extra towels.");
        Select(host, ConciergeToolDefinitions.ProposeRide,
            new() { ["dropoff"] = "South Beach", ["immediate"] = true });
        await SubmitAsync(handle, stayId, "book", "Please book me a ride to South Beach.");
        if (!confirmed)
        {
            // Model/renderer prose cannot replace a typed booking confirmation.
            host.ActionTools.QueueResult(ConciergeToolDefinitions.ConfirmRide, JsonSerializer.Serialize(
                new ConciergeRideActionResult(true, "Your ride is booked.", null, []), JsonOptions));
        }
        Select(host, ConciergeToolDefinitions.ConfirmRide);
        await SubmitAsync(handle, stayId, "confirm", "Yes, please.");

        await SubmitAsync(handle, stayId, "recall", "What did I ask for?");

        var memory = Assert.Single(host.ChatClient.Requests[^1].Messages, message =>
            message.Role == ChatRole.System && message.Text.StartsWith("Guest-reported stay continuity", StringComparison.Ordinal)).Text;
        Assert.Contains("Guest-reported unresolved-request [transcript sequence 0]: guest requested extra towels", memory, StringComparison.Ordinal);
        if (confirmed)
        {
            Assert.Contains("Guest-reported request-history [transcript sequence 1]: guest requested me a ride to South Beach", memory, StringComparison.Ordinal);
            Assert.DoesNotContain("Guest-reported unresolved-request [transcript sequence 1]", memory, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("Guest-reported unresolved-request [transcript sequence 1]", memory, StringComparison.Ordinal);
            Assert.DoesNotContain("request-history", memory, StringComparison.Ordinal);
        }
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);
        await ReplayAsync(handle);
    }

    [Theory]
    [InlineData("Yes. Cancel that ride.", 1, 7)]
    [InlineData("Actually, don't cancel it.", 1, 7)]
    [InlineData("Cancel my other ride instead.", 1, 7)]
    [InlineData("Yes. Cancel that ride.", 2, 1)]
    public async Task FollowUpReceivesConfirmedOutcomeWithoutCollapsingChangedTextOrConnections(
        string question, int connection, int ordinal)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartAsync(host);
        var rideId = Guid.NewGuid();
        await StageAsync(host, handle, stayId, rideId, "South Beach", "offer");
        var voiceSession = Guid.NewGuid();
        var originalId = ThinkRequest.TurnId(voiceSession, 1, 7, "Yes.");
        QueueCancellation(host, rideId);
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        var original = await SubmitAsync(handle, stayId, originalId, "Yes.");
        var callsBeforeRetry = host.ChatClient.Requests.Count;

        var retry = await SubmitAsync(handle, stayId, originalId, "Yes.");
        Assert.Equal(original, retry);
        Assert.Equal(callsBeforeRetry, host.ChatClient.Requests.Count);

        // Even if the model searches upcoming rides again, the confirmed cancellation stays in context.
        Select(host, ConciergeToolDefinitions.GetRideHistory,
            new() { ["when"] = "upcoming", ["purpose"] = "select" });
        host.ChatClient.AnswerFactory = messages => messages.Any(IsOutcomeNote)
            ? "The earlier cancellation has already succeeded."
            : "I couldn't find an upcoming ride to cancel.";
        var changedId = ThinkRequest.TurnId(voiceSession, connection, ordinal, question);
        var changed = await SubmitAsync(handle, stayId, changedId, question);

        Assert.NotEqual(originalId, changedId);
        Assert.Equal(original.Sequence + 1, changed.Sequence);
        Assert.Equal("The earlier cancellation has already succeeded.", changed.AssistantMessage);
        Assert.All(host.ChatClient.Requests.TakeLast(2), request =>
        {
            Assert.Equal(question, request.LastUserText);
            var note = Assert.Single(request.Messages, IsOutcomeNote);
            Assert.Contains("South Beach", note.Text, StringComparison.Ordinal);
            Assert.Contains("already cancelled", note.Text, StringComparison.Ordinal);
            Assert.Contains("'Don't cancel' cannot undo it", note.Text, StringComparison.Ordinal);
            Assert.Contains("explicit new ride request", note.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(rideId.ToString("D"), note.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(originalId, note.Text, StringComparison.Ordinal);
        });
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);
        await ReplayAsync(handle);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different-ride")]
    [InlineData("unavailable")]
    public async Task MissingMismatchedOrUncertainSuccessDoesNotCreateOutcomeRecall(string outcome)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartAsync(host);
        var rideId = Guid.NewGuid();
        await StageAsync(host, handle, stayId, rideId, "South Beach", "offer");
        QueueCancellation(host, outcome == "different-ride" ? Guid.NewGuid() : null,
            outcome == "unavailable" ? "The cancellation outcome is unknown." : "Your ride is canceled.");
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "confirm", "Yes.");

        await SubmitAsync(handle, stayId, "follow-up", "Actually, don't cancel it.");

        Assert.DoesNotContain(host.ChatClient.Requests[^1].Messages, IsOutcomeNote);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task RepeatedConfirmationReportsSpecificCommittedOutcomeWithoutAnotherMutation()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartAsync(host);
        var rideId = Guid.NewGuid();
        await StageAsync(host, handle, stayId, rideId, "South Beach", "offer");
        QueueCancellation(host, rideId);
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "original", "Yes.");
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);

        var followUp = await SubmitAsync(handle, stayId, "revised", "Yes. Cancel that ride.");

        Assert.Contains("South Beach", followUp.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("already cancelled", followUp.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("different ride", followUp.AssistantMessage, StringComparison.Ordinal);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);
        await ReplayAsync(handle);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("show")]
    public async Task EmptyUpcomingHistoryReportsConfirmedCancellationInsteadOfForgettingIt(string? purpose)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartAsync(host);
        var rideId = Guid.NewGuid();
        await StageAsync(host, handle, stayId, rideId, "South Beach", "offer");
        QueueCancellation(host, rideId);
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "confirm", "Yes.");
        Select(host, ConciergeToolDefinitions.GetRideHistory,
            new() { ["when"] = "upcoming", ["purpose"] = purpose });

        var result = await SubmitAsync(handle, stayId, "negation", "Actually, don't cancel it.");

        Assert.Contains("don't have any upcoming rides", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("South Beach", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("already cancelled", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);
        Assert.Equal(3, host.ChatClient.Requests.Count);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ChangedRequestAdmittedBeforeCancellationFinishesWaitsAndSeesCommittedOutcome()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartAsync(host);
        var rideId = Guid.NewGuid();
        await StageAsync(host, handle, stayId, rideId, "South Beach", "offer");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatClient.Gate = gate;
        QueueCancellation(host, rideId);
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        Select(host, ConciergeToolDefinitions.GetRideHistory,
            new() { ["when"] = "upcoming", ["purpose"] = "show" });
        var firstRequest = new ConciergeTurnSubmission("original", "Yes.", "voice", ConciergeTestHost.VerifiedGuest(stayId));
        var changedRequest = new ConciergeTurnSubmission("revised", "Actually, don't cancel it.", "voice", ConciergeTestHost.VerifiedGuest(stayId));
        var original = await handle.StartUpdateAsync(
            workflow => workflow.SubmitTurnAsync(firstRequest), new(WorkflowUpdateStage.Accepted));
        var revised = await handle.StartUpdateAsync(
            workflow => workflow.SubmitTurnAsync(changedRequest), new(WorkflowUpdateStage.Accepted));

        Assert.DoesNotContain(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);
        gate.SetResult();
        var firstResult = await original.GetResultAsync();
        var revisedResult = await revised.GetResultAsync();

        Assert.Equal(firstResult.Sequence + 1, revisedResult.Sequence);
        Assert.Contains("already cancelled", revisedResult.AssistantMessage, StringComparison.Ordinal);
        Assert.Single(host.ChatClient.Requests[^1].Messages, IsOutcomeNote);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ConfirmedCancellationDoesNotBlockCancellationOfADifferentRide()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartAsync(host);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await StageAsync(host, handle, stayId, first, "South Beach", "offer-a");
        QueueCancellation(host, first);
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "cancel-a", "Yes.");
        await StageAsync(host, handle, stayId, second, "Airport", "offer-b");
        QueueCancellation(host, second);
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "cancel-b", "Yes, cancel the airport ride.");

        var cancellations = host.ActionTools.Calls
            .Where(call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation).ToArray();
        Assert.Equal(new[] { first.ToString("D"), second.ToString("D") },
            cancellations.Select(call => call.Arguments["rideId"]?.ToString()));
        await SubmitAsync(handle, stayId, "recall-b", "What happened to the cancellation?");
        var note = Assert.Single(host.ChatClient.Requests[^1].Messages, IsOutcomeNote);
        Assert.Contains("Airport", note.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("South Beach", note.Text, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Theory]
    [InlineData("South Beach, Miami Beach, FL 33139, United States of America", 0, true, false)]
    [InlineData("South Beach, Miami Beach, FL 33139", 0, true, false)]
    [InlineData("Airport", 0, false, false)]
    [InlineData("South Beach, Miami Beach, FL 33139", 60, false, false)]
    [InlineData("South Beach, Miami Beach, FL 33139", 0, true, true)]
    public async Task MatchingReplacementOfferStatesCancellationAndRequiresFreshConfirmation(
        string destination, int minutesLater, bool matchesCancelledTrip, bool hasAnotherActiveRide)
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartAsync(host);
        var rideId = Guid.NewGuid();
        await StageAsync(host, handle, stayId, rideId, "South Beach, Miami Beach, FL 33139, United States of America", "offer");
        QueueCancellation(host, rideId);
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "cancel", "Yes.");
        var pickup = DateTimeOffset.Parse("2026-10-24T10:00:00-04:00").AddMinutes(minutesLater);
        host.ActionTools.QueueResult(ConciergeToolDefinitions.ProposeRide, JsonSerializer.Serialize(
            new ConciergeRideProposalToolResult("Available", null, [], new ConciergeRideProposalDraft(
                new("Hotel ZGY", 25.8, -80.2), new(destination, 25.7, -80.1), false, pickup, 720), null, null, null,
                ExistingRides: hasAnotherActiveRide
                    ? [new RideSummary(Guid.NewGuid(), "Booked", "Hotel ZGY", destination, pickup, null, null, null)]
                    : []), JsonOptions));
        Select(host, ConciergeToolDefinitions.ProposeRide,
            new() { ["dropoff"] = destination, ["immediate"] = false, ["pickupAt"] = pickup.ToString("O") });

        var proposed = await SubmitAsync(handle, stayId, "replacement", "Actually, don't cancel it.");

        if (matchesCancelledTrip)
        {
            Assert.Contains("already cancelled", proposed.AssistantMessage, StringComparison.Ordinal);
            if (!hasAnotherActiveRide)
            {
                Assert.Contains("Would you like a new booking", proposed.AssistantMessage, StringComparison.Ordinal);
                Assert.Contains("No new ride has been booked", proposed.AssistantMessage, StringComparison.Ordinal);
            }
        }
        if (hasAnotherActiveRide)
        {
            Assert.Contains("ADDITIONAL ride, not a change", proposed.AssistantMessage, StringComparison.Ordinal);
            Assert.Contains("confirmation on a later turn", proposed.AssistantMessage, StringComparison.Ordinal);
        }
        if (!matchesCancelledTrip)
        {
            Assert.DoesNotContain("already cancelled", proposed.AssistantMessage, StringComparison.Ordinal);
            Assert.Contains(destination == "Airport" ? "Airport" : "South Beach, Miami Beach",
                proposed.AssistantMessage, StringComparison.Ordinal);
            if (minutesLater == 60)
            {
                Assert.Contains("11:00 AM", proposed.AssistantMessage, StringComparison.Ordinal);
            }
        }
        Assert.DoesNotContain(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);

        Select(host, ConciergeToolDefinitions.ConfirmRide);
        await SubmitAsync(handle, stayId, "new-consent", "Yes, book the new ride.");
        var booking = Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);
        Assert.NotEqual(rideId.ToString("D"), booking.Arguments["proposalId"]?.ToString());
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ConfirmedCancellationRecallSurvivesActualContinueAsNew()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql,
            devServerArgs: ["--dynamic-config-value", "limit.historyCount.suggestContinueAsNew=160"],
            flushBatchSize: 1);
        var (stayId, handle) = await StartAsync(host);
        var firstRun = (await handle.DescribeAsync()).RunId;
        var rideId = Guid.NewGuid();
        await StageAsync(host, handle, stayId, rideId, "South Beach", "offer");
        QueueCancellation(host, rideId);
        Select(host, ConciergeToolDefinitions.ConfirmRideCancellation);
        await SubmitAsync(handle, stayId, "confirm", "Yes.");
        for (var turn = 0; turn < 15 && (await handle.DescribeAsync()).RunId == firstRun; turn++)
        {
            await SubmitAsync(handle, stayId, $"filler-{turn}", "What can you help with?");
            for (var wait = 0; wait < 20 && (await handle.DescribeAsync()).RunId == firstRun; wait++)
                await Task.Delay(100);
        }
        Assert.NotEqual(firstRun, (await handle.DescribeAsync()).RunId);

        await SubmitAsync(handle, stayId, "after-can", "Did the cancellation succeed?");
        var note = Assert.Single(host.ChatClient.Requests[^1].Messages, IsOutcomeNote);
        Assert.Contains("South Beach", note.Text, StringComparison.Ordinal);
        Assert.Contains("already cancelled", note.Text, StringComparison.Ordinal);
        var history = await handle.FetchHistoryAsync();
        var input = await host.Environment.Client.Options.DataConverter.ToSingleValueAsync<ConciergeStayWorkflowInput>(
            history.Events.First().WorkflowExecutionStartedEventAttributes.Input.Payloads_);
        // Assert the carried wire input, not just recent transcript text that happens to mention success.
        var receipt = Assert.IsType<ConciergeRideCancellationOutcome>(input.LastConfirmedCancellation);
        Assert.Equal(rideId, receipt.RideId);
        Assert.Equal("South Beach", receipt.Dropoff);
        Assert.Equal("confirm", receipt.TurnId);
        Assert.Equal(1, receipt.Sequence);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);
        await ReplayAsync(handle);
    }

    private static bool IsOutcomeNote(ChatMessage message) =>
        message.Role == ChatRole.System && message.Text.StartsWith("Last confirmed cancellation:", StringComparison.Ordinal);

    private static async Task<(Guid StayId, WorkflowHandle<ConciergeStayWorkflow> Handle)> StartAsync(ConciergeTestHost host)
    {
        var stayId = await host.SeedStayAsync("Recall", "222", ConciergeTestHost.HotelToday, ConciergeTestHost.HotelToday.AddDays(1));
        return (stayId, await host.StartStayAsync(stayId, ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2))));
    }

    private static Task<ConciergeTurnResult> SubmitAsync(WorkflowHandle<ConciergeStayWorkflow> handle,
        Guid stayId, string turnId, string text) => handle.ExecuteUpdateAsync(workflow => workflow.SubmitTurnAsync(
            new ConciergeTurnSubmission(turnId, text, "voice", ConciergeTestHost.VerifiedGuest(stayId))));

    private static async Task StageAsync(ConciergeTestHost host, WorkflowHandle<ConciergeStayWorkflow> handle,
        Guid stayId, Guid rideId, string destination, string turnId, string? requestText = null)
    {
        host.ActionTools.QueueResult(ConciergeToolDefinitions.GetRideStatus, JsonSerializer.Serialize(
            new ConciergeRideActionResult(true, null,
                new RideSummary(rideId, "Booked", "Hotel ZGY", destination,
                    DateTimeOffset.Parse("2026-10-24T10:00:00-04:00"), null, null, null), []), JsonOptions));
        Select(host, ConciergeToolDefinitions.ProposeRideCancellation, new() { ["rideId"] = rideId.ToString("D") });
        await SubmitAsync(handle, stayId, turnId, requestText ?? $"Cancel my ride to {destination}.");
    }

    private static void QueueCancellation(ConciergeTestHost host, Guid? confirmedId, string message = "Your ride is canceled.") =>
        host.ActionTools.QueueResult(ConciergeToolDefinitions.ConfirmRideCancellation, JsonSerializer.Serialize(
            new { simulated = true, message, ride = (object?)null, rides = Array.Empty<object>(), confirmedCancellationRideId = confirmedId }, JsonOptions));

    private static void Select(ConciergeTestHost host, string tool, Dictionary<string, object?>? arguments = null) =>
        host.ChatClient.ScriptedResponses.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(Guid.NewGuid().ToString("N"), tool, arguments ?? new())]))
            { FinishReason = ChatFinishReason.ToolCalls });

    private static async Task ReplayAsync(WorkflowHandle<ConciergeStayWorkflow> handle) =>
        await new WorkflowReplayer(new WorkflowReplayerOptions
        { DataConverter = TemporalCommunity.Extensions.AI.DurableAIDataConverter.Instance }.AddWorkflow<ConciergeStayWorkflow>())
            .ReplayWorkflowAsync(await handle.FetchHistoryAsync());
}
