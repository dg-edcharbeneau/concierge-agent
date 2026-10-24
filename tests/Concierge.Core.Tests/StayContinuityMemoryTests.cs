using Concierge.Contracts;
using Concierge.WorkflowServer.Workflows;
using Xunit;

namespace Concierge.Core.Tests;

public sealed class StayContinuityMemoryTests
{
    [Fact]
    public void ActionConfirmationHandsOffOnlyItsExactSourceRequestWithoutGrowingMemory()
    {
        ConciergeStayMemoryItem[] memory =
        [
            new("unresolved-request", "ride and towels", "guest requested a ride and extra towels", 4, 2),
            new("unresolved-request", "airport ride", "guest requested an airport ride", 5),
            new("allergy", "shellfish", "guest reports allergic to shellfish", 6),
        ];

        var confirmed = StayContinuityMemory.RecordActionConfirmation(memory, 4);

        Assert.Equal(memory[0] with { Category = "request-history" }, confirmed[0]);
        Assert.Equal(memory[1], confirmed[1]);
        Assert.Equal(memory[2], confirmed[2]);
        Assert.Equal("unresolved-request", memory[0].Category);
        var formatted = StayContinuityMemory.Format(confirmed);
        Assert.Contains("guest requested a ride and extra towels", formatted, StringComparison.Ordinal);
        Assert.Contains("A linked action was confirmed", formatted, StringComparison.Ordinal);
        Assert.Contains("not a new instruction", formatted, StringComparison.Ordinal);
        Assert.True(StayContinuityMemory.EstimateTokens(formatted)
            <= StayContinuityMemory.EstimateTokens(StayContinuityMemory.Format(memory)));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void ActionConfirmationDoesNotChangeUnrelatedSequencesOrGuestFacts(long sequence)
    {
        ConciergeStayMemoryItem[] memory =
        [
            new("unresolved-request", "towels", "guest requested extra towels", 6),
            new("preference", "quiet", "guest prefers a quiet room", 7),
        ];

        Assert.Equal(memory, StayContinuityMemory.RecordActionConfirmation(memory, sequence));
        Assert.Empty(StayContinuityMemory.RecordActionConfirmation([], sequence));
    }

    [Fact]
    public void NewExplicitRequestSupersedesItsHistoryAsANewUnresolvedRequest()
    {
        var requested = StayContinuityMemory.ApplyGuestMessage([], "Please book a ride to South Beach.", 2);
        var confirmed = StayContinuityMemory.RecordActionConfirmation(requested, 2);
        var repeated = StayContinuityMemory.ApplyGuestMessage(confirmed, "Please book a ride to South Beach.", 5);

        var item = Assert.Single(repeated);
        Assert.Equal("unresolved-request", item.Category);
        Assert.Equal(5, item.SourceSequence);
        Assert.Equal(2, item.SupersedesSequence);
        Assert.Equal(requested[0].Statement, item.Statement);
    }

    [Theory]
    [InlineData("I am allergic to shellfish.", "allergy")]
    [InlineData("I prefer a quiet room.", "preference")]
    [InlineData("I have decided to dine early.", "decision")]
    [InlineData("Please send extra towels.", "unresolved-request")]
    public void ExplicitGuestStatementsAreRecognized(string message, string category)
    {
        var memory = StayContinuityMemory.ApplyGuestMessage([], message, 7);

        Assert.Equal(category, Assert.Single(memory).Category);
    }

    [Fact]
    public void QuestionsAreNotRetained()
    {
        Assert.Empty(StayContinuityMemory.ApplyGuestMessage(
            [],
            "Am I allergic to shellfish?",
            0));
    }

    [Fact]
    public void DifferentPreferenceTopicsArePreservedEvenWhenNewPhraseSaysActually()
    {
        var first = StayContinuityMemory.ApplyGuestMessage(
            [],
            "I prefer a quiet room.",
            0);
        var second = StayContinuityMemory.ApplyGuestMessage(
            first,
            "Actually, I prefer vegetarian meals.",
            1);

        Assert.Equal(2, second.Count);
        Assert.All(second, item => Assert.Null(item.SupersedesSequence));
    }

    [Fact]
    public void ExactPreferenceTopicCorrectionSupersedesOnlyItsMatchingItem()
    {
        var first = StayContinuityMemory.ApplyGuestMessage(
            [],
            "I prefer a quiet room.",
            0);
        var withSeparateTopic = StayContinuityMemory.ApplyGuestMessage(
            first,
            "I prefer vegetarian meals.",
            1);
        var corrected = StayContinuityMemory.ApplyGuestMessage(
            withSeparateTopic,
            "Correction: I prefer a quiet room.",
            2);

        Assert.Equal(2, corrected.Count);
        Assert.Contains(corrected, item =>
            item.Subject == "a quiet room"
            && item.SourceSequence == 2
            && item.SupersedesSequence == 0);
        Assert.Contains(corrected, item =>
            item.Subject == "vegetarian meals"
            && item.SourceSequence == 1
            && item.SupersedesSequence is null);
    }

    [Theory]
    [InlineData("I have no allergy to shellfish.")]
    [InlineData("I have no allergies to shellfish.")]
    public void ExplicitNoAllergyCorrectionSupersedesTheMatchingClaim(string correction)
    {
        var original = StayContinuityMemory.ApplyGuestMessage(
            [],
            "I am allergic to shellfish.",
            4);
        var corrected = StayContinuityMemory.ApplyGuestMessage(original, correction, 5);

        var allergy = Assert.Single(corrected);
        Assert.Equal("allergy", allergy.Category);
        Assert.Equal("shellfish", allergy.Subject);
        Assert.Equal(5, allergy.SourceSequence);
        Assert.Equal(4, allergy.SupersedesSequence);
        Assert.Equal("guest reports no allergy to shellfish", allergy.Statement);
    }
}
