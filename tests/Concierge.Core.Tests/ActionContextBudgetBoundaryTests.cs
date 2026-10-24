using System.Text;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Setup;
using Concierge.WorkflowServer.Workflows;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace Concierge.Core.Tests;

/// <summary>
/// Sizes the action-turn budget against every supported input at its limit, then follows the
/// prompt through the tool rounds the workflow allows. Estimates are the application's
/// conservative UTF-8 byte counts, not provider token counts.
/// </summary>
public sealed class ActionContextBudgetBoundaryTests(ITestOutputHelper output)
{
    private const string GuestContextPrefix = "Verified guest context:";
    private const string MemoryPrefix = "Guest-reported stay continuity";

    // Tool arguments pass ArgumentsFit (2,000 serialized bytes); compact results are capped at 2,048.
    private const int MaxValidatedArgumentBytes = 2_000;
    private const int MaxCompactResultBytes = 2_048;

    [Theory]
    [InlineData("cancellation")]
    [InlineData("closed")]
    public void ConfirmedCancellationAndOtherOfferStatesFitTwoMaximumToolRounds(string offerState)
    {
        var prompt = BuildWorstCasePrompt(offerState);
        Assert.True(prompt.FitActionContext(includeEndChat: true));
        for (var round = 1; round <= ConciergeStayWorkflow.MaxToolRoundsPerTurn; round++)
        {
            AddToolRound(prompt.Messages, $"call-{round}", ConciergeToolDefinitions.ProposeRide);
            Report($"{offerState} round {round}", prompt.Messages);
            Assert.True(prompt.FitActionContext(includeEndChat: true));
        }
        Assert.Contains(prompt.Messages, message =>
            message.Text?.StartsWith("Last confirmed cancellation:", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void FullMemoryIsMeasuredInItsFormattedPromptRepresentation()
    {
        var memory = FullMemory();

        Assert.Equal(ConciergePromptBuilder.StayMemoryBudgetTokens, FormattedBytes(memory));
        Assert.InRange(memory.Count, 2, ConciergePromptBuilder.MaxStayMemoryItems);
        var formatted = StayContinuityMemory.Format(memory);
        Assert.Contains("[transcript sequence 1, supersedes sequence 0]", formatted, StringComparison.Ordinal);
        output.WriteLine($"{memory.Count} labelled items fill the {FormattedBytes(memory)}-byte memory cap.");
        Assert.Contains("no system confirmation of completion is recorded", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void WorstCaseTurnFitsTwoMaximumSingleToolRoundsByReplacingTheProfileWithStayFacts()
    {
        var prompt = BuildWorstCasePrompt();
        Report("initial", prompt.Messages);

        Assert.True(prompt.FitActionContext(includeEndChat: true));
        // The first model call chooses tool arguments, so it sees the whole verified profile.
        Assert.Contains(prompt.Messages, IsGuestContext);
        AssertMandatoryContextRetained(prompt.Messages);

        for (var round = 1; round <= ConciergeStayWorkflow.MaxToolRoundsPerTurn; round++)
        {
            AddToolRound(prompt.Messages, $"call-{round}", ConciergeToolDefinitions.ProposeRide);
            Report($"after round {round}", prompt.Messages);

            Assert.True(prompt.FitActionContext(includeEndChat: true));
            AssertMandatoryContextRetained(prompt.Messages);
            Assert.Equal(round, prompt.Messages.Count(message => message.Role == ChatRole.Tool));
        }

        // The full profile (22,397 with it) cannot stay for the final call. Its name-free stay
        // facts replace it, so the final answer still has the verified room and stay dates.
        Assert.DoesNotContain(prompt.Messages, IsGuestContext);
        AssertStayFactsReplaceTheProfile(prompt.Messages);
        output.WriteLine(
            $"final call: {ConciergePromptBuilder.EstimateActionPromptTokens(prompt.Messages) + Reserved} "
            + $"of {ConciergePromptBuilder.ActionWorkingContextBudgetTokens}");
    }

    [Fact]
    public void StayFactsCarryEveryVerifiedStayValueButNoName()
    {
        var facts = VerifiedGuestAnswerContext.FormatStayFacts(WorstCaseGuest);
        var profile = VerifiedGuestAnswerContext.Format(WorstCaseGuest);

        output.WriteLine($"stay facts {Encoding.UTF8.GetByteCount(facts)} bytes; profile {Encoding.UTF8.GetByteCount(profile)}.");
        Assert.DoesNotContain("\\u20AC", facts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("use no guest name", facts, StringComparison.Ordinal);
        foreach (var value in StayValues(WorstCaseGuest))
        {
            Assert.Contains(value, facts, StringComparison.Ordinal);
            Assert.Contains(value, profile, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorstCaseSupportedTurnFitsARepresentativeFullWeekWeatherRound()
    {
        var prompt = BuildWorstCasePrompt();
        Assert.True(prompt.FitActionContext(includeEndChat: true));

        // Observed live weather results are about 350 bytes; give each day 512 and a 200-byte place.
        AddWeatherRound(prompt.Messages, ConciergeToolCallPolicy.MaxParallelWeatherCalls, 512);

        Report("after week weather", prompt.Messages);
        Assert.True(prompt.FitActionContext(includeEndChat: true));
        AssertMandatoryContextRetained(prompt.Messages);
    }

    [Fact]
    public void FullWeekWeatherAtTheResultCapFailsClosedWithStayContextRetained()
    {
        // Each weather result is capped at 2,048 bytes, not the round, so a full week at the cap
        // is allowed input that the budget does not cover. Find where it stops fitting.
        var fittingDays = 0;
        for (var days = 1; days <= ConciergeToolCallPolicy.MaxParallelWeatherCalls; days++)
        {
            var attempt = BuildWorstCasePrompt();
            Assert.True(attempt.FitActionContext(includeEndChat: true));
            AddWeatherRound(attempt.Messages, days, MaxCompactResultBytes);
            if (!attempt.FitActionContext(includeEndChat: true))
            {
                break;
            }

            fittingDays = days;
        }

        output.WriteLine($"{fittingDays} weather results at the 2,048-byte cap fit; more get the context-limit reply.");
        Assert.InRange(fittingDays, 1, ConciergeToolCallPolicy.MaxParallelWeatherCalls - 1);

        var prompt = BuildWorstCasePrompt();
        Assert.True(prompt.FitActionContext(includeEndChat: true));
        AddWeatherRound(prompt.Messages, ConciergeToolCallPolicy.MaxParallelWeatherCalls, MaxCompactResultBytes);
        Assert.False(prompt.FitActionContext(includeEndChat: true));
        AssertMandatoryContextRetained(prompt.Messages);
        AssertStayFactsReplaceTheProfile(prompt.Messages);
    }

    [Fact]
    public void ToolResultsPastTheAllowanceTrimOptionalContextThenFailClosedWithoutDroppingMemory()
    {
        var prompt = BuildWorstCasePrompt();
        Assert.True(prompt.FitActionContext(includeEndChat: true));

        var round = 0;
        while (prompt.FitActionContext(includeEndChat: true))
        {
            Assert.True(++round <= 10, "Tool results never exhausted the action budget.");
            AddToolRound(prompt.Messages, $"overflow-{round}", ConciergeToolDefinitions.GetRoute);
        }

        // Rounds past the workflow limit are reachable only through parallel weather, so the
        // boundary sits beyond the supported two maximum rounds.
        Assert.True(round > ConciergeStayWorkflow.MaxToolRoundsPerTurn);
        Assert.DoesNotContain(prompt.Messages, IsGuestContext);
        AssertStayFactsReplaceTheProfile(prompt.Messages);
        Assert.DoesNotContain(prompt.Messages, message =>
            message.Role == ChatRole.User && message.Text != new string('a', ConciergeTurnLimits.MaxUserMessageUtf8Bytes));
        Assert.Contains(prompt.Messages, message =>
            message.Text?.Contains("did not fit the safe context budget", StringComparison.Ordinal) == true);
        AssertMandatoryContextRetained(prompt.Messages);
    }

    // Non-ASCII names JSON-escape to six bytes per character, the largest profile encoding. Stay
    // length has no limit, so the widest dates give the longest Nights value (seven digits).
    private static readonly ConciergeVerifiedGuest WorstCaseGuest = new(
        Guid.NewGuid(),
        new string('\u20ac', ConciergeVerifiedGuest.MaxNameLength),
        new string('\u20ac', ConciergeVerifiedGuest.MaxNameLength),
        "9999",
        Guid.NewGuid(),
        DateOnly.MaxValue,
        DateOnly.MinValue.AddDays(1));

    private static int Reserved =>
        ConciergeToolDefinitions.EstimateToolDefinitionTokens(includeEndChat: true)
        + ConciergePromptBuilder.AnswerMaxOutputTokens
        + ConciergePromptBuilder.ContextEstimationHeadroomTokens;

    private ConciergePromptBuilder.PromptBuildResult BuildWorstCasePrompt(string offerState = "ride")
    {
        var guest = WorstCaseGuest;
        var evidence = Enumerable.Range(0, 4)
            .Select(index => new HotelRetrievalEvidence(
                $"c{index}", $"k{index}", "s", "t", "t", null, new string('e', 600),
                "v", "h", "ready", "{}", "{}", index + 1, index + 1, index + 1, 1))
            .ToArray();
        ConciergeTranscriptTurn[] recentTurns =
        [
            .. Enumerable.Range(0, 4).Select(index => new ConciergeTranscriptTurn(
                $"old-{index}", index, "web", new string('q', 400), new string('r', 400), DateTimeOffset.UnixEpoch)),
        ];
        var budget = new ConciergePromptBuilder.PromptBudget(
            ConciergePromptBuilder.ActionWorkingContextBudgetTokens,
            ConciergeToolDefinitions.EstimateToolDefinitionTokens(includeEndChat: true),
            ActionTools: true);

        var prompt = ConciergePromptBuilder.BuildMemoryTurn(
            new string('a', ConciergeTurnLimits.MaxUserMessageUtf8Bytes),
            new HotelRetrievalResult(HotelRetrievalPath.Hybrid, evidence),
            FullMemory(),
            budget,
            guest,
            recentTurns);

        // The workflow inserts the offer state after building the prompt, before the guest message.
        // Place names are capped at 200 UTF-8 bytes when the proposal is created.
        var rideOffer = new ConciergeRideProposal(
                Guid.NewGuid(), 4,
                new(new string('p', 200), 0, 0),
                new(new string('d', 200), 0, 0),
                Immediate: false, DateTimeOffset.UnixEpoch, 600);
        var offer = ConciergeStayWorkflow.RideOfferContextNote(
            offerState == "ride" ? rideOffer : null,
            offerState == "cancellation"
                ? new ConciergeRideCancellationProposal(Guid.NewGuid(), 4, "Booked",
                    new string('p', 200), new string('d', 200), DateTimeOffset.UnixEpoch)
                : null,
            offerState == "closed" ? rideOffer : null, 5);
        var currentUser = prompt.Messages.FindLastIndex(message => message.Role == ChatRole.User);
        prompt.Messages.Insert(currentUser, new ChatMessage(ChatRole.System, offer));
        prompt.Messages.Insert(currentUser + 1, new ChatMessage(ChatRole.System,
            ConciergeStayWorkflow.CancellationOutcomeContextNote(new ConciergeRideCancellationOutcome(
                Guid.NewGuid(), new string('c', 200), DateTimeOffset.UnixEpoch, "confirmed-cancellation", 3))));
        return prompt;
    }

    private static List<ConciergeStayMemoryItem> FullMemory()
    {
        // Item labels cost far more than statements, so the byte cap binds before the item cap.
        // Add labelled items while another fits, then pad the last to meet the cap exactly.
        var items = new List<ConciergeStayMemoryItem>();
        for (var sequence = 0; sequence < ConciergePromptBuilder.MaxStayMemoryItems; sequence++)
        {
            var next = new ConciergeStayMemoryItem(
                sequence % 2 == 0 ? "unresolved-request" : "preference",
                $"topic {sequence}",
                "x",
                sequence,
                sequence > 0 ? sequence - 1 : null);
            if (FormattedBytes([.. items, next]) > ConciergePromptBuilder.StayMemoryBudgetTokens)
            {
                break;
            }

            items.Add(next);
        }

        var last = items[^1];
        items[^1] = last with
        {
            Statement = last.Statement
                + new string('m', ConciergePromptBuilder.StayMemoryBudgetTokens - FormattedBytes(items)),
        };
        return items;
    }

    private static int FormattedBytes(IReadOnlyList<ConciergeStayMemoryItem> items) =>
        StayContinuityMemory.EstimateTokens(StayContinuityMemory.Format(items));

    private static void AddToolRound(List<ChatMessage> messages, string callId, string toolName)
    {
        messages.Add(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent(callId, toolName, new Dictionary<string, object?>
            {
                // {"dropoff":"..."} serializes to the validated argument cap.
                ["dropoff"] = new string('d', MaxValidatedArgumentBytes - "{\"dropoff\":\"\"}".Length),
            }),
        ]));
        messages.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent(callId, new string('r', MaxCompactResultBytes))]));
    }

    private static void AddWeatherRound(List<ChatMessage> messages, int days, int resultBytes)
    {
        for (var day = 0; day < days; day++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent($"weather-{day}", ConciergeToolDefinitions.GetWeather,
                    new Dictionary<string, object?> { ["place"] = new string('p', 200), ["period"] = $"day+{day}" }),
            ]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent($"weather-{day}", new string('w', resultBytes))]));
        }
    }

