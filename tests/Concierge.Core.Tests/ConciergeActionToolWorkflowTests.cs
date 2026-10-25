using System.Net;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;
using Microsoft.Extensions.AI;
using Temporalio.Client;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace Concierge.Core.Tests;

public sealed class ConciergeActionToolWorkflowTests(PostgreSqlFixture postgreSql)
    : IClassFixture<PostgreSqlFixture>
{
    [Fact]
    public async Task NoToolTurnUsesOneAnswerCallWithActivityMaterializedTools()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);

        var result = await SubmitAsync(host, handle, stayId, "plain", "What time is breakfast?");

        Assert.Equal("Answer: What time is breakfast?", result.AssistantMessage);
        Assert.Single(host.ChatClient.Requests);
        Assert.Single(host.ChatClient.Requests);
        var request = Assert.Single(host.ChatClient.Requests);
        Assert.Equal(
            new[]
            {
                ConciergeToolDefinitions.GetWeather,
                ConciergeToolDefinitions.GetRoute,
                ConciergeToolDefinitions.FindNearbyPlaces,
                ConciergeToolDefinitions.GetPlaceOpeningHours,
                ConciergeToolDefinitions.ProposeRide,
                ConciergeToolDefinitions.ConfirmRide,
                ConciergeToolDefinitions.GetRideStatus,
                ConciergeToolDefinitions.GetRideHistory,
                ConciergeToolDefinitions.ProposeRideCancellation,
                ConciergeToolDefinitions.ConfirmRideCancellation,
            },
            request.Tools);
        Assert.False(request.HasActionToolsMarker);
        Assert.Empty(host.ActionTools.Calls);
    }

    [Fact]
    public async Task WeatherToolAddsBoundedCuratedResultAsPairedMessagesAndCapturedHistoryReplays()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "weather-1",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["place"] = "Miami Beach", ["period"] = "tomorrow" }));

        var result = await SubmitAsync(host, handle, stayId, "weather", "What will the weather be tomorrow?");

        Assert.Single(host.ChatClient.Requests);
        Assert.Contains("![partly cloudy](/images/weather/partly-cloudy.svg)", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("Expect partly cloudy tomorrow in Miami Beach", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("high around 82°F", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("low near 70°F", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("10% chance", result.AssistantMessage, StringComparison.Ordinal);
        Assert.EndsWith(".", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", result.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ConciergeToolDefinitions.GetWeather, Assert.Single(host.ActionTools.Calls).Name);

        var history = await handle.FetchHistoryAsync();
        Assert.Contains(history.Events, historyEvent =>
            historyEvent.EventType == Temporalio.Api.Enums.V1.EventType.ActivityTaskScheduled);
        await ReplayAsync(history);
    }

    [Fact]
    public async Task WeatherToolRetriesTransientProviderFailuresThenAnswers()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        // The two failures seen live on 2026-10-21: an HttpClient timeout, then an Open-Meteo 503.
        host.ActionTools.WeatherFailures.Enqueue(new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 5 seconds elapsing.",
            new TimeoutException()));
        host.ActionTools.WeatherFailures.Enqueue(new HttpRequestException(
            "Open-Meteo request failed with HTTP status code 503.", null, HttpStatusCode.ServiceUnavailable));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "weather-retry",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["place"] = null, ["period"] = "today" }));

        var result = await SubmitAsync(host, handle, stayId, "weather-retry", "What's the weather like today?");

        Assert.Contains("high around 82°F", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Equal(3, host.ActionTools.Calls.Count(call => call.Name == ConciergeToolDefinitions.GetWeather));
        var history = await handle.FetchHistoryAsync();
        var started = Assert.Single(history.Events, historyEvent =>
            historyEvent.EventType == Temporalio.Api.Enums.V1.EventType.ActivityTaskStarted
            && history.Events.Any(scheduled =>
                scheduled.EventId == historyEvent.ActivityTaskStartedEventAttributes.ScheduledEventId
                && scheduled.ActivityTaskScheduledEventAttributes.ActivityType.Name
                    == "TemporalCommunity.Extensions.AI.InvokeFunction"));
        Assert.Equal(3, started.ActivityTaskStartedEventAttributes.Attempt);
        await ReplayAsync(history);
    }

    [Fact]
    public async Task WeatherToolDoesNotRetryNonTransientProviderFailure()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.WeatherFailures.Enqueue(new HttpRequestException(
            "Open-Meteo request failed with HTTP status code 400.", null, HttpStatusCode.BadRequest));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "weather-bad-request",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["place"] = null, ["period"] = "today" }));

        await SubmitAsync(host, handle, stayId, "weather-bad-request", "What's the weather like today?");

        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.GetWeather);
    }

    [Fact]
    public async Task RouteToolReportsDistanceDurationAndOnlyShortVoiceInstructions()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "route-1",
            ConciergeToolDefinitions.GetRoute,
            new Dictionary<string, object?> { ["places"] = new[] { "South Beach" } }));

        var result = await SubmitAsync(host, handle, stayId, "route", "How do I drive to South Beach?");

        Assert.Single(host.ChatClient.Requests);
        Assert.Contains("South Beach is about 3 miles away, roughly 12 minutes by car from Hotel ZGY.", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("1. Head north for two blocks.", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("2. Turn right onto the avenue.", result.AssistantMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NearbyPlacesToolReturnsTheWorkflowRenderedListWithoutASecondModelCall()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "nearby-1",
            ConciergeToolDefinitions.FindNearbyPlaces,
            new Dictionary<string, object?> { ["category"] = "pharmacy" }));

        var result = await SubmitAsync(host, handle, stayId, "nearby", "What is the nearest pharmacy?");

        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(
            "Here is the closest pharmacy to Hotel ZGY:\n\n1. **Scripted Pharmacy**, 100 Main Street (0.4 miles)",
            result.AssistantMessage);
        var call = Assert.Single(host.ActionTools.Calls);
        Assert.Equal(ConciergeToolDefinitions.FindNearbyPlaces, call.Name);
        Assert.Equal("pharmacy", call.Arguments["category"]);
        await ReplayAsync(await handle.FetchHistoryAsync());
    }

    [Fact]
    public async Task NamedPlaceHoursToolRendersProviderHoursWithoutNearbySearch()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "hours-wayan",
            ConciergeToolDefinitions.GetPlaceOpeningHours,
            new Dictionary<string, object?> { ["placeName"] = "Wayan Miami" }));

        var result = await SubmitAsync(host, handle, stayId, "hours-wayan", "What are Wayan Miami's hours?");

        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(
            "Wayan Miami's listed opening hours are 17:30-21:30; Th 17:30-22:00; Fr,Sa 17:30-22:30.",
            result.AssistantMessage);
        var call = Assert.Single(host.ActionTools.Calls);
        Assert.Equal(ConciergeToolDefinitions.GetPlaceOpeningHours, call.Name);
        Assert.Equal("Wayan Miami", call.Arguments["placeName"]);
        await ReplayAsync(await handle.FetchHistoryAsync());
    }

    [Fact]
    public async Task MissingNamedPlaceHoursAreReportedWithoutNearbyPlaceResults()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetPlaceOpeningHours,
            JsonSerializer.Serialize(
                new ConciergePlaceOpeningHoursToolResult("NotListed", "Some Cafe", null, []),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "hours-missing",
            ConciergeToolDefinitions.GetPlaceOpeningHours,
            new Dictionary<string, object?> { ["placeName"] = "Some Cafe" }));

        var result = await SubmitAsync(host, handle, stayId, "hours-missing", "What time does Some Cafe open?");

        Assert.Equal("Opening hours for Some Cafe are not listed.", result.AssistantMessage);
        Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.GetPlaceOpeningHours);
    }

    [Fact]
    public async Task UnknownNearbyCategoryIsRejectedWithoutCallingGeoapify()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "nearby-bad",
            ConciergeToolDefinitions.FindNearbyPlaces,
            new Dictionary<string, object?> { ["category"] = "casino" }));

        var result = await SubmitAsync(host, handle, stayId, "nearby-bad", "Where is the nearest casino?");

        Assert.StartsWith("I can't search for that kind of place.", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Empty(host.ActionTools.Calls);
    }

    [Fact]
    public async Task VoiceToolRepliesAreCompletePlainSentencesWithoutMarkdown()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "voice-weather",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["period"] = "today" }));

        var weather = await handle.ExecuteUpdateAsync(workflow => workflow.SubmitTurnAsync(
            Turn(stayId, "voice-weather", "What's the weather?", "voice")));

        Assert.DoesNotContain("![", weather.AssistantMessage, StringComparison.Ordinal);
        Assert.EndsWith(".", weather.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("high around 82°F", weather.AssistantMessage, StringComparison.Ordinal);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "voice-route",
            ConciergeToolDefinitions.GetRoute,
            new Dictionary<string, object?> { ["places"] = new[] { "South Beach" } }));
        var route = await handle.ExecuteUpdateAsync(workflow => workflow.SubmitTurnAsync(
            Turn(stayId, "voice-route", "How do I drive there?", "voice")));

        Assert.DoesNotContain("1.", route.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("South Beach is about 3 miles away", route.AssistantMessage, StringComparison.Ordinal);
        Assert.EndsWith(".", route.AssistantMessage, StringComparison.Ordinal);
        Assert.Equal(2, host.ChatClient.Requests.Count);
    }

    [Fact]
    public async Task WeatherTemplateOmitsUnknownFieldsInsteadOfPrintingFallbackData()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetWeather,
            JsonSerializer.Serialize(
                new ConciergeWeatherToolResult(
                    "Available", null, [], null, null, null, null, null, null, null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "weather-partial",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["period"] = "today" }));

        var result = await SubmitAsync(host, handle, stayId, "weather-partial", "What's the weather?");

        Assert.Contains("Expect mixed conditions today in the area.", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", result.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Single(host.ChatClient.Requests);
    }

    [Fact]
    public async Task AmbiguousWeatherAndRouteResultsAskTheGuestInsteadOfGuessing()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetWeather,
            JsonSerializer.Serialize(
                new ConciergeWeatherToolResult(
                    "NeedsClarification",
                    "airport",
                    ["Miami International Airport", "Fort Lauderdale Airport"],
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "ambiguous-weather",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["place"] = "airport", ["period"] = "today" }));
        await SubmitAsync(host, handle, stayId, "ambiguous-weather", "What's the weather at the airport?");

        var weatherResult = Assert.Single(host.ChatClient.Requests[1].Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()).Result?.ToString();
        Assert.Contains("Ask the guest which one they mean", weatherResult, StringComparison.Ordinal);
        Assert.Contains("Miami International Airport", weatherResult, StringComparison.Ordinal);

        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetRoute,
            JsonSerializer.Serialize(
                new ConciergeRouteToolResult(
                    "NeedsClarification",
                    "airport",
                    ["Miami International Airport", "Fort Lauderdale Airport"],
                    null,
                    null,
                    null,
                    null,
                    [],
                    [],
                    null,
                    null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "ambiguous-route",
            ConciergeToolDefinitions.GetRoute,
            new Dictionary<string, object?> { ["places"] = new[] { "airport" } }));
        await SubmitAsync(host, handle, stayId, "ambiguous-route", "How do I drive to the airport?");

        var routeResult = Assert.Single(host.ChatClient.Requests[3].Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()).Result?.ToString();
        Assert.Contains("Ask the guest which one they mean", routeResult, StringComparison.Ordinal);
        Assert.Contains("Fort Lauderdale Airport", routeResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedAndMixedEndChatCallsAreRejectedWithoutExecutingTools()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "unsupported-1",
            "DeleteAllGuestData",
            new Dictionary<string, object?>()));

        var unsupported = await SubmitAsync(host, handle, stayId, "unsupported", "What is the weather?");
        Assert.Contains("unsupported action", unsupported.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.ActionTools.Calls);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCalls(
            ("end-chat-mixed", ConciergeEndChat.FunctionName, new Dictionary<string, object?>()),
            ("weather-mixed", ConciergeToolDefinitions.GetWeather,
                new Dictionary<string, object?> { ["period"] = "today" })));
        var mixedTurn = Turn(stayId, "mixed-end-chat", "I'm done.", "voice") with
        {
            EnableEndChatFunction = true,
        };
        var mixed = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(mixedTurn));
        Assert.Null(mixed.EndChatFunctionCall);
        Assert.Contains("safely process", mixed.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.ActionTools.Calls);
    }

    [Fact]
    public async Task VoiceEndChatAlonePreservesTheClientHandshake()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "end-chat-1",
            ConciergeEndChat.FunctionName,
            new Dictionary<string, object?>()));

        var endChatTurn = Turn(stayId, "voice-end-chat", "Please end this conversation.", "voice") with
        {
            EnableEndChatFunction = true,
        };
        var result = await handle.ExecuteUpdateAsync(wf => wf.SubmitTurnAsync(endChatTurn));

        Assert.Equal(ConciergeEndChat.Farewell, result.AssistantMessage);
        Assert.NotNull(result.EndChatFunctionCall);
        Assert.Equal(ConciergeEndChat.FunctionName, result.EndChatFunctionCall!.Name);
        Assert.Empty(host.ActionTools.Calls);
    }

    [Fact]
    public async Task ToolLoopStopsAfterTwoRoundsAndThreeModelCalls()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var unresolvedRoute = JsonSerializer.Serialize(
            new ConciergeRouteToolResult(
                "NeedsClarification", "Miami", ["Miami, Florida"], null, null, null, null, [], [], null, null),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        for (var index = 1; index <= 3; index++)
        {
            host.ActionTools.QueueResult(ConciergeToolDefinitions.GetRoute, unresolvedRoute);
            host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
                $"route-{index}",
                ConciergeToolDefinitions.GetRoute,
                new Dictionary<string, object?> { ["places"] = new[] { "Miami" } }));
        }

        var result = await SubmitAsync(host, handle, stayId, "round-limit", "Show my upcoming rides.");

        Assert.Equal(3, host.ChatClient.Requests.Count);
        Assert.Equal(2, host.ActionTools.Calls.Count);
        Assert.Contains("allowed steps", result.AssistantMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NonTimeoutModelActivityFailureReturnsReplyAfterThreeAttempts()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.Fail = true;

        var result = await SubmitAsync(host, handle, stayId, "model-failure", "Check the weather.");

        Assert.Equal(
            "I couldn't reach the concierge service just now. Please try again in a moment.",
            result.AssistantMessage);
        Assert.Equal(3, host.ChatClient.Requests.Count);
        Assert.Empty(host.ActionTools.Calls);
        Assert.Equal(
            ["model-failure"],
            (await handle.ExecuteUpdateAsync(workflow => workflow.GetHistoryAsync())).Turns.Select(turn => turn.TurnId));

        host.ChatClient.Fail = false;
        var next = await SubmitAsync(host, handle, stayId, "model-recovered", "Is the spa open?");
        Assert.Equal(1, next.Sequence);
    }

    /// <summary>A hung model is retried; once every 20-second attempt times out the guest gets the deadline reply.</summary>
    [Fact]
    public async Task ModelActivityDeadlineExhaustionReturnsSafeAnswer()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ChatClient.Gate = gate;

        try
        {
            var result = await SubmitAsync(
                host, handle, stayId, "model-timeout", "Check the weather.")
                .WaitAsync(TimeSpan.FromSeconds(120));

            Assert.Contains("before the request deadline", result.AssistantMessage, StringComparison.Ordinal);
            Assert.Equal(3, host.ChatClient.Requests.Count);
            Assert.Empty(host.ActionTools.Calls);
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task DurableToolActivityTimeoutReturnsSafeResultAndContinuesTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ActionTools.WeatherGate = gate;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "timed-out-weather",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["place"] = null, ["period"] = "today" }));
        host.ChatClient.AnswerFactory = messages =>
        {
            var toolResult = messages.SelectMany(message => message.Contents)
                .OfType<FunctionResultContent>()
                .SingleOrDefault();
            if (toolResult is not null)
            {
                Assert.Contains("couldn't retrieve a reliable result", toolResult.Result?.ToString(), StringComparison.OrdinalIgnoreCase);
            }

            return "I couldn't retrieve the weather just now.";
        };

        try
        {
            var result = await SubmitAsync(
                host, handle, stayId, "weather-tool-timeout", "What is the weather today?")
                .WaitAsync(TimeSpan.FromSeconds(75));

            Assert.Equal("I couldn't retrieve the weather just now.", result.AssistantMessage);
            Assert.Equal(2, host.ChatClient.Requests.Count);
            // Weather retries a per-attempt timeout; the curated reply follows the third attempt.
            Assert.Equal(3, host.ActionTools.Calls.Count(call => call.Name == ConciergeToolDefinitions.GetWeather));
        }
        finally
        {
            host.ActionTools.WeatherGate = null;
            gate.TrySetResult();
        }
    }

    [Fact]
    public void WholeTurnActivityOptionsAreClampedToRemainingDeadline()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var options = new ActivityOptions
        {
            StartToCloseTimeout = TimeSpan.FromMinutes(1),
            ScheduleToCloseTimeout = TimeSpan.FromMinutes(3),
            RetryPolicy = new RetryPolicy { MaximumAttempts = 3 },
        };

        var bounded = ConciergeStayWorkflow.BoundActivityOptionsToDeadline(
            options, now, now.AddSeconds(12));
        Assert.Equal(TimeSpan.FromSeconds(12), bounded.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromSeconds(12), bounded.ScheduleToCloseTimeout);

        var expired = ConciergeStayWorkflow.BoundActivityOptionsToDeadline(
            options, now.AddSeconds(13), now.AddSeconds(12));
        Assert.Equal(TimeSpan.FromMilliseconds(1), expired.StartToCloseTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1), expired.ScheduleToCloseTimeout);
    }

    [Fact]
    public async Task DurableToolActivitiesUseTheirDocumentedPerToolExecutionTimeouts()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var rideId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var cases = new (string Name, string Prompt, IDictionary<string, object?> Arguments, TimeSpan Timeout)[]
        {
            (
                ConciergeToolDefinitions.GetWeather,
                "What is the weather today?",
                new Dictionary<string, object?> { ["period"] = "today" },
                TimeSpan.FromSeconds(12)),
            (
                ConciergeToolDefinitions.FindNearbyPlaces,
                "Where is the nearest pharmacy?",
                new Dictionary<string, object?> { ["category"] = "pharmacy" },
                TimeSpan.FromSeconds(8)),
            (
                ConciergeToolDefinitions.GetPlaceOpeningHours,
                "What are the opening hours?",
                new Dictionary<string, object?> { ["placeName"] = "Wayan Miami" },
                TimeSpan.FromSeconds(8)),
            (
                ConciergeToolDefinitions.GetRoute,
                "How do I drive to South Beach?",
                new Dictionary<string, object?> { ["places"] = new[] { "South Beach" } },
                TimeSpan.FromSeconds(18)),
            (
                ConciergeToolDefinitions.ProposeRide,
                "Could I get a ride to South Beach?",
                new Dictionary<string, object?> { ["dropoff"] = "South Beach", ["immediate"] = true },
                TimeSpan.FromSeconds(18)),
            (
                ConciergeToolDefinitions.ConfirmRide,
                "Please confirm the ride.",
                new Dictionary<string, object?>(),
                TimeSpan.FromSeconds(15)),
            (
                ConciergeToolDefinitions.GetRideStatus,
                "Where is my ride?",
                new Dictionary<string, object?> { ["rideId"] = rideId.ToString("D") },
                TimeSpan.FromSeconds(12)),
            (
                ConciergeToolDefinitions.GetRideHistory,
                "Show my upcoming rides.",
                new Dictionary<string, object?> { ["when"] = "upcoming" },
                TimeSpan.FromSeconds(12)),
            (
                ConciergeToolDefinitions.ProposeRideCancellation,
                "Would you cancel my ride?",
                new Dictionary<string, object?> { ["rideId"] = rideId.ToString("D") },
                TimeSpan.FromSeconds(12)),
            (
                ConciergeToolDefinitions.ConfirmRideCancellation,
                "Yes, cancel that ride.",
                new Dictionary<string, object?>(),
                TimeSpan.FromSeconds(12)),
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var tool = cases[index];
            host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
                $"timeout-{index}",
                tool.Name,
                tool.Arguments));

            await SubmitAsync(host, handle, stayId, $"timeout-{index}", tool.Prompt);
        }

        Assert.Equal(
            cases.Select(testCase => testCase.Name == ConciergeToolDefinitions.ProposeRideCancellation
                ? ConciergeToolDefinitions.GetRideStatus
                : testCase.Name),
            host.ActionTools.Calls.Select(call => call.Name));
        var history = await handle.FetchHistoryAsync();
        var scheduledTools = history.Events
            .Where(historyEvent =>
                historyEvent.ActivityTaskScheduledEventAttributes?.ActivityType.Name
                == "TemporalCommunity.Extensions.AI.InvokeFunction")
            .OrderBy(historyEvent => historyEvent.EventId)
            .ToArray();
        Assert.Equal(cases.Length, scheduledTools.Length);
        Assert.Equal(
            cases.Select(testCase => testCase.Timeout),
            scheduledTools.Select(scheduled =>
                scheduled.ActivityTaskScheduledEventAttributes!.StartToCloseTimeout.ToTimeSpan()));
    }

    [Fact]
    public async Task OversizedToolResultsAreReplacedOrBoundedBeforeEnteringModelContext()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetWeather,
            new string('x', 64_001));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "large-weather",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["place"] = "Miami", ["period"] = "today" }));

        await SubmitAsync(host, handle, stayId, "large-result", "What is the weather?");

        var result = Assert.Single(host.ChatClient.Requests[1].Messages
            .SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()).Result?.ToString() ?? string.Empty;
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(result) <= 2_048);
        Assert.DoesNotContain(new string('x', 100), result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActionContextBudgetCountsToolSchemasForLongUserTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var stayId = await SeedStayAsync(host);
        var handle = await host.StartStayAsync(
            stayId,
            ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)));

        var longMessage =
            "Check the weather. " + new string('x', ConciergeTurnLimits.MaxUserMessageUtf8Bytes - 19);
        var result = await SubmitAsync(
            host,
            handle,
            stayId,
            "context-limit",
            longMessage);

        Assert.Equal($"Answer: {longMessage}", result.AssistantMessage);
        var request = Assert.Single(host.ChatClient.Requests);
        Assert.Equal(10, request.Tools.Count);
        Assert.True(
            ConciergeStayWorkflow.EstimatePromptTokens(request.Messages)
            + ConciergeToolDefinitions.EstimateToolDefinitionTokens(includeEndChat: false)
            + ConciergeStayWorkflow.AnswerMaxOutputTokens
            + ConciergeStayWorkflow.ContextEstimationHeadroomTokens
            <= ConciergeStayWorkflow.ActionWorkingContextBudgetTokens);
    }

    [Fact]
    public async Task ToolFollowUpKeepsToolPairAndPriorTurnsWithinActionBudget()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = messages =>
        {
            var priorQuestion = messages.Last(message => message.Role == ChatRole.User).Text!;
            var turnMarker = priorQuestion[^2];
            return $"prior-turn-{turnMarker}-marker-" + new string('p', 1200);
        };
        for (var index = 0; index < 3; index++)
        {
            await SubmitAsync(
                host, handle, stayId, $"prior-context-{index}", $"Remember detail {index}.");
        }

        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetWeather,
            JsonSerializer.Serialize(
                new ConciergeWeatherToolResult(
                    "NeedsClarification",
                    "airport",
                    Enumerable.Range(0, 4).Select(index =>
                        $"candidate-{index}-" + new string('c', 140)).ToArray(),
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "weather-under-pressure",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["place"] = null, ["period"] = "today" }));
        host.ChatClient.AnswerFactory = _ => "The weather is partly cloudy.";
        // At the 21,000-token action budget a tool round no longer forces eviction from this host;
        // trimming and fail-closed behavior at the boundary are covered by ActionContextBudgetBoundaryTests.
        var userMessage = "What is the weather today? " + new string('x', 1180);
        var result = await SubmitAsync(host, handle, stayId, "tool-context-pressure", userMessage);

        Assert.Equal("The weather is partly cloudy.", result.AssistantMessage);
        var initial = host.ChatClient.Requests[^2];
        var initialPriorMarkers = initial.Messages
            .Select(message => message.Text)
            .Where(text => text?.Contains("prior-turn-", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.NotEmpty(initialPriorMarkers);
        var followup = host.ChatClient.Requests[^1];
        var functionCall = Assert.Single(followup.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionCallContent>());
        var functionResult = Assert.Single(followup.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>());
        Assert.Equal("weather-under-pressure", functionCall.CallId);
        Assert.Equal(functionCall.CallId, functionResult.CallId);
        var followupPriorMarkers = followup.Messages
            .Select(message => message.Text)
            .Where(text => text?.Contains("prior-turn-", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.True(
            followupPriorMarkers.SequenceEqual(initialPriorMarkers),
            $"Expected the follow-up to keep every prior turn: before {initialPriorMarkers.Length}, after {followupPriorMarkers.Length}.");
        Assert.True(
            ConciergeStayWorkflow.EstimatePromptTokens(followup.Messages)
            + ConciergeToolDefinitions.EstimateToolDefinitionTokens(includeEndChat: false)
            + ConciergeStayWorkflow.AnswerMaxOutputTokens
            + ConciergeStayWorkflow.ContextEstimationHeadroomTokens
            <= ConciergeStayWorkflow.ActionWorkingContextBudgetTokens);
    }

    [Fact]
    public async Task ModelSelectedRideProposalAndConfirmationUseStoredOfferWithoutPhraseMatching()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "proposal-1",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["pickup"] = null,
                ["dropoff"] = "South Beach",
                ["immediate"] = true,
                ["pickupAt"] = null,
            }));

        var proposalTurn = await SubmitAsync(
            host, handle, stayId, "proposal", "Could we get a car to South Beach?");
        Assert.True(host.ActionTools.Calls.Count > 0, proposalTurn.AssistantMessage);
        Assert.Equal(ConciergeToolDefinitions.ProposeRide, host.ActionTools.Calls[0].Name);
        Assert.Contains("No ride has been booked", proposalTurn.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("South Beach, Miami Beach", proposalTurn.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("complimentary for current hotel guests", proposalTurn.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("United States of America", proposalTurn.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("2217 NW Miami Court", proposalTurn.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("simulat", proposalTurn.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(GuidPattern, proposalTurn.AssistantMessage);
        Assert.Single(host.ChatClient.Requests);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-ride",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        var confirmed = await SubmitAsync(host, handle, stayId, "confirm-ride", "That's the ride I want.");

        Assert.Equal(
            [ConciergeToolDefinitions.ProposeRide, ConciergeToolDefinitions.ConfirmRide],
            host.ActionTools.Calls.Select(call => call.Name));
        Assert.Equal(ConciergeToolDefinitions.ConfirmRide, host.ActionTools.Calls[1].Name);
        Assert.Contains("South Beach, Miami Beach", confirmed.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("2217 NW Miami Court", confirmed.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("United States of America", confirmed.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("simulat", confirmed.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(GuidPattern, confirmed.AssistantMessage);
        // The model classifies the reply; the workflow supplies only its immediately pending offer.
        Assert.Equal(2, host.ChatClient.Requests.Count);
        Assert.Contains(
            host.ChatClient.Requests[0].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("no ride or cancellation offer awaits", StringComparison.Ordinal));
        Assert.Contains(
            host.ChatClient.Requests[1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains(
                    "you offered a car from Hotel ZGY to South Beach, Miami Beach picking up now in your previous reply",
                    StringComparison.Ordinal)
                && !System.Text.RegularExpressions.Regex.IsMatch(message.Text, GuidPattern));
        var trustedProposal = host.ActionTools.Calls[1].Arguments[
            ConciergeToolDefinitions.TrustedProposalKey]?.ToString();
        Assert.NotNull(trustedProposal);
        using var proposalDocument = JsonDocument.Parse(trustedProposal);
        Assert.Equal(
            720,
            proposalDocument.RootElement.GetProperty("routeDurationSeconds").GetDouble());
        Assert.Equal(
            proposalDocument.RootElement.GetProperty("proposalId").GetString(),
            host.ActionTools.Calls[1].Arguments["proposalId"]?.ToString());
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task InvalidScheduleRendersRecordedNonDefaultMinimumAndReplaysWithoutBooking()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.QueueResult(ConciergeToolDefinitions.ProposeRide,
            JsonSerializer.Serialize(new ConciergeRideProposalToolResult(
                "InvalidSchedule", "Provider detail must not reach the guest.", [],
                null, null, null, null, 12),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "scheduled-too-soon", ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["dropoff"] = "South Beach",
                ["immediate"] = false,
                ["pickupAt"] = "2026-10-24T18:05:00-04:00",
            }));

        var result = await SubmitAsync(host, handle, stayId, "scheduled-too-soon", "Schedule a ride.");

        Assert.Equal(
            "A scheduled pickup must be at least 12 minutes from now. Please choose a later time. No ride has been booked.",
            result.AssistantMessage);
        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(ConciergeToolDefinitions.ProposeRide, Assert.Single(host.ActionTools.Calls).Name);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task FailedRideProposalUsesCuratedReplyAndCannotBeConfirmed()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.QueueResult(ConciergeToolDefinitions.ProposeRide, "null");
        host.ChatClient.AnswerFactory = _ =>
            "I couldn't prepare the ride — did you mean Walgreens at 3103 Biscayne Boulevard in Miami, FL 33137?";
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "failed-ride-proposal",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["dropoff"] = "Walgreens, 3103 Biscayne Boulevard",
                ["immediate"] = true,
            }));

        var proposal = await SubmitAsync(
            host,
            handle,
            stayId,
            "failed-ride-proposal",
            "Book me a ride to Walgreens, 3103 Biscayne Boulevard.");

        Assert.Equal(
            "I couldn't prepare the ride, so no ride has been booked. Please try again in a moment.",
            proposal.AssistantMessage);
        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(ConciergeToolDefinitions.ProposeRide, Assert.Single(host.ActionTools.Calls).Name);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-without-offer",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        var confirmation = await SubmitAsync(
            host, handle, stayId, "confirm-without-offer", "yes, confirm");

        Assert.Contains("there's no ride offer waiting", confirmation.AssistantMessage, StringComparison.Ordinal);
        Assert.Single(host.ActionTools.Calls);
        Assert.Equal(2, host.ChatClient.Requests.Count);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ModelSelectedRideIntentIsNotRejectedByPhraseAuthorizationAndOnlyStagesOffer()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "contextual-ride-request",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?> { ["dropoff"] = "South Beach", ["immediate"] = true }));

        var result = await SubmitAsync(
            host,
            handle,
            stayId,
            "contextual-ride-request",
            "Could we get a car over to South Beach?");

        Assert.Equal(ConciergeToolDefinitions.ProposeRide, Assert.Single(host.ActionTools.Calls).Name);
        Assert.Contains("No ride has been booked", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(host.ActionTools.Calls, call =>
            call.Name is ConciergeToolDefinitions.ConfirmRide or ConciergeToolDefinitions.ConfirmRideCancellation);

        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ModelSelectedRideRequestsCreateOnlyProposals()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var acceptedRequests = new[]
        {
            "Can you Book me ride to South Beach please?",
            "Could you book a ride to South Beach?",
            "Please book a ride to South Beach.",
            "I'd like a ride to South Beach.",
            "I need a ride to South Beach.",
            "Get me a ride to South Beach.",
        };

        for (var index = 0; index < acceptedRequests.Length; index++)
        {
            host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
                $"polite-booking-{index}",
                ConciergeToolDefinitions.ProposeRide,
                new Dictionary<string, object?>
                {
                    ["dropoff"] = "South Beach",
                    ["immediate"] = true,
                }));

            var result = await SubmitAsync(
                host, handle, stayId, $"polite-booking-{index}", acceptedRequests[index]);

            Assert.Contains("No ride has been booked", result.AssistantMessage, StringComparison.Ordinal);
        }

        Assert.Equal(
            acceptedRequests.Length,
            host.ActionTools.Calls.Count(call => call.Name == ConciergeToolDefinitions.ProposeRide));
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task CancellationIsOfferedFirstAndOnlyConfirmedOnTheNextTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "polite-cancel",
            ConciergeToolDefinitions.ProposeRideCancellation,
            new Dictionary<string, object?> { ["rideId"] = Guid.NewGuid().ToString("D") }));

        var offer = await SubmitAsync(host, handle, stayId, "polite-cancel", "Could you get rid of my ride?");

        Assert.Equal(ConciergeToolDefinitions.GetRideStatus, Assert.Single(host.ActionTools.Calls).Name);
        Assert.Contains("Would you like me to cancel it?", offer.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-cancel",
            ConciergeToolDefinitions.ConfirmRideCancellation,
            new Dictionary<string, object?>()));
        var cancelled = await SubmitAsync(host, handle, stayId, "confirm-cancel", "Yes, that's the one.");

        Assert.Equal(
            [ConciergeToolDefinitions.GetRideStatus, ConciergeToolDefinitions.ConfirmRideCancellation],
            host.ActionTools.Calls.Select(call => call.Name));
        Assert.Contains("canceled", cancelled.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            host.ChatClient.Requests[^1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("you offered to cancel the ride to", StringComparison.Ordinal)
                && !System.Text.RegularExpressions.Regex.IsMatch(message.Text, GuidPattern));
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task CancellationConfirmationCannotUseAStaleOffer()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var guestId = Guid.NewGuid();
        var checkout = ConciergeTestHost.HotelToday.AddDays(1);
        host.ChatClient.AnswerFactory = _ => "I can help with that.";
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "stage-cancel",
            ConciergeToolDefinitions.ProposeRideCancellation,
            new Dictionary<string, object?> { ["rideId"] = Guid.NewGuid().ToString("D") }));
        await SubmitWithGuestAsync(
            host, handle, stayId, "stage-cancel", "Please cancel my ride.", "web",
            ConciergeTestHost.VerifiedGuest(stayId, guestId, checkout));
        await SubmitWithGuestAsync(
            host, handle, stayId, "unrelated-turn", "What is the weather?", "web",
            ConciergeTestHost.VerifiedGuest(stayId, guestId, checkout));
        // The offer survives one intervening turn; the second one lapses it.
        await SubmitWithGuestAsync(
            host, handle, stayId, "second-unrelated-turn", "What time is checkout?", "web",
            ConciergeTestHost.VerifiedGuest(stayId, guestId, checkout));

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "stale-confirm",
            ConciergeToolDefinitions.ConfirmRideCancellation,
            new Dictionary<string, object?>()));
        var result = await SubmitWithGuestAsync(
            host, handle, stayId, "stale-confirm", "Yes, cancel it.", "web",
            ConciergeTestHost.VerifiedGuest(stayId, guestId, checkout));

        Assert.DoesNotContain(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRideCancellation);
        Assert.Contains("No new cancellation was submitted", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("There's no ride cancellation offer", result.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task NonConfirmingModelRepliesDoNotBookAndTheOfferExpiresAfterTwoTurns()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "proposal-expire",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["pickup"] = null,
                ["dropoff"] = "South Beach",
                ["immediate"] = true,
                ["pickupAt"] = null,
            }));
        await SubmitAsync(host, handle, stayId, "proposal-expire", "Please book a ride to South Beach now.");

        host.ChatClient.AnswerFactory = _ => "I haven't booked the ride.";
        var agreement = await SubmitAsync(host, handle, stayId, "agreement", "Yeah. Let's do that.");
        await SubmitAsync(host, handle, stayId, "deferral", "Let me think about it.");

        Assert.Single(host.ActionTools.Calls);
        Assert.Equal(3, host.ChatClient.Requests.Count);
        Assert.Contains("haven't booked", agreement.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains(
            host.ChatClient.Requests[2].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("two replies ago", StringComparison.Ordinal));

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "late-confirm",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        var confirmed = await SubmitAsync(host, handle, stayId, "late-confirm", "Yes, please.");

        Assert.Single(host.ActionTools.Calls);
        Assert.StartsWith("No ride was booked", confirmed.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ModelClassifiedConfirmationBooksTheImmediatelyPendingOffer()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "proposal-natural",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["pickup"] = null,
                ["dropoff"] = "South Beach",
                ["immediate"] = true,
                ["pickupAt"] = null,
            }));
        await SubmitAsync(host, handle, stayId, "proposal-natural", "Can you book me a ride to South Beach?");

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-natural",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        var confirmed = await SubmitAsync(host, handle, stayId, "confirm-natural", "That's the one.");

        Assert.Equal(ConciergeToolDefinitions.ConfirmRide, host.ActionTools.Calls[^1].Name);
        Assert.Equal(2, host.ChatClient.Requests.Count);
        Assert.StartsWith("You're all set.", confirmed.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task AnswerToTheRideTimingQuestionCarriesTheGuestsBookingRequest()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);

        // Live voice 2026-10-21: the model asked "now or later?" and the follow-up was refused
        // because it lacked the word "ride"; the filler-led request also failed the anchored rule.
        host.ChatClient.AnswerFactory = _ => "Would you like the ride now or later?";
        await SubmitAsync(
            host, handle, stayId, "ask-timing", "Okay. Um, could you book me a ride to South Beach?");

        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "followup-proposal",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?> { ["dropoff"] = "South Beach", ["immediate"] = true }));
        var proposal = await SubmitAsync(host, handle, stayId, "answer-timing", "Book it now.");

        Assert.Equal(ConciergeToolDefinitions.ProposeRide, Assert.Single(host.ActionTools.Calls).Name);
        Assert.Contains("No ride has been booked", proposal.AssistantMessage, StringComparison.Ordinal);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-followup",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        var confirmed = await SubmitAsync(host, handle, stayId, "confirm-followup", "Yes, that's right.");

        Assert.Equal(ConciergeToolDefinitions.ConfirmRide, host.ActionTools.Calls[^1].Name);
        Assert.StartsWith("You're all set.", confirmed.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ModelSelectedFollowUpOnlyCreatesAProposalAndNeverBooks()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = _ => "Checkout is at 11 a.m.";
        await SubmitAsync(host, handle, stayId, "unrelated", "What time is checkout?");

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "unrequested-followup",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?> { ["dropoff"] = "South Beach", ["immediate"] = true }));
        host.ChatClient.AnswerFactory = _ => "Okay.";
        await SubmitAsync(host, handle, stayId, "right-now", "Right now, please.");

        Assert.Equal(ConciergeToolDefinitions.ProposeRide, Assert.Single(host.ActionTools.Calls).Name);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ModelConfirmationWithoutAnOpenOfferDoesNotBook()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "proposal-stale",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["pickup"] = null,
                ["dropoff"] = "South Beach",
                ["immediate"] = true,
                ["pickupAt"] = null,
            }));
        await SubmitAsync(host, handle, stayId, "proposal-stale", "Please book a ride to South Beach now.");
        host.ChatClient.AnswerFactory = _ => "Checkout is at 11 a.m.";
        await SubmitAsync(host, handle, stayId, "unrelated", "What time is checkout?");
        await SubmitAsync(host, handle, stayId, "unrelated-2", "And breakfast?");
        var answerCalls = host.ChatClient.Requests.Count;

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "late-confirm",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        var result = await SubmitAsync(host, handle, stayId, "late-confirm", "Confirm ride.");

        Assert.Single(host.ActionTools.Calls);
        Assert.Equal(answerCalls + 1, host.ChatClient.Requests.Count);
        Assert.StartsWith("No ride was booked", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("upcoming rides", result.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task RideOfferSurvivesOneInterveningTurnWithoutARideAction()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "proposal-wait",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?> { ["dropoff"] = "South Beach", ["immediate"] = true }));
        await SubmitAsync(host, handle, stayId, "proposal-wait", "Book me a ride to South Beach.");

        host.ChatClient.AnswerFactory = _ => "Take your time.";
        await SubmitAsync(host, handle, stayId, "moment", "I need a moment to think.");

        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "go-ahead",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        var confirmed = await SubmitAsync(
            host, handle, stayId, "go-ahead", "I'd like to go ahead with that ride.");

        Assert.Equal(
            [ConciergeToolDefinitions.ProposeRide, ConciergeToolDefinitions.ConfirmRide],
            host.ActionTools.Calls.Select(call => call.Name));
        Assert.StartsWith("You're all set.", confirmed.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains(
            host.ChatClient.Requests[1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains("in your previous reply", StringComparison.Ordinal));
        Assert.Contains(
            host.ChatClient.Requests[2].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains(
                    "you offered a car from Hotel ZGY to South Beach, Miami Beach picking up now two replies ago",
                    StringComparison.Ordinal));
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task BookedOfferIsClosedOnTheNextTurn()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "proposal-book",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?> { ["dropoff"] = "South Beach", ["immediate"] = true }));
        await SubmitAsync(host, handle, stayId, "proposal-book", "Book me a ride to South Beach.");
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "book-now",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        await SubmitAsync(host, handle, stayId, "book-now", "Book it for right now.");

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "new-ride",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["dropoff"] = "Wynwood",
                ["immediate"] = true,
            }));
        var newRide = await SubmitAsync(host, handle, stayId, "new-ride", "Please book me a new ride to Wynwood.");
        Assert.Contains("Wynwood", newRide.AssistantMessage, StringComparison.Ordinal);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-new-ride",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        await SubmitAsync(host, handle, stayId, "confirm-new-ride", "Book that ride.");

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "repeat-closed-ride",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["dropoff"] = "Wynwood",
                ["immediate"] = true,
            }));
        var again = await SubmitAsync(host, handle, stayId, "thanks", "Yes, please.");

        Assert.Equal(
            [
                ConciergeToolDefinitions.ProposeRide,
                ConciergeToolDefinitions.ConfirmRide,
                ConciergeToolDefinitions.ProposeRide,
                ConciergeToolDefinitions.ConfirmRide,
            ],
            host.ActionTools.Calls.Select(call => call.Name));
        Assert.Equal(
            "That ride offer is already closed, so I haven't prepared it again. If you'd like a new ride, please tell me the destination, pickup, or time.",
            again.AssistantMessage);
        Assert.Contains(
            host.ChatClient.Requests[^1].Messages,
            message => message.Role == ChatRole.System
                && message.Text.Contains(
                    "booked a car from Hotel ZGY to Wynwood, Miami Beach picking up now; closed. Don't repeat. New explicit ride (same route too): ProposeRide. No offer: no ConfirmRide/ConfirmRideCancellation.",
                    StringComparison.Ordinal));
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ExplicitSameRideRequestAfterBookingCreatesAndConfirmsNewProposal()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "initial-south-beach-proposal",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["pickup"] = null,
                ["dropoff"] = "South Beach",
                ["immediate"] = true,
                ["pickupAt"] = null,
            }));
        await SubmitAsync(
            host, handle, stayId, "initial-south-beach-proposal", "Please book me a ride to South Beach.");

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-initial-south-beach-ride",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        await SubmitAsync(
            host, handle, stayId, "confirm-initial-south-beach-ride", "Book it for right now.");

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "new-south-beach-proposal",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["pickup"] = null,
                ["dropoff"] = "South Beach",
                ["immediate"] = true,
                ["pickupAt"] = null,
            }));
        var newProposal = await SubmitAsync(
            host,
            handle,
            stayId,
            "new-south-beach-proposal",
            "Okay. Um, could you book me a ride to South Beach?");
        Assert.Contains("No ride has been booked", newProposal.AssistantMessage, StringComparison.Ordinal);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-new-south-beach-ride",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        var confirmed = await SubmitAsync(
            host, handle, stayId, "confirm-new-south-beach-ride", "Book it for right now.");

        Assert.Equal(
            [
                ConciergeToolDefinitions.ProposeRide,
                ConciergeToolDefinitions.ConfirmRide,
                ConciergeToolDefinitions.ProposeRide,
                ConciergeToolDefinitions.ConfirmRide,
            ],
            host.ActionTools.Calls.Select(call => call.Name));
        Assert.StartsWith("You're all set.", confirmed.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task CancellationOfferSurvivesOneInterveningTurnAndReplacesARideOffer()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "proposal-replaced",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?> { ["dropoff"] = "South Beach", ["immediate"] = true }));
        await SubmitAsync(host, handle, stayId, "proposal-replaced", "Book me a ride to South Beach.");
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "stage-cancel-wait",
            ConciergeToolDefinitions.ProposeRideCancellation,
            new Dictionary<string, object?> { ["rideId"] = Guid.NewGuid().ToString("D") }));
        await SubmitAsync(host, handle, stayId, "stage-cancel-wait", "Actually, cancel my existing ride.");

        // The newer cancellation offer replaced the ride offer.
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "stale-ride-confirm",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?>()));
        var refused = await SubmitAsync(host, handle, stayId, "stale-ride-confirm", "Book that ride.");
        Assert.StartsWith("No ride was booked", refused.AssistantMessage, StringComparison.Ordinal);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-cancel-wait",
            ConciergeToolDefinitions.ConfirmRideCancellation,
            new Dictionary<string, object?>()));
        var cancelled = await SubmitAsync(host, handle, stayId, "confirm-cancel-wait", "Okay, cancel it.");

        Assert.Equal(
            [
                ConciergeToolDefinitions.ProposeRide,
                ConciergeToolDefinitions.GetRideStatus,
                ConciergeToolDefinitions.ConfirmRideCancellation,
            ],
            host.ActionTools.Calls.Select(call => call.Name));
        Assert.Contains("canceled", cancelled.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task TwoDayWeatherRunsBothForecastsInOneRoundAndReplays()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "weather-today",
                    ConciergeToolDefinitions.GetWeather,
                    new Dictionary<string, object?> { ["period"] = "today" }),
                new FunctionCallContent(
                    "weather-tomorrow",
                    ConciergeToolDefinitions.GetWeather,
                    new Dictionary<string, object?> { ["period"] = "tomorrow" }),
            ]))
        {
            FinishReason = ChatFinishReason.ToolCalls,
        });

        var result = await SubmitAsync(
            host, handle, stayId, "two-days", "I wanna know the weather for the next two days.", "voice");

        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(
            ["today", "tomorrow"],
            host.ActionTools.Calls.Select(call => call.Arguments["period"]?.ToString()).Order());
        Assert.Contains("Expect partly cloudy today at Hotel ZGY", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("Expect partly cloudy tomorrow at Hotel ZGY", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("couldn't safely", result.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task SingleWeatherCallForTheNextTwoDaysFetchesBothForecastsAndReplays()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "weather-range", ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["period"] = "today" }));

        var result = await SubmitAsync(
            host, handle, stayId, "two-days-range", "What's the weather for the next two days?");

        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(
            ["today", "tomorrow"],
            host.ActionTools.Calls.Select(call => call.Arguments["period"]?.ToString()).Order());
        Assert.Contains("Expect partly cloudy today at Hotel ZGY", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("Expect partly cloudy tomorrow at Hotel ZGY", result.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task NextThreeDaysWeatherFetchesThreeConsecutiveDaysInOneRoundAndReplays()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        // Recorded live: the model answered "next 3 days" with today, tonight and tomorrow, which
        // the workflow previously refused as an unsafe multi-call.
        host.ChatClient.ScriptedResponses.Enqueue(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            new[] { "today", "tonight", "tomorrow" }
                .Select((period, index) => (AIContent)new FunctionCallContent(
                    $"call_{index}",
                    ConciergeToolDefinitions.GetWeather,
                    new Dictionary<string, object?> { ["place"] = "Plantation", ["period"] = period }))
                .ToList()))
        {
            FinishReason = ChatFinishReason.ToolCalls,
        });

        var result = await SubmitAsync(
            host, handle, stayId, "three-days", "what's the weather for the next 3 days in plantation?", "voice");

        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(
            ["day+2", "today", "tomorrow"],
            host.ActionTools.Calls.Select(call => call.Arguments["period"]?.ToString()).Order());
        Assert.Contains("Expect partly cloudy today in Plantation", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("Expect partly cloudy tomorrow in Plantation", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("Expect partly cloudy on Tuesday in Plantation", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("couldn't safely", result.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task NextThreeDaysStartingTomorrowKeepsTheGuestsStartDayAndReplays()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            new[] { "tomorrow" }
                .Select((period, index) => (AIContent)new FunctionCallContent(
                    $"call_{index}",
                    ConciergeToolDefinitions.GetWeather,
                    new Dictionary<string, object?> { ["period"] = period }))
                .ToList()))
        {
            FinishReason = ChatFinishReason.ToolCalls,
        });

        var result = await SubmitAsync(
            host, handle, stayId, "three-days-tomorrow", "What's the weather for the next three days starting tomorrow?", "web");

        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(
            ["day+2", "day+3", "tomorrow"],
            host.ActionTools.Calls.Select(call => call.Arguments["period"]?.ToString()).Order());
        Assert.DoesNotContain(" today ", result.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task NextThreeDaysStartingAWeekdayFetchesFromThatWeekdayAndReplays()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetWeather,
            JsonSerializer.Serialize(
                new ConciergeWeatherToolResult(
                    "BeyondForecast", null, [], null, new DateOnly(2026, 10, 24), null, null, null, null, null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "weather-friday",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["period"] = "today" }));

        var result = await SubmitAsync(
            host, handle, stayId, "three-days-friday", "What's the weather for the next three days starting Friday?", "web");

        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(
            ["friday+0", "friday+1", "friday+2"],
            host.ActionTools.Calls.Select(call => call.Arguments["period"]?.ToString()).Order());
        Assert.Contains("The forecast for Saturday, Oct 24 isn't available yet", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("Expect partly cloudy on Tuesday at Hotel ZGY", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(" today ", result.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ThisWeekendFetchesSaturdayAndSundayAgainstOneTurnDateAndReplays()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        QueueWeather(host, new DateOnly(2026, 10, 24));
        QueueWeather(host, new DateOnly(2026, 10, 25));
        // Live 2026-10-22: capitalized weekday names the validator rejected.
        host.ChatClient.ScriptedResponses.Enqueue(ToolCalls(
            ("weekend-sat", ConciergeToolDefinitions.GetWeather, new Dictionary<string, object?> { ["period"] = "Saturday" }),
            ("weekend-sun", ConciergeToolDefinitions.GetWeather, new Dictionary<string, object?> { ["period"] = "Sunday" })));

        var result = await SubmitAsync(host, handle, stayId, "weekend", "What's the forecast for this weekend?", "web");

        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(
            ["weekend+0", "weekend+1"],
            host.ActionTools.Calls.Select(call => call.Arguments["period"]?.ToString()).Order());
        var anchors = host.ActionTools.WeatherAnchors.ToArray();
        Assert.Equal(2, anchors.Length);
        Assert.NotNull(anchors[0]);
        Assert.Equal(anchors[0], anchors[1]);
        Assert.Contains("on Saturday at Hotel ZGY", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("on Sunday at Hotel ZGY", result.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task WeekendQuestionAnsweredWithoutAToolCallIsFetchedByTheWorkflowAndReplays()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        QueueWeather(host, new DateOnly(2026, 10, 24));
        QueueWeather(host, new DateOnly(2026, 10, 25));
        // Live 2026-10-22: the model repeated an earlier Saturday answer instead of calling GetWeather.
        host.ChatClient.AnswerFactory = _ => "As I mentioned, Saturday looks sunny.";

        var result = await SubmitAsync(host, handle, stayId, "weekend-stale", "What's the forecast for this weekend?", "web");

        Assert.Single(host.ChatClient.Requests);
        Assert.Equal(
            ["weekend+0", "weekend+1"],
            host.ActionTools.Calls.Select(call => call.Arguments["period"]?.ToString()).Order());
        Assert.DoesNotContain("As I mentioned", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("on Saturday at Hotel ZGY", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("on Sunday at Hotel ZGY", result.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task WeekendQuestionForANamedPlaceWithoutAToolCallKeepsTheModelsAnswer()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = _ => "Which Key West location do you mean?";

        var result = await SubmitAsync(host, handle, stayId, "weekend-place", "What's the weather in Key West this weekend?", "web");

        Assert.Empty(host.ActionTools.Calls);
        Assert.Equal("Which Key West location do you mean?", result.AssistantMessage);
    }

    [Fact]
    public async Task SundayWeekendQuestionSaysSaturdayHasPassed()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetWeather,
            JsonSerializer.Serialize(
                new ConciergeWeatherToolResult("Elapsed", null, [], null, new DateOnly(2026, 10, 24), null, null, null, null, null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        QueueWeather(host, new DateOnly(2026, 10, 25));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "weekend", ConciergeToolDefinitions.GetWeather, new Dictionary<string, object?> { ["period"] = "today" }));

        var result = await SubmitAsync(host, handle, stayId, "sunday-weekend", "What's the weather this weekend?", "web");

        Assert.Single(host.ActionTools.Calls, call => call.Arguments["period"]?.ToString() == "weekend+0");
        Assert.Contains("Saturday has passed; today is the remaining weekend day.", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("on Sunday at Hotel ZGY", result.AssistantMessage, StringComparison.Ordinal);
        await ReplayAsync(handle);
    }

    private static void QueueWeather(ConciergeTestHost host, DateOnly date) =>
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetWeather,
            JsonSerializer.Serialize(
                new ConciergeWeatherToolResult(
                    "Available", null, [], "Hotel ZGY, Miami", date, "partly cloudy", 82, 70, 8, 10),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Fact]
    public async Task RideActionsUseVerifiedIdentityAndKeepGuestFacingResultsHuman()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var guestId = Guid.NewGuid();
        var checkout = ConciergeTestHost.HotelToday.AddDays(1);
        var rideId = Guid.NewGuid();
        host.ChatClient.AnswerFactory = _ => "Here are your upcoming rides.";

        var status = await SubmitWithGuestAsync(
            host, handle, stayId, "status", "Where is my ride?", "web",
            ConciergeTestHost.VerifiedGuest(stayId, guestId, checkout),
            ToolCall("status-1", ConciergeToolDefinitions.GetRideStatus,
                new Dictionary<string, object?> { ["rideId"] = rideId.ToString("D") }));
        var historyRide = new RideSummary(
            rideId, "Booked", "Hotel ZGY, 2217 NW Miami Court, Miami, Florida",
            "South Beach, Miami Beach, Florida, United States of America",
            DateTimeOffset.Parse("2026-10-21T10:00:00-04:00"), null, null, null);
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetRideHistory,
            JsonSerializer.Serialize(
                new ConciergeRideActionResult(true, null, null, [historyRide]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var history = await SubmitWithGuestAsync(
            host, handle, stayId, "history", "Show upcoming rides.", "web",
            ConciergeTestHost.VerifiedGuest(stayId, guestId, checkout),
            ToolCall("history-1", ConciergeToolDefinitions.GetRideHistory,
                new Dictionary<string, object?> { ["when"] = "upcoming" }));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "stage-cancel",
            ConciergeToolDefinitions.ProposeRideCancellation,
            new Dictionary<string, object?> { ["rideId"] = rideId.ToString("D") }));
        var cancellationOffer = await SubmitWithGuestAsync(
            host, handle, stayId, "cancel", $"Please cancel ride {rideId:D}.", "web",
            ConciergeTestHost.VerifiedGuest(stayId, guestId, checkout));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-cancel",
            ConciergeToolDefinitions.ConfirmRideCancellation,
            new Dictionary<string, object?>()));
        var cancellation = await SubmitWithGuestAsync(
            host, handle, stayId, "confirm-cancel", "Yes, cancel that one.", "web",
            ConciergeTestHost.VerifiedGuest(stayId, guestId, checkout));

        Assert.Equal(
            [
                ConciergeToolDefinitions.GetRideStatus,
                ConciergeToolDefinitions.GetRideHistory,
                ConciergeToolDefinitions.GetRideStatus,
                ConciergeToolDefinitions.ConfirmRideCancellation,
            ],
            host.ActionTools.Calls.Select(call => call.Name));
        Assert.Contains("has a driver assigned", status.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("United States of America", status.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotMatch(GuidPattern, status.AssistantMessage);
        Assert.Contains("South Beach, Miami Beach", history.AssistantMessage, StringComparison.Ordinal);
        Assert.Contains("10:00 AM", history.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("2217 NW Miami Court", history.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("United States of America", history.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotMatch(GuidPattern, history.AssistantMessage);
        Assert.Contains("Would you like me to cancel it?", cancellationOffer.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("canceled", cancellationOffer.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Your ride is canceled.", cancellation.AssistantMessage, StringComparison.Ordinal);
        Assert.Equal(4, host.ChatClient.Requests.Count);
        Assert.All(host.ActionTools.Calls, call =>
        {
            Assert.Equal(guestId.ToString("D"), call.GuestId);
            Assert.Equal(stayId.ToString("D"), call.StayId);
            Assert.Equal(checkout.ToString("yyyy-MM-dd"), call.CheckoutDate);
        });
        Assert.All(host.ChatClient.Requests, request =>
            Assert.DoesNotContain("simulat", string.Join(" ", request.Messages.Select(message => message.Text)),
                StringComparison.OrdinalIgnoreCase));
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task CancellationUsesHistoryThenStagesAndConfirmsAWorkflowOwnedRide()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        var guestId = Guid.NewGuid();
        var checkout = ConciergeTestHost.HotelToday.AddDays(1);
        var rideId = Guid.NewGuid();
        var ride = new RideSummary(
            rideId, "Booked", "Hotel ZGY, 2217 NW Miami Court, Miami, Florida",
            "South Beach, Miami Beach, Florida, United States of America",
            DateTimeOffset.Parse("2026-10-21T10:00:00-04:00"), null, null, null);
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetRideHistory,
            JsonSerializer.Serialize(
                new ConciergeRideActionResult(true, null, null, [ride]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "history-before-cancel",
            ConciergeToolDefinitions.GetRideHistory,
            new Dictionary<string, object?> { ["when"] = "upcoming", ["purpose"] = "select" }));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "propose-cancel-after-history",
            ConciergeToolDefinitions.ProposeRideCancellation,
            new Dictionary<string, object?> { ["rideId"] = rideId.ToString("D") }));

        var offer = await handle.ExecuteUpdateAsync(workflow => workflow.SubmitTurnAsync(
            new ConciergeTurnSubmission(
                "cancel-after-history",
                "Cancel my upcoming ride, please.",
                "web",
                ConciergeTestHost.VerifiedGuest(stayId, guestId, checkout))));

        Assert.Equal(
            [ConciergeToolDefinitions.GetRideHistory, ConciergeToolDefinitions.GetRideStatus],
            host.ActionTools.Calls.Select(call => call.Name));
        Assert.Contains("Would you like me to cancel it?", offer.AssistantMessage, StringComparison.Ordinal);
        var modelAfterHistory = host.ChatClient.Requests[1];
        var historyResult = Assert.Single(modelAfterHistory.Messages.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>());
        Assert.Contains(rideId.ToString("D"), historyResult.Result?.ToString(), StringComparison.Ordinal);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "confirm-cancel-after-history",
            ConciergeToolDefinitions.ConfirmRideCancellation,
            new Dictionary<string, object?>()));
        var result = await handle.ExecuteUpdateAsync(workflow => workflow.SubmitTurnAsync(
            new ConciergeTurnSubmission(
                "confirm-cancel-after-history",
                "Yes, cancel that ride.",
                "web",
                ConciergeTestHost.VerifiedGuest(stayId, guestId, checkout))));

        Assert.Equal(
            [
                ConciergeToolDefinitions.GetRideHistory,
                ConciergeToolDefinitions.GetRideStatus,
                ConciergeToolDefinitions.ConfirmRideCancellation,
            ],
            host.ActionTools.Calls.Select(call => call.Name));
        Assert.Contains("Your ride is canceled.", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotMatch(GuidPattern, result.AssistantMessage);
        Assert.Equal(3, host.ChatClient.Requests.Count);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ModelCannotOverrideTrustedRideScopeWithArguments()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "scope-override",
            ConciergeToolDefinitions.GetRideStatus,
            new Dictionary<string, object?>
            {
                ["rideId"] = Guid.NewGuid().ToString("D"),
                ["guestId"] = Guid.NewGuid().ToString("D"),
            }));

        var result = await SubmitAsync(host, handle, stayId, "scope-override", "Check my ride.");

        Assert.Contains("safely identify", result.AssistantMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(host.ActionTools.Calls);
    }

    [Fact]
    public async Task ConfirmationBooksTheStoredProposalNotModelSuppliedIds()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "proposal-2",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["pickup"] = null,
                ["dropoff"] = "South Beach",
                ["immediate"] = true,
                ["pickupAt"] = null,
            }));
        await SubmitAsync(
            host, handle, stayId, "proposal-2", "Please book a ride to South Beach now.");
        var wrongProposalId = Guid.NewGuid();
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "wrong-proposal",
            ConciergeToolDefinitions.ConfirmRide,
            new Dictionary<string, object?> { ["proposalId"] = wrongProposalId.ToString("D") }));
        var answerCalls = host.ChatClient.Requests.Count;

        var result = await SubmitAsync(host, handle, stayId, "wrong-confirm", "confirm ride");

        Assert.Equal(answerCalls + 1, host.ChatClient.Requests.Count);
        Assert.Single(host.ActionTools.Calls);
        Assert.Equal("No ride was booked. A ride confirmation must not include arguments.", result.AssistantMessage);
        Assert.NotEqual(ConciergeToolDefinitions.ConfirmRide, host.ActionTools.Calls[^1].Name);
        Assert.NotEqual(wrongProposalId.ToString("D"), result.AssistantMessage);

        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "current-confirm", ConciergeToolDefinitions.ConfirmRide, new Dictionary<string, object?>()));
        await SubmitAsync(host, handle, stayId, "current-confirm", "Yes, book the ride you offered.");
        var booking = Assert.Single(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);
        Assert.NotEqual(wrongProposalId.ToString("D"), booking.Arguments["proposalId"]?.ToString());
    }

    [Fact]
    public async Task FinalAnswerKeepsOnlyTheFirstAssistantMessageAndReplays()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetWeather,
            JsonSerializer.Serialize(
                new ConciergeWeatherToolResult(
                    "NeedsClarification", "Miami", ["Miami, Florida"], null, null, null, null, null, null, null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetWeather,
            JsonSerializer.Serialize(
                new ConciergeWeatherToolResult(
                    "NeedsClarification", "Miami", ["Miami, Florida"], null, null, null, null, null, null, null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "weather-split",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["place"] = "Miami", ["period"] = "today" }));
        // Shape observed live from gpt-5-mini: the real answer, then a repeated capabilities greeting.
        host.ChatClient.ScriptedResponses.Enqueue(new ChatResponse(
            [
                new ChatMessage(ChatRole.Assistant, "It is partly cloudy today.") { MessageId = "msg_answer" },
                new ChatMessage(ChatRole.Assistant, "Hello — I can help with hotel questions.") { MessageId = "msg_greeting" },
            ])
        {
            FinishReason = ChatFinishReason.Stop,
        });

        var result = await SubmitAsync(host, handle, stayId, "split", "Show my upcoming rides.");

        Assert.Equal("It is partly cloudy today.", result.AssistantMessage);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ModelSelectedProposalAfterRouteToolStillCannotBookWithoutConfirmation()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ActionTools.QueueResult(
            ConciergeToolDefinitions.GetRoute,
            JsonSerializer.Serialize(
                new ConciergeRouteToolResult(
                    "NeedsClarification", "South Beach", [], null, null, null, null, [], [], null, null),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "route-1",
            ConciergeToolDefinitions.GetRoute,
            new Dictionary<string, object?> { ["places"] = new[] { "South Beach" } }));
        // Shape observed live: a correct route answer plus an unsolicited ProposeRide call.
        host.ChatClient.ScriptedResponses.Enqueue(new ChatResponse(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new TextContent("It is about 5 miles."),
                        new FunctionCallContent(
                            "unrequested-proposal",
                            ConciergeToolDefinitions.ProposeRide,
                            new Dictionary<string, object?> { ["dropoff"] = "South Beach", ["immediate"] = true }),
                    ]),
            ])
        {
            FinishReason = ChatFinishReason.ToolCalls,
        });
        // Live, a third model call added a redundant GetWeather call and hit the per-turn cap.
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "redundant-weather",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["period"] = "today" }));

        var result = await SubmitAsync(host, handle, stayId, "route-only", "How long is the drive to South Beach?");

        Assert.Contains("No ride has been booked", result.AssistantMessage, StringComparison.Ordinal);
        Assert.Equal(
            [ConciergeToolDefinitions.GetRoute, ConciergeToolDefinitions.ProposeRide],
            host.ActionTools.Calls.Select(call => call.Name));
        Assert.DoesNotContain(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);
        Assert.Single(host.ChatClient.ScriptedResponses);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ModelSelectedRideProposalCreatesNoBookingEvenWhenItAlsoReturnsText()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.ScriptedResponses.Enqueue(new ChatResponse(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new TextContent("It is about 15 miles."),
                        new FunctionCallContent(
                            "unrequested-proposal",
                            ConciergeToolDefinitions.ProposeRide,
                            new Dictionary<string, object?> { ["dropoff"] = "South Beach", ["immediate"] = true }),
                    ]),
            ])
        {
            FinishReason = ChatFinishReason.ToolCalls,
        });
        var result = await SubmitAsync(host, handle, stayId, "route-only", "How long is the drive to South Beach?");

        Assert.Single(host.ActionTools.Calls);
        Assert.Equal(ConciergeToolDefinitions.ProposeRide, host.ActionTools.Calls[0].Name);
        Assert.Contains("No ride has been booked", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(host.ActionTools.Calls, call => call.Name == ConciergeToolDefinitions.ConfirmRide);
        await ReplayAsync(handle);
    }

    [Fact]
    public async Task ScheduledProposalStatesPickupInTheRequestedLocalOffset()
    {
        await using var host = await ConciergeTestHost.StartAsync(postgreSql);
        var (stayId, handle) = await StartStayAsync(host);
        host.ChatClient.AnswerFactory = LastToolResult;
        host.ChatClient.ScriptedResponses.Enqueue(ToolCall(
            "scheduled-proposal",
            ConciergeToolDefinitions.ProposeRide,
            new Dictionary<string, object?>
            {
                ["dropoff"] = "South Beach",
                ["immediate"] = false,
                ["pickupAt"] = "2026-10-21T10:00:00-04:00",
            }));

        var result = await SubmitAsync(
            host, handle, stayId, "scheduled", "Please book me a ride to South Beach tomorrow at 10 am.");

        Assert.Contains("on Wednesday, October 21 at 10:00 AM hotel time", result.AssistantMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("+00:00", result.AssistantMessage, StringComparison.Ordinal);
    }

    private static async Task<(Guid StayId, WorkflowHandle<ConciergeStayWorkflow> Handle)> StartStayAsync(
        ConciergeTestHost host)
    {
        var stayId = await SeedStayAsync(host);
        var handle = await host.StartStayAsync(
            stayId,
            ConciergeTestHost.HotelMidnightUtc(ConciergeTestHost.HotelToday.AddDays(2)));
        return (stayId, handle);
    }

    private static Task<Guid> SeedStayAsync(ConciergeTestHost host) =>
        host.SeedStayAsync(
            "Action",
            "202",
            ConciergeTestHost.HotelToday,
            ConciergeTestHost.HotelToday.AddDays(1));

    private static Task<ConciergeTurnResult> SubmitAsync(
        ConciergeTestHost host,
        WorkflowHandle<ConciergeStayWorkflow> handle,
        Guid stayId,
        string turnId,
        string message,
        string channel = "web") =>
        handle.ExecuteUpdateAsync(workflow => workflow.SubmitTurnAsync(
            Turn(stayId, turnId, message, channel)));

    private static async Task<ConciergeTurnResult> SubmitWithGuestAsync(
        ConciergeTestHost host,
        WorkflowHandle<ConciergeStayWorkflow> handle,
        Guid stayId,
        string turnId,
        string message,
        string channel,
        ConciergeVerifiedGuest guest,
        ChatResponse? response = null)
    {
        if (response is not null)
        {
            host.ChatClient.ScriptedResponses.Enqueue(response);
        }
        return await handle.ExecuteUpdateAsync(workflow => workflow.SubmitTurnAsync(
            new ConciergeTurnSubmission(turnId, message, channel, guest)));
    }

    private static ConciergeTurnSubmission Turn(Guid stayId, string turnId, string message, string channel) =>
        new(turnId, message, channel, ConciergeTestHost.VerifiedGuest(stayId));

    private static ChatResponse ToolCall(
        string callId,
        string name,
        IDictionary<string, object?> arguments) =>
        ToolCalls((callId, name, arguments));

    private static ChatResponse ToolCalls(params (string CallId, string Name, IDictionary<string, object?> Arguments)[] calls) =>
        new(
            [
                new ChatMessage(
                    ChatRole.Assistant,
                    calls.Select(call => new FunctionCallContent(
                        call.CallId, call.Name, call.Arguments)).ToArray()),
            ])
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };

    private static string LastToolResult(IReadOnlyList<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents)
            .OfType<FunctionResultContent>()
            .LastOrDefault()
            ?.Result?.ToString() ?? "No tool result.";

    private const string GuidPattern = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    private static async Task ReplayAsync(WorkflowHandle<ConciergeStayWorkflow> handle)
        => await ReplayAsync(await handle.FetchHistoryAsync());

    private static async Task ReplayAsync(Temporalio.Common.WorkflowHistory history)
    {
        var replayer = new Temporalio.Worker.WorkflowReplayer(new Temporalio.Worker.WorkflowReplayerOptions
        {
            DataConverter = TemporalCommunity.Extensions.AI.DurableAIDataConverter.Instance,
        }.AddWorkflow<ConciergeStayWorkflow>());
        await replayer.ReplayWorkflowAsync(history);
    }

}
