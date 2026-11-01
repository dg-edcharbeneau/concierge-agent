using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Setup;
using Microsoft.Extensions.AI;

namespace Concierge.Core.Tests;

public sealed class ConciergePromptBuilderTests
{
    [Fact]
    public void MissingHotelEvidenceDoesNotDisableNearbyPlaceTools()
    {
        var prompt = ConciergePromptBuilder.BuildMemoryTurn(
            "What is the nearest pharmacy?",
            new HotelRetrievalResult(HotelRetrievalPath.NoEvidence, []),
            [],
            new ConciergePromptBuilder.PromptBudget(
                ConciergePromptBuilder.ActionWorkingContextBudgetTokens, 0, ActionTools: true),
            guest: null,
            recentTurns: []);

        Assert.Contains("Use tools for live information even without hotel evidence.", prompt.Messages[0].Text);
        Assert.Contains("FindNearbyPlaces: new nearest/nearby search.", prompt.Messages[1].Text);
        Assert.Contains("call ProposeRide directly, without searching again", prompt.Messages[1].Text);
        Assert.True(prompt.FitActionContext(includeEndChat: true));
    }

    [Fact]
    public void ActionPromptKeepsApprovedWeatherInstructionAlongsideEarlierForecasts()
    {
        ConciergeTranscriptTurn[] recentTurns =
        [
            new(
                "prior-weather",
                0,
                "web",
                "What's the weather this weekend?",
                "Saturday looks sunny.",
                DateTimeOffset.UnixEpoch),
        ];

        var prompt = ConciergePromptBuilder.BuildMemoryTurn(
            "What's the weather in Key West this weekend?",
            new HotelRetrievalResult(HotelRetrievalPath.NoEvidence, []),
            [],
            new ConciergePromptBuilder.PromptBudget(
                ConciergePromptBuilder.ActionWorkingContextBudgetTokens, 0, ActionTools: true),
            guest: null,
            recentTurns);
        var actionInstructions = prompt.Messages[1].Text!;

        // Pin the instruction isolated by the live comparison; stronger wording misrouted other tools.
        Assert.Contains(
            "- GetWeather: any weather question; \"current\" is right now, not daily highs/lows. Weather, route start, and ride pickup default to Hotel ZGY; never ask for them. Omit place unless the guest names one.",
            actionInstructions,
            StringComparison.Ordinal);
        Assert.Contains(
            "Use current weather and route results directly.",
            prompt.Messages[0].Text,
            StringComparison.Ordinal);
        Assert.Contains(prompt.Messages, message => message.Text == "Saturday looks sunny.");
        Assert.True(prompt.FitActionContext(includeEndChat: true));
    }

    [Fact]
    public void CurrentWeatherInstructionAndToolSchemaFitTheFixedAndActionBudgets()
    {
        var prompt = ConciergePromptBuilder.BuildMemoryTurn(
            "What's the weather right now?",
            new HotelRetrievalResult(HotelRetrievalPath.NoEvidence, []),
            [],
            new ConciergePromptBuilder.PromptBudget(
                ConciergePromptBuilder.ActionWorkingContextBudgetTokens,
                ConciergeToolDefinitions.EstimateToolDefinitionTokens(includeEndChat: true),
                ActionTools: true),
            guest: null,
            recentTurns: []);
        var weatherTool = ConciergeToolDefinitions.FindModelTool(
            ConciergeToolDefinitions.GetWeather, includeEndChat: false);

        Assert.Contains("\"current\" is right now, not daily highs/lows", prompt.Messages[1].Text, StringComparison.Ordinal);
        Assert.Contains("current", weatherTool.JsonSchema.ToString(), StringComparison.Ordinal);
        Assert.True(prompt.FitActionContext(includeEndChat: true));
        Assert.Equal(
            ConciergePromptBuilder.FixedPromptAllowanceTokens
                + prompt.Messages.Sum(message =>
                    System.Text.Encoding.UTF8.GetByteCount(message.Text ?? string.Empty) + 8),
            ConciergePromptBuilder.EstimatePromptTokens(prompt.Messages));
        Assert.True(
            ConciergePromptBuilder.EstimatePromptTokens(prompt.Messages)
                + ConciergeToolDefinitions.EstimateToolDefinitionTokens(includeEndChat: true)
                + ConciergePromptBuilder.AnswerMaxOutputTokens
                + ConciergePromptBuilder.ContextEstimationHeadroomTokens
            <= ConciergePromptBuilder.ActionWorkingContextBudgetTokens);
    }

