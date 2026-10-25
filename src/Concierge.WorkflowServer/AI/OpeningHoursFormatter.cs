using System.Globalization;
using System.Text.RegularExpressions;

namespace Concierge.WorkflowServer.AI;

/// <summary>
/// Parses a common subset of the OpenStreetMap <c>opening_hours</c> syntax into a Monday-first
/// week and renders it for the web or voice channel. Pure and deterministic, so the workflow can
/// call it. Anything outside the subset fails to parse and callers show the provider string.
/// </summary>
/// <remarks>
/// Supported: semicolon-separated rules; an optional weekday selector (<c>Mo</c>..<c>Su</c>,
/// ranges such as <c>Mo-Fr</c> or wrapping <c>Sa-Mo</c>, and comma lists); one or more
/// <c>HH:MM-HH:MM</c> ranges, an end at or before the start meaning past midnight;
/// <c>off</c>/<c>closed</c>; and a standalone <c>24/7</c>. A later rule replaces earlier hours
/// for the days it names; days no rule names are closed. Holidays, months, weeks, fallback
/// (<c>||</c>), comments, and solar times are not supported.
/// See https://wiki.openstreetmap.org/wiki/Key:opening_hours/specification.
/// </remarks>
internal static class OpeningHoursFormatter
{
    internal const int MinutesPerDay = 24 * 60;

    internal readonly record struct OpeningInterval(int StartMinute, int EndMinute)
    {
        // End is exclusive; an end after MinutesPerDay continues into the next day.
        internal bool IsAllDay => StartMinute == 0 && EndMinute == MinutesPerDay;
    }

