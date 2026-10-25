using System.Text.Json;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Workflows;

namespace Concierge.Core.Tests;

public sealed class ConciergeToolDefinitionTests
{
    [Fact]
    public void ScheduledPickupSchemaDefersToConfiguredMinimumInsteadOfClaimingAFixedLimit()
    {
        var description = Schema(ConciergeToolDefinitions.ProposeRide)
            .GetProperty("properties").GetProperty("pickupAt")
            .GetProperty("description").GetString()!;

        Assert.Contains("ISO 8601 with UTC offset", description, StringComparison.Ordinal);
        Assert.Contains("configured minimum", description, StringComparison.Ordinal);
        Assert.DoesNotContain("45", description, StringComparison.Ordinal);
        Assert.DoesNotContain("5 minutes", description, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryModelToolExplainsWhatItDoes()
    {
        var tools = ConciergeToolDefinitions.CreateModelTools(includeEndChat: false);

        Assert.Equal(10, tools.Count);
        Assert.All(tools, tool => Assert.True(
            tool.Description.Length >= 25,
            $"{tool.Name} needs a description the model can act on: '{tool.Description}'."));
        Assert.All(tools, tool => Assert.DoesNotContain("simulat", tool.Description, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(ConciergeToolDefinitions.GetWeather, "place")]
    [InlineData(ConciergeToolDefinitions.GetRoute, "places")]
    [InlineData(ConciergeToolDefinitions.ProposeRide, "pickup")]
    public void HotelDefaultIsDeclaredOnTheLocationParameter(string toolName, string parameter)
    {
        var schema = Schema(toolName);

        var description = schema.GetProperty("properties").GetProperty(parameter)
            .GetProperty("description").GetString();
        Assert.Contains("Hotel ZGY", description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConciergeToolDefinitions.GetWeather, "place")]
    [InlineData(ConciergeToolDefinitions.ProposeRide, "pickup")]
    [InlineData(ConciergeToolDefinitions.ProposeRide, "pickupAt")]
    public void HotelDefaultedParametersAreOptional(string toolName, string parameter)
    {
        var schema = Schema(toolName);

        var required = schema.TryGetProperty("required", out var values)
            ? values.EnumerateArray().Select(value => value.GetString()).ToArray()
            : [];
        Assert.DoesNotContain(parameter, required);
    }

    [Fact]
    public void ActionPromptDeclaresHotelDefaultAndGreetingReplyNamesEveryCapability()
    {
        var tools = ConciergeToolDefinitions.CreateModelTools(includeEndChat: false);
        Assert.Contains(
            "Omit place unless the guest names one.",
            ConciergeStayWorkflow.ConciergeActionInstructions,
            StringComparison.Ordinal);
        Assert.Contains(
            "Weather, route start, and ride pickup default to Hotel ZGY; never ask for them.",
            ConciergeStayWorkflow.ConciergeActionInstructions,
            StringComparison.Ordinal);

        var greeting = ConciergeStayWorkflow.ConciergeActionBaseInstructions;
        Assert.Contains("what can you do?", greeting, StringComparison.Ordinal);
        Assert.Contains("hotel questions", greeting, StringComparison.Ordinal);
        Assert.Contains("weather", greeting, StringComparison.Ordinal);
        Assert.Contains("driving directions", greeting, StringComparison.Ordinal);
        Assert.Contains("finding nearby places", greeting, StringComparison.Ordinal);
        Assert.Contains("named-place opening hours", greeting, StringComparison.Ordinal);
        Assert.Contains("complimentary rides", greeting, StringComparison.Ordinal);
        Assert.DoesNotContain("simulat", greeting, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("simulat", ConciergeStayWorkflow.ConciergeActionInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accepts the pending cancellation offer", ConciergeStayWorkflow.ConciergeActionInstructions, StringComparison.Ordinal);
        Assert.Contains("stage an offer", ConciergeStayWorkflow.ConciergeActionInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelRide", tools.Select(tool => tool.Name));
    }

    private static JsonElement Schema(string toolName) =>
        ConciergeToolDefinitions.FindModelTool(toolName, includeEndChat: false).JsonSchema;
}