    private static IEnumerable<string> StayValues(ConciergeVerifiedGuest guest) =>
    [
        guest.RoomNumber!,
        $"\"Floor\":{guest.Floor}",
        guest.CheckInDate.ToString("ddd yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        guest.CheckoutDate.ToString("ddd yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        $"\"Nights\":{guest.Nights}",
    ];

    private static void AssertStayFactsReplaceTheProfile(IReadOnlyList<ChatMessage> messages)
    {
        var facts = Assert.Single(messages, message =>
            message.Role == ChatRole.System
            && message.Text?.StartsWith(VerifiedGuestAnswerContext.StayFactsPrefix, StringComparison.Ordinal) == true);
        Assert.Equal(VerifiedGuestAnswerContext.FormatStayFacts(WorstCaseGuest), facts.Text);
        Assert.Equal(7, WorstCaseGuest.Nights!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
    }

    private static void AssertMandatoryContextRetained(IReadOnlyList<ChatMessage> messages)
    {
        Assert.Single(messages, message =>
            message.Text?.StartsWith(MemoryPrefix, StringComparison.Ordinal) == true
            && Encoding.UTF8.GetByteCount(message.Text) == ConciergePromptBuilder.StayMemoryBudgetTokens);
        Assert.Contains(messages, message =>
            message.Role == ChatRole.User
            && message.Text == new string('a', ConciergeTurnLimits.MaxUserMessageUtf8Bytes));
        Assert.Contains(messages, message =>
            message.Text?.StartsWith("Ride offer state:", StringComparison.Ordinal) == true);
        Assert.Contains(messages, message =>
            message.Text?.StartsWith("Last confirmed cancellation:", StringComparison.Ordinal) == true);
    }

    private static bool IsGuestContext(ChatMessage message) =>
        message.Text?.StartsWith(GuestContextPrefix, StringComparison.Ordinal) == true;

    private void Report(string stage, IReadOnlyList<ChatMessage> messages)
    {
        // Essential context: everything trimming can't remove. Earlier turns go, evidence shrinks to
        // its placeholder, and the guest profile gives way to its stay facts once tool results need room.
        var currentUser = messages.ToList().FindLastIndex(message => message.Role == ChatRole.User);
        var essential = messages
            .Where((message, index) => !(index < currentUser
                && (message.Role == ChatRole.User || message.Role == ChatRole.Assistant)))
            .Where(message => !IsGuestContext(message)
                && message.Text?.StartsWith(VerifiedGuestAnswerContext.StayFactsPrefix, StringComparison.Ordinal) != true)
            .Select(message => message.Text?.StartsWith("Current Hotel ZGY evidence", StringComparison.Ordinal) == true
                ? new ChatMessage(ChatRole.System,
                    "Current Hotel ZGY evidence for this question:\nRetrieved evidence did not fit the safe context budget; do not infer hotel facts.")
                : message)
            .ToList();
        var reserved = Reserved;
        var profile = Encoding.UTF8.GetByteCount(VerifiedGuestAnswerContext.Format(WorstCaseGuest)) + 8;
        var stayFacts = Encoding.UTF8.GetByteCount(VerifiedGuestAnswerContext.FormatStayFacts(WorstCaseGuest)) + 8;
        var floor = ConciergePromptBuilder.EstimateActionPromptTokens(essential) + reserved;
        output.WriteLine(
            $"{stage}: essential {floor}, with stay facts {floor + stayFacts}, with profile {floor + profile} "
            + $"(budget {ConciergePromptBuilder.ActionWorkingContextBudgetTokens})");
    }
}
