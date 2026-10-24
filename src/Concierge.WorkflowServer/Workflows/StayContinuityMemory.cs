using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Concierge.Contracts;

namespace Concierge.WorkflowServer.Workflows;

/// <summary>
/// Conservatively recognizes explicit guest statements. This intentionally does not interpret
/// model answers, questions, or general conversation as facts.
/// </summary>
internal static class StayContinuityMemory
{
    private const RegexOptions PatternOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private static readonly Regex NegativeAllergyPattern = new(
        @"\b(?:i\s+am|i'm)\s+(?<negation>not|no longer)\s+allergic\s+to\s+(?<subject>[^.!?\r\n]{1,96})",
        PatternOptions);

    private static readonly Regex NegativeHaveAllergyPattern = new(
        @"\bi\s+have\s+no\s+allerg(?:y|ies)\s+to\s+(?<subject>[^.!?\r\n]{1,96})",
        PatternOptions);

    private static readonly Regex PositiveAllergyPattern = new(
        @"\b(?:i\s+am|i'm)\s+allergic\s+to\s+(?<subject>[^.!?\r\n]{1,96})"
        + @"|\bi\s+have\s+(?:an?\s+)?(?<subject>[^.!?\r\n]{1,96}?)\s+allerg(?:y|ies)\b"
        + @"|\bmy\s+allerg(?:y|ies)\s+(?:is|are)\s+(?<subject>[^.!?\r\n]{1,96})",
        PatternOptions);

    private static readonly Regex PreferencePattern = new(
        @"\bi\s+(?:prefer|like|would rather)\s+(?<subject>[^.!?\r\n]{1,160})",
        PatternOptions);

    private static readonly Regex DecisionPattern = new(
        @"\bi(?:'ve| have)\s+decided\s+to\s+(?<subject>[^.!?\r\n]{1,160})",
        PatternOptions);

    private static readonly Regex RequestPattern = new(
        @"\b(?:please\s+(?:arrange|book|reserve|send|provide|bring|schedule|call|request|prepare|deliver|set\s+up)"
        + @"|i\s+need\s+(?:you\s+to\s+)?(?:arrange|book|reserve|send|provide|bring|schedule|call|request|prepare|deliver|set\s+up)"
        + @"|i'd\s+like\s+you\s+to\s+(?:arrange|book|reserve|send|provide|bring|schedule|call|request|prepare|deliver|set\s+up))"
        + @"\s+(?<subject>[^.!?\r\n]{1,160})",
        PatternOptions);

    public static IReadOnlyList<ConciergeStayMemoryItem> ApplyGuestMessage(
        IReadOnlyList<ConciergeStayMemoryItem> current,
        string guestMessage,
        long sequence)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(guestMessage);

        // Questions are not evidence that a guest has an allergy, preference, or decision.
        if (guestMessage.Contains('?'))
        {
            return current;
        }

        var candidate = MatchAllergy(guestMessage, sequence)
            ?? MatchSimpleClaim(PreferencePattern, "preference", guestMessage, sequence)
            ?? MatchSimpleClaim(DecisionPattern, "decision", guestMessage, sequence)
            ?? MatchSimpleClaim(RequestPattern, "unresolved-request", guestMessage, sequence);
        if (candidate is null)
        {
            return current;
        }

        if (current.Any(item => item.SourceSequence == sequence))
        {
            return current;
        }

        var superseded = current.Where(item =>
            (StringComparer.Ordinal.Equals(item.Category, candidate.Category)
                || candidate.Category == "unresolved-request" && item.Category == "request-history")
            && StringComparer.OrdinalIgnoreCase.Equals(item.Subject, candidate.Subject))
            .ToArray();

