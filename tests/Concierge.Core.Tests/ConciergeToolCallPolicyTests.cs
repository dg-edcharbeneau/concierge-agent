using Concierge.WorkflowServer.AI;

namespace Concierge.Core.Tests;

public sealed class ConciergeToolCallPolicyTests
{
    [Fact]
    public void WeatherValidationRejectsUnexpectedArguments()
    {
        var valid = ConciergeToolCallPolicy.TryValidateArguments(
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["place"] = "Miami", ["unexpected"] = true },
            out var error);

        Assert.False(valid);
        Assert.Equal(
            "I couldn't safely read the requested weather period or place. Please clarify.",
            error);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("show", true)]
    [InlineData("select", true)]
    [InlineData("cancel", false)]
    public void RideHistoryPurposeIsOptionalAndBounded(string? purpose, bool expected)
    {
        var arguments = new Dictionary<string, object?> { ["when"] = "upcoming" };
        if (purpose is not null)
        {
            arguments["purpose"] = purpose;
        }

        var valid = ConciergeToolCallPolicy.TryValidateArguments(
            ConciergeToolDefinitions.GetRideHistory,
            arguments,
            out _);

        Assert.Equal(expected, valid);
    }

    [Theory]
    [InlineData("What's the weather for the next two days?", "today", 2)]
    [InlineData("Weather for the next 2 days in Fort Lauderdale", "tomorrow", 2)]
    [InlineData("Forecast for today and tomorrow, please.", null, 2)]
    [InlineData("What's the weather for the next couple of days?", "today", 2)]
    [InlineData("What's the weather for the next two days?", "tonight", 2)]
    [InlineData("what's the weather for the next 3 days in plantation?", "today", 3)]
    [InlineData("Give me the 5-day forecast", "today", 5)]
    [InlineData("Weather for the next few days?", null, 3)]
    [InlineData("What about the coming seven days?", "today", 7)]
    [InlineData("What's the weather tomorrow?", "tomorrow", 0)]
    [InlineData("Will it rain today?", "today", 0)]
    [InlineData("Weather for the next 10 days", "today", 0)]
    public void MultiDayWeatherRequestsAreRecognizedFromTheGuestsWords(string message, string? period, int expectedDays)
    {
        var call = new Microsoft.Extensions.AI.FunctionCallContent(
            "weather-1",
            ConciergeToolDefinitions.GetWeather,
            new Dictionary<string, object?> { ["period"] = period, ["place"] = "Fort Lauderdale" });

        var recognized = ConciergeToolCallPolicy.TryGetMultiDayWeather([call], message, out var plan);

        Assert.Equal(expectedDays > 0, recognized);
        if (recognized)
        {
            Assert.Equal(0, plan.StartOffset);
            Assert.Null(plan.StartWeekday);
            Assert.Equal(expectedDays, plan.Days);
            var expanded = ConciergeToolCallPolicy.ExpandToMultiDayWeather([call], plan);
            Assert.Equal(
                new[] { "today", "tomorrow", "day+2", "day+3", "day+4", "day+5", "day+6" }.Take(expectedDays),
                expanded.Select(item => item.Arguments!["period"]));
            Assert.All(expanded, item => Assert.Equal("Fort Lauderdale", item.Arguments!["place"]));
            Assert.All(expanded, item => Assert.True(ConciergeToolCallPolicy.IsSafeFunctionCallId(item.CallId)));
            Assert.All(expanded, item => Assert.True(ConciergeToolCallPolicy.TryValidateArguments(
                item.Name, item.Arguments!, out _)));
            Assert.Equal(expectedDays, expanded.Select(item => item.CallId).Distinct().Count());
        }
    }

    [Fact]
    public void ModelsTodayTonightTomorrowForThreeDaysIsRewrittenToThreeConsecutiveDays()
    {
        // Recorded live: the model answered "next 3 days" with today, tonight and tomorrow.
        var calls = new[] { "today", "tonight", "tomorrow" }
            .Select((period, index) => new Microsoft.Extensions.AI.FunctionCallContent(
                $"call_{index}",
                ConciergeToolDefinitions.GetWeather,
                new Dictionary<string, object?> { ["place"] = "Plantation", ["period"] = period }))
            .ToArray();

        Assert.True(ConciergeToolCallPolicy.TryGetMultiDayWeather(
            calls, "what's the weather for the next 3 days in plantation?", out var plan));
        Assert.Equal(
            ["today", "tomorrow", "day+2"],
            ConciergeToolCallPolicy.ExpandToMultiDayWeather(calls, plan).Select(item => item.Arguments!["period"]));
    }

    [Theory]
    [InlineData("What's the weather for the next three days starting tomorrow?", new[] { "tomorrow", "day+2", "day+3" })]
    [InlineData("Forecast for the next 3 days from tomorrow", new[] { "tomorrow", "day+2", "day+3" })]
    [InlineData("Starting tomorrow, what's the weather for the next two days?", new[] { "tomorrow", "day+2" })]
    [InlineData("Give me the 3-day forecast beginning tomorrow", new[] { "tomorrow", "day+2", "day+3" })]
    [InlineData("Weather for the next three days starting today", new[] { "today", "tomorrow", "day+2" })]
    // The forecast reaches day+6, so a week starting tomorrow is clamped to six days.
    [InlineData("Weather for the next seven days starting tomorrow", new[] { "tomorrow", "day+2", "day+3", "day+4", "day+5", "day+6" })]
    // Weekday starts are resolved by the activity against the hotel calendar.
    [InlineData("What's the weather for the next three days starting Friday?", new[] { "friday+0", "friday+1", "friday+2" })]
    [InlineData("Forecast for the next 2 days from this Saturday", new[] { "saturday+0", "saturday+1" })]
    [InlineData("Starting on Monday, give me the 3-day forecast", new[] { "monday+0", "monday+1", "monday+2" })]
    [InlineData("Weather for the next seven days beginning Sunday", new[] { "sunday+0", "sunday+1", "sunday+2", "sunday+3", "sunday+4", "sunday+5", "sunday+6" })]
    public void MultiDayWeatherKeepsAnExplicitStartDay(string message, string[] expectedPeriods)
    {
        var calls = new[] { "today", "tomorrow" }
            .Select((period, index) => new Microsoft.Extensions.AI.FunctionCallContent(
                $"call_{index}",
                ConciergeToolDefinitions.GetWeather,
                new Dictionary<string, object?> { ["period"] = period }))
            .ToArray();

        Assert.True(ConciergeToolCallPolicy.TryGetMultiDayWeather(calls, message, out var plan));
        var expanded = ConciergeToolCallPolicy.ExpandToMultiDayWeather(calls, plan);

        Assert.Equal(expectedPeriods, expanded.Select(item => item.Arguments!["period"]));
        Assert.InRange(expanded.Length, 1, ConciergeToolCallPolicy.MaxParallelWeatherCalls);
        Assert.All(expanded, item => Assert.True(ConciergeToolCallPolicy.TryValidateArguments(
            item.Name, item.Arguments!, out _)));
        Assert.Equal(expanded.Length, expanded.Select(item => item.CallId).Distinct().Count());
    }

    [Theory]
    [InlineData("What's the forecast for this weekend?")]
    [InlineData("Will it rain on the weekend?")]
    [InlineData("Weekend weather in Miami Beach?")]
    public void ThisWeekendRewritesTheModelsCallsToSaturdayAndSunday(string message)
    {
        // Live 2026-10-22 the model sent tomorrow only, today only, or bare "Saturday"/"Sunday".
        var calls = new[] { "Saturday", "Sunday" }
            .Select((period, index) => new Microsoft.Extensions.AI.FunctionCallContent(
                $"call_{index}",
                ConciergeToolDefinitions.GetWeather,
                new Dictionary<string, object?> { ["place"] = "Miami Beach", ["period"] = period }))
            .ToArray();

        Assert.True(ConciergeToolCallPolicy.TryGetMultiDayWeather(calls, message, out var plan));
        var expanded = ConciergeToolCallPolicy.ExpandToMultiDayWeather(calls, plan);

        Assert.Equal(["weekend+0", "weekend+1"], expanded.Select(item => item.Arguments!["period"]));
        Assert.All(expanded, item => Assert.Equal("Miami Beach", item.Arguments!["place"]));
        Assert.All(expanded, item => Assert.True(ConciergeToolCallPolicy.TryValidateArguments(item.Name, item.Arguments!, out _)));
    }

    [Theory]
    [InlineData("What's the weather next weekend?")]
    [InlineData("How was the weather last weekend?")]
    [InlineData("What's the weather this weekend, especially Saturday?")]
    public void AnotherWeekendOrANamedWeekdayKeepsTheModelsCalls(string message)
    {
        var call = new Microsoft.Extensions.AI.FunctionCallContent(
            "weather-1", ConciergeToolDefinitions.GetWeather, new Dictionary<string, object?> { ["period"] = "saturday" });

        Assert.False(ConciergeToolCallPolicy.TryGetMultiDayWeather([call], message, out _));
    }

    [Theory]
    [InlineData("What's the forecast for this weekend?", new[] { "weekend+0", "weekend+1" })]
    [InlineData("What's the weather like this weekend at the hotel?", new[] { "weekend+0", "weekend+1" })]
    [InlineData("What's the weather for the next three days?", new[] { "today", "tomorrow", "day+2" })]
    [InlineData("Will it rain over the next two days?", new[] { "today", "tomorrow" })]
    public void MultiDayWeatherWithoutModelCallsIsSynthesizedForTheHotel(string message, string[] expectedPeriods)
    {
        Assert.True(ConciergeToolCallPolicy.TrySynthesizeWeatherCalls(message, "workflow-weather", out var calls));

        Assert.Equal(expectedPeriods, calls.Select(call => call.Arguments!["period"]));
        Assert.All(calls, call => Assert.Null(call.Arguments!["place"]));
        Assert.All(calls, call => Assert.True(ConciergeToolCallPolicy.TryValidateArguments(call.Name, call.Arguments!, out _)));
        Assert.Equal(calls.Length, calls.Select(call => call.CallId).Distinct().Count());
    }

    [Theory]
    // A named place could be anywhere; the workflow does not guess which.
    [InlineData("What's the weather in Key West this weekend?")]
    [InlineData("Weekend forecast for Miami Beach?")]
    [InlineData("What's the weather near Wynwood for the next three days?")]
    // Not a weather question, or not multi-day.
    [InlineData("Book me a ride this weekend.")]
    [InlineData("What's the weather tomorrow?")]
    [InlineData("What's the weather next weekend?")]
    public void WeatherCallsAreNotSynthesizedForOtherPlacesOrRequests(string message)
    {
        Assert.False(ConciergeToolCallPolicy.TrySynthesizeWeatherCalls(message, "workflow-weather", out var calls));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("What's the weather for the next three days starting next Friday?")]
    [InlineData("Weather for the next 3 days starting the 25th")]
    [InlineData("Forecast for the next two days from next Monday")]
    [InlineData("Give me the 3-day forecast beginning October 25")]
    public void MultiDayWeatherWithAnUnmappedStartKeepsTheModelsCalls(string message)
    {
        var call = new Microsoft.Extensions.AI.FunctionCallContent(
            "weather-1", ConciergeToolDefinitions.GetWeather, new Dictionary<string, object?> { ["period"] = "today" });

        Assert.False(ConciergeToolCallPolicy.TryGetMultiDayWeather([call], message, out _));
    }

    [Theory]
    [InlineData("friday+0", true)]
    [InlineData("wednesday+6", true)]
    [InlineData("friday+7", false)]
    [InlineData("fri+1", false)]
    // A bare weekday, in any case, is the model-facing form.
    [InlineData("friday", true)]
    [InlineData("Saturday", true)]
    [InlineData("weekend+0", true)]
    [InlineData("weekend+1", true)]
    [InlineData("weekend+2", false)]
    [InlineData("weekend", false)]
    public void WorkflowWeekdayPeriodsAreBounded(string period, bool expected)
    {
        Assert.Equal(expected, ConciergeToolCallPolicy.TryValidateArguments(
            ConciergeToolDefinitions.GetWeather, new Dictionary<string, object?> { ["period"] = period }, out _));
    }

    [Fact]
    public void WeatherForDifferentPlacesOrOtherToolsIsNotRewritten()
    {
        var miami = new Microsoft.Extensions.AI.FunctionCallContent(
            "w1", ConciergeToolDefinitions.GetWeather, new Dictionary<string, object?> { ["place"] = "Miami" });
        var plantation = new Microsoft.Extensions.AI.FunctionCallContent(
            "w2", ConciergeToolDefinitions.GetWeather, new Dictionary<string, object?> { ["place"] = "Plantation" });
        var route = new Microsoft.Extensions.AI.FunctionCallContent(
            "r1", ConciergeToolDefinitions.GetRoute, new Dictionary<string, object?> { ["places"] = new[] { "Miami" } });

        Assert.False(ConciergeToolCallPolicy.TryGetMultiDayWeather(
            [miami, plantation], "weather for the next 3 days in Miami and Plantation", out _));
        Assert.False(ConciergeToolCallPolicy.TryGetMultiDayWeather(
            [miami, route], "weather for the next 3 days", out _));
    }

    [Theory]
    [InlineData("day+2", true)]
    [InlineData("day+6", true)]
    [InlineData("day+1", false)]
    [InlineData("day+7", false)]
    [InlineData("day+10", false)]
    public void WorkflowDayOffsetPeriodsAreBounded(string period, bool expected)
    {
        var arguments = new Dictionary<string, object?> { ["period"] = period };

        Assert.Equal(expected, ConciergeToolCallPolicy.TryValidateArguments(
            ConciergeToolDefinitions.GetWeather, arguments, out _));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", false)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("not-a-guid", false)]
    public void ConfirmRideAcceptsOnlyNoArguments(string? proposalId, bool expected)
    {
        var arguments = new Dictionary<string, object?>();
        if (proposalId is not null)
        {
            arguments["proposalId"] = proposalId;
        }

        var valid = ConciergeToolCallPolicy.TryValidateArguments(
            ConciergeToolDefinitions.ConfirmRide,
            arguments,
            out var error);

        Assert.Equal(expected, valid);
        Assert.Equal(
            expected ? null : "No ride was booked. A ride confirmation must not include arguments.",
            error);
    }
}