    private static readonly string[] OsmDays = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];
    private static readonly string[] ShortDays = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private static readonly string[] LongDays =
        ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    private const string Day = "(?:Mo|Tu|We|Th|Fr|Sa|Su)";
    private const string DayItem = Day + "(?:-" + Day + ")?";
    private const string Time = "[0-9]{2}:[0-9]{2}";
    private const string TimeSpanPattern = Time + "-" + Time;

    private static readonly Regex RulePattern = new(
        "^(?:(?<days>" + DayItem + @"(?:\s*,\s*" + DayItem + @")*)\s+)?"
        + "(?<times>" + TimeSpanPattern + @"(?:\s*,\s*" + TimeSpanPattern + ")*|off|closed)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Parses the supported subset into seven Monday-first days; an empty day is closed.</summary>
    internal static bool TryParse(string? value, out IReadOnlyList<OpeningInterval>[] week)
    {
        week = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var days = new IReadOnlyList<OpeningInterval>[7];
        Array.Fill(days, Array.Empty<OpeningInterval>());
        var parsedRule = false;
        foreach (var rawRule in value.Split(';'))
        {
            var rule = rawRule.Trim();
            if (rule.Length == 0)
            {
                continue;
            }

            if (rule == "24/7")
            {
                Array.Fill(days, [new OpeningInterval(0, MinutesPerDay)]);
                parsedRule = true;
                continue;
            }

            Match match;
            try
            {
                match = RulePattern.Match(rule);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }

            if (!match.Success
                || !TryParseDays(match.Groups["days"], out var selectedDays)
                || !TryParseTimes(match.Groups["times"].Value, out var intervals))
            {
                return false;
            }

            foreach (var day in selectedDays)
            {
                days[day] = intervals;
            }

            parsedRule = true;
        }

        if (!parsedRule)
        {
            return false;
        }

        week = days;
        return true;
    }

    /// <summary>Renders one Markdown bullet per day, Monday through Sunday.</summary>
    internal static string FormatWeb(
        string escapedPlaceName,
        IReadOnlyList<OpeningInterval>[] week,
        string? timeZoneName)
    {
        var zone = string.IsNullOrWhiteSpace(timeZoneName) ? string.Empty : $" ({timeZoneName.Trim()})";
        var lines = week.Select((intervals, day) => $"- **{ShortDays[day]}:** {FormatDayWeb(intervals)}");
        return $"{escapedPlaceName}'s hours{zone}:\n\n" + string.Join("\n", lines);
    }

    /// <summary>Renders plain sentences that group days with identical hours.</summary>
    internal static string FormatVoice(string placeName, IReadOnlyList<OpeningInterval>[] week)
    {
        var groups = new List<(IReadOnlyList<OpeningInterval> Intervals, List<int> Days)>();
        for (var day = 0; day < week.Length; day++)
        {
            var existing = groups.FindIndex(group => group.Intervals.SequenceEqual(week[day]));
            if (existing < 0)
            {
                groups.Add((week[day], [day]));
            }
            else
            {
                groups[existing].Days.Add(day);
            }
        }

        var open = groups.Where(group => group.Intervals.Count > 0).ToArray();
        var closedDays = groups.Where(group => group.Intervals.Count == 0).SelectMany(group => group.Days).ToArray();
        if (open.Length == 0)
        {
            return $"{placeName} is listed as closed every day.";
        }

        if (open.Length == 1 && closedDays.Length == 0 && open[0].Intervals is [{ IsAllDay: true }])
        {
            return $"{placeName} is open 24 hours a day, every day.";
        }

        var phrases = new List<string>();
        IReadOnlyList<OpeningInterval>? previous = null;
        foreach (var (intervals, days) in open)
        {
            var dayText = days.Count == 7 ? "every day" : DescribeDays(days);
            string hours;
            if (intervals is [{ IsAllDay: true }])
            {
                hours = "24 hours";
            }
            else if (intervals is [var only] && previous is [var prior] && !prior.IsAllDay
                && prior.StartMinute == only.StartMinute)
            {
                hours = $"until {FormatTime(only.EndMinute)}";
            }
            else
            {
                hours = JoinList(intervals.Select(interval =>
                    $"from {FormatTime(interval.StartMinute)} to {FormatTime(interval.EndMinute)}").ToArray());
            }

            phrases.Add($"{dayText} {hours}");
            previous = intervals;
        }

        // Day lists already use "and", so separate schedules with a comma for clarity.
        var sentence = $"{placeName} is open {JoinList(phrases, serialComma: true)}.";
        return closedDays.Length == 0
            ? sentence
            : $"{sentence} It's closed {DescribeDays(closedDays)}.";
    }

    /// <summary>
    /// A short generic name such as "Eastern Time" when the host's time zone data provides one;
    /// otherwise null so callers omit the label rather than guess.
    /// </summary>
    internal static string? TimeZoneLabel(TimeZoneInfo zone)
    {
        const string standardSuffix = " Standard Time";
        var standard = zone.StandardName?.Trim();
        if (string.IsNullOrEmpty(standard) || standard.StartsWith("GMT", StringComparison.Ordinal)
            || standard.StartsWith("UTC", StringComparison.Ordinal) || standard.Contains('/'))
        {
            return null;
        }

        if (!zone.SupportsDaylightSavingTime)
        {
            return standard;
        }

        return standard.EndsWith(standardSuffix, StringComparison.Ordinal)
            ? standard[..^standardSuffix.Length] + " Time"
            : null;
    }

    private static bool TryParseDays(Group group, out IReadOnlyList<int> days)
    {
        if (!group.Success)
        {
            days = [0, 1, 2, 3, 4, 5, 6];
            return true;
        }

        var selected = new List<int>();
        foreach (var item in group.Value.Split(','))
        {
            var bounds = item.Trim().Split('-');
            var start = Array.IndexOf(OsmDays, bounds[0]);
            var end = bounds.Length == 2 ? Array.IndexOf(OsmDays, bounds[1]) : start;
            // Sa-Mo wraps through Sunday.
            for (var day = start; ; day = (day + 1) % 7)
            {
                if (!selected.Contains(day))
                {
                    selected.Add(day);
                }

                if (day == end)
                {
                    break;
                }
            }
        }

        days = selected;
        return true;
    }

    private static bool TryParseTimes(string value, out IReadOnlyList<OpeningInterval> intervals)
    {
        intervals = [];
        if (value is "off" or "closed")
        {
            return true;
        }

        var parsed = new List<OpeningInterval>();
        foreach (var item in value.Split(','))
        {
            var bounds = item.Trim().Split('-');
            if (!TryParseMinute(bounds[0], allowEndOfDay: false, out var start)
                || !TryParseMinute(bounds[1], allowEndOfDay: true, out var end)
                || start == end)
            {
                return false;
            }

            // An end at or before the start closes after midnight, e.g. 18:00-02:00.
            parsed.Add(new OpeningInterval(start, end < start ? end + MinutesPerDay : end));
        }

        intervals = parsed.OrderBy(interval => interval.StartMinute).ToArray();
        return true;
    }

    private static bool TryParseMinute(string value, bool allowEndOfDay, out int minute)
    {
        var hour = int.Parse(value.AsSpan(0, 2), CultureInfo.InvariantCulture);
        var minutes = int.Parse(value.AsSpan(3, 2), CultureInfo.InvariantCulture);
        minute = hour * 60 + minutes;
        return minutes < 60 && (hour < 24 || (allowEndOfDay && minute == MinutesPerDay));
    }

    private static string FormatDayWeb(IReadOnlyList<OpeningInterval> intervals) =>
        intervals switch
        {
            [] => "Closed",
            [{ IsAllDay: true }] => "Open 24 hours",
            _ => string.Join(", ", intervals.Select(interval =>
                $"{FormatTime(interval.StartMinute)}–{FormatTime(interval.EndMinute)}")),
        };

    // 12-hour clock; on-the-hour times omit minutes ("5 PM"), others keep them ("5:30 PM").
    private static string FormatTime(int minute)
    {
        minute %= MinutesPerDay;
        var hour24 = minute / 60;
        var minutes = minute % 60;
        var hour12 = hour24 % 12 == 0 ? 12 : hour24 % 12;
        var suffix = hour24 < 12 ? "AM" : "PM";
        return minutes == 0
            ? $"{hour12} {suffix}"
            : $"{hour12}:{minutes:00} {suffix}";
    }

    // Consecutive runs of three or more days read as "Monday through Wednesday".
    private static string DescribeDays(IReadOnlyList<int> days)
    {
        var sorted = days.Order().ToArray();
        var items = new List<string>();
        for (var index = 0; index < sorted.Length;)
        {
            var end = index;
            while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1)
            {
                end++;
            }

            if (end - index >= 2)
            {
                items.Add($"{LongDays[sorted[index]]} through {LongDays[sorted[end]]}");
            }
            else
            {
                for (var day = index; day <= end; day++)
                {
                    items.Add(LongDays[sorted[day]]);
                }
            }

            index = end + 1;
        }

        return JoinList(items);
    }

    private static string JoinList(IReadOnlyList<string> items, bool serialComma = false) =>
        items.Count switch
        {
            1 => items[0],
            2 => serialComma ? $"{items[0]}, and {items[1]}" : $"{items[0]} and {items[1]}",
            _ => string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1],
        };
}