        var supersedesSequence = superseded
            .Select(item => (long?)item.SourceSequence)
            .Max();
        var next = current.Except(superseded).Append(
            candidate with { SupersedesSequence = supersedesSequence }).ToArray();
        return next;
    }

    public static IReadOnlyList<ConciergeStayMemoryItem> RecordActionConfirmation(
        IReadOnlyList<ConciergeStayMemoryItem> current,
        long requestSequence) => current.Select(item =>
            item.SourceSequence == requestSequence && item.Category == "unresolved-request"
                ? item with { Category = "request-history" }
                : item).ToArray();

    public static string Format(IReadOnlyList<ConciergeStayMemoryItem> memory)
    {
        if (memory.Count == 0)
        {
            return string.Empty;
        }

        var text = new StringBuilder(
            "Guest-reported stay continuity (not verified hotel facts):\n");
        foreach (var item in memory.OrderBy(item => item.SourceSequence))
        {
            text.Append("- Guest-reported ").Append(item.Category)
                .Append(" [transcript sequence ")
                .Append(item.SourceSequence.ToString(CultureInfo.InvariantCulture));
            if (item.SupersedesSequence is { } superseded)
            {
                text.Append(", supersedes sequence ")
                    .Append(superseded.ToString(CultureInfo.InvariantCulture));
            }

            text.Append("]: ").Append(item.Statement);
            if (StringComparer.Ordinal.Equals(item.Category, "unresolved-request"))
            {
                text.Append(" This remains unresolved; no system confirmation of completion is recorded.");
            }
            else if (StringComparer.Ordinal.Equals(item.Category, "request-history"))
            {
                // A linked action can cover only part of a compound request. Preserve the guest's
                // words without claiming the entire request was fulfilled or asking to repeat it.
                text.Append(" A linked action was confirmed; request history is not a new instruction.");
            }

            text.Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    public static int EstimateTokens(string text) => Encoding.UTF8.GetByteCount(text);

    private static ConciergeStayMemoryItem? MatchAllergy(string message, long sequence)
    {
        var negative = NegativeAllergyPattern.Match(message);
        var negativeHaveAllergy = negative.Success
            ? Match.Empty
            : NegativeHaveAllergyPattern.Match(message);
        var positive = negative.Success || negativeHaveAllergy.Success
            ? Match.Empty
            : PositiveAllergyPattern.Match(message);
        var match = negative.Success
            ? negative
            : negativeHaveAllergy.Success ? negativeHaveAllergy : positive;
        if (!match.Success)
        {
            return null;
        }

        var subject = CleanSubject(match.Groups["subject"].Value);
        if (subject.Length == 0)
        {
            return null;
        }

        var statement = negative.Success
            ? $"guest reports {negative.Groups["negation"].Value.ToLowerInvariant()} allergic to {subject}"
            : negativeHaveAllergy.Success
                ? $"guest reports no allergy to {subject}"
                : $"guest reports allergic to {subject}";
        return new ConciergeStayMemoryItem("allergy", Normalize(subject), statement, sequence);
    }

    private static ConciergeStayMemoryItem? MatchSimpleClaim(
        Regex pattern,
        string category,
        string message,
        long sequence)
    {
        var match = pattern.Match(message);
        if (!match.Success)
        {
            return null;
        }

        var subject = CleanSubject(match.Groups["subject"].Value);
        if (subject.Length == 0)
        {
            return null;
        }

        var statement = category switch
        {
            "preference" => $"guest says they prefer {subject}",
            "decision" => $"guest decided to {subject}",
            _ => $"guest requested {subject}",
        };
        return new ConciergeStayMemoryItem(category, Normalize(subject), statement, sequence);
    }

    private static string CleanSubject(string value)
    {
        var safeCharacters = value.Where(character =>
            char.IsLetterOrDigit(character)
            || char.IsWhiteSpace(character)
            || character is '-' or '\'');
        return Regex.Replace(
            new string(safeCharacters.ToArray()).Trim(),
            @"\s+",
            " ",
            PatternOptions);
    }

    private static string Normalize(string value) =>
        Regex.Replace(value.Trim(), @"\s+", " ", PatternOptions).ToLowerInvariant();
}
