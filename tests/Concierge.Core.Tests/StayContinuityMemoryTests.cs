using Concierge.WorkflowServer.Workflows;
using Xunit;

namespace Concierge.Core.Tests;

public sealed class StayContinuityMemoryTests
{
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