    [Fact]
    public void ActionPromptDoesNotAskTheModelToRenderWorkflowOwnedRideProposals()
    {
        var prompt = ConciergePromptBuilder.BuildMemoryTurn(
            "Book me a ride to South Beach.",
            new HotelRetrievalResult(HotelRetrievalPath.NoEvidence, []),
            [],
            new ConciergePromptBuilder.PromptBudget(
                ConciergePromptBuilder.ActionWorkingContextBudgetTokens, 0, ActionTools: true),
            guest: null,
            recentTurns: []);

        Assert.Equal(ConciergePromptBuilder.ConciergeActionInstructions, prompt.Messages[1].Text);
        Assert.DoesNotContain(
            prompt.Messages,
            message => message.Text?.Contains("Repeat the proposal exactly", StringComparison.Ordinal) == true);
        Assert.Contains(
            "ProposeRide: when you determine from the guest's intent that they want a ride booked; this only prepares an offer and never books.",
            prompt.Messages[1].Text,
            StringComparison.Ordinal);
        Assert.Contains("accepts the pending ride offer named in the ride offer state, in any wording", prompt.Messages[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("immediately preceding", prompt.Messages[1].Text, StringComparison.Ordinal);
        Assert.Contains("ProposeRideCancellation", prompt.Messages[1].Text, StringComparison.Ordinal);
        Assert.Contains("ConfirmRideCancellation", prompt.Messages[1].Text, StringComparison.Ordinal);
        Assert.True(prompt.FitActionContext(includeEndChat: true));
        Assert.Equal(
            ConciergePromptBuilder.FixedPromptAllowanceTokens
                + prompt.Messages.Sum(message => System.Text.Encoding.UTF8.GetByteCount(message.Text ?? string.Empty) + 8),
            ConciergePromptBuilder.EstimatePromptTokens(prompt.Messages));
    }

    [Fact]
    public void MemoryPromptRetainsRecentTurnsInOrderAndRemovesWeatherImageMarkdown()
    {
        ConciergeTranscriptTurn[] turns =
        [
            new("old-1", 0, "web", "First question", "![sun](/images/weather/clear.svg)\n\nFirst answer.",
                DateTimeOffset.UnixEpoch),
            new("old-2", 1, "web", "Second question", "Second answer.",
                DateTimeOffset.UnixEpoch.AddMinutes(1)),
        ];

        var prompt = ConciergePromptBuilder.BuildMemoryTurn(
            "Current question",
            new HotelRetrievalResult(HotelRetrievalPath.NoEvidence, []),
            [],
            new ConciergePromptBuilder.PromptBudget(
                ConciergePromptBuilder.AnswerWorkingContextBudgetTokens, 0, ActionTools: false),
            guest: null,
            turns);

        Assert.Equal(turns, prompt.RetainedTurns);
        Assert.Equal(
            new[]
            {
                ChatRole.System,
                ChatRole.System,
                ChatRole.User,
                ChatRole.Assistant,
                ChatRole.User,
                ChatRole.Assistant,
                ChatRole.User,
            },
            prompt.Messages.Select(message => message.Role));
        Assert.Equal("First answer.", prompt.Messages[3].Text);
        Assert.Contains(
            prompt.Messages,
            message => message.Text?.Contains("No current hotel evidence was found.", StringComparison.Ordinal)
                == true);
    }

    [Fact]
    public void MaximumMessageWithFullEscapedProfileFitsTheActionBudgetWithEveryTool()
    {
        // Supported limits: a 2,000-byte guest message, given and family names at the shared limit
        // that JSON-escape to six bytes per character, plus stay dates and evidence that cannot fit.
        var today = new DateOnly(2026, 10, 22);
        var maxName = new string('\u20ac', ConciergeVerifiedGuest.MaxNameLength);
        var guest = new ConciergeVerifiedGuest(
            Guid.NewGuid(), maxName, maxName, "1112", Guid.NewGuid(), today.AddDays(30), today);
        var evidence = new HotelRetrievalEvidence(
            "c", "k", "s", "t", "t", null, new string('e', 4_000), "v", "h", "ready", "{}", "{}", 1, 1, 1, 1);
        var budget = new ConciergePromptBuilder.PromptBudget(
            ConciergePromptBuilder.ActionWorkingContextBudgetTokens,
            ConciergeToolDefinitions.EstimateToolDefinitionTokens(includeEndChat: true),
            ActionTools: true);

        var prompt = ConciergePromptBuilder.BuildMemoryTurn(
            new string('a', ConciergeTurnLimits.MaxUserMessageUtf8Bytes),
            new HotelRetrievalResult(HotelRetrievalPath.Hybrid, [evidence]),
            [],
            budget,
            guest,
            recentTurns: []);

        Assert.True(prompt.FitActionContext(includeEndChat: true));
        Assert.Single(prompt.Messages, message =>
            message.Text?.StartsWith("Verified guest context:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ActionBudgetTrimsTheExplicitEvidenceSectionBeforeFailing()
    {
        // Fill the answer budget exactly, so only the tool definitions push the action prompt over.
        var budget = new ConciergePromptBuilder.PromptBudget(
            ConciergePromptBuilder.ActionWorkingContextBudgetTokens, 0, ActionTools: true);
        var retrieval = new HotelRetrievalResult(HotelRetrievalPath.NoEvidence, []);
        var baseline = ConciergePromptBuilder.BuildMemoryTurn(
            string.Empty, retrieval, [], budget, guest: null, recentTurns: []);
        var prompt = ConciergePromptBuilder.BuildMemoryTurn(
            new string('x', budget.PromptLimit - ConciergePromptBuilder.EstimatePromptTokens(baseline.Messages)),
            retrieval,
            [],
            budget,
            guest: null,
            recentTurns: []);

        Assert.False(prompt.FitActionContext(includeEndChat: false));
        var evidence = Assert.Single(prompt.Messages, message =>
            message.Role == ChatRole.System
            && message.Text?.Contains("evidence for this question", StringComparison.Ordinal) == true);
        Assert.Equal(
            "Current Hotel ZGY evidence for this question:\nRetrieved evidence did not fit the safe context budget; do not infer hotel facts.",
            evidence.Text);
    }
}
