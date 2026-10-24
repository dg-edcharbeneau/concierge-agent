using System.Text.Json;
using System.Text.RegularExpressions;
using Concierge.Contracts;
using Microsoft.Extensions.AI;

namespace Concierge.WorkflowServer.AI;

/// <summary>
/// Pure validation for model-originated tool calls. Guest intent is the model's judgment; ride side
/// effects are gated by workflow-owned offers, not by matching the guest's wording.
/// </summary>
internal static class ConciergeToolCallPolicy
{
    private const string DayCountWords = "two|three|four|five|six|seven|couple(?: of)?|few|[2-7]";

    private static readonly Regex MultiDayWeatherPattern = new(
        $@"\b(?:(?:next|coming|following) (?<count>{DayCountWords}) days|(?<count>{DayCountWords})[- ]day (?:forecast|outlook)|(?<pair>today and tomorrow|tomorrow and today))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    private const string WeekdayNames = "sunday|monday|tuesday|wednesday|thursday|friday|saturday";

    // An explicit first day ("the next three days starting tomorrow" or "starting Friday").
    private static readonly Regex MultiDayWeatherStartPattern = new(
        $@"\b(?:starting|beginning|from) (?:on )?(?:this )?(?<start>today|tomorrow|{WeekdayNames})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    // A first day the policy cannot map ("starting next Friday", "starting the 25th"). The model's
    // own calls run instead of being rewritten to start today.
    private static readonly Regex UnmappedWeatherStartPattern = new(
        @"\b(?:starting|beginning)\b|\bfrom (?:on )?(?:next|the)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex WeatherDayOffsetPattern = new(
        @"^day\+(?<offset>[2-6])$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    // A bare weekday is the model-facing form; "{weekday}+n" is workflow-generated.
    private static readonly Regex WeatherWeekdayPeriodPattern = new(
        $@"^(?<weekday>{WeekdayNames})(?:\+(?<offset>[0-6]))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex WeatherWeekendPeriodPattern = new(
        @"^weekend\+(?<offset>[01])$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromMilliseconds(100));

    // "this weekend", "the weekend", "weekend forecast". A different weekend or a named weekday
    // is left to the model's own calls.
    private static readonly Regex WeekendPattern = new(
        @"\bweekend\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex OtherWeekendPattern = new(
        $@"\b(?:next|last|following|previous|past)\s+weekend\b|\b(?:{WeekdayNames})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex WeatherWordPattern = new(
        @"\b(?:weather|forecast|rain\w*|temperature|sunny|storm\w*|humid\w*|wind\w*|hot|cold|warm)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    // Phrases that name the hotel or a time span; whatever preposition remains names a place.
    private static readonly Regex HotelPlacePattern = new(
        @"\b(?:at|in|around|near|for)\s+(?:the\s+hotel|hotel\s+zgy|zgy)\b|\b(?:in|over|for|during|of)\s+(?:this|the)\s+(?:weekend|next|coming|following)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex PlacePrepositionPattern = new(
        @"\b(?:in|at|near|around|for|of)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Days to fetch: <see cref="StartOffset"/> days after hotel-local today, or, when
    /// <see cref="StartWeekday"/> is set, from that weekday's next occurrence (today included). The
    /// workflow does not hold the hotel calendar, so the weather activity resolves weekdays.
    /// </summary>
    /// <remarks><see cref="Weekend"/> fetches this weekend's Saturday and Sunday, resolved by the activity.</remarks>
    internal readonly record struct MultiDayWeather(int StartOffset, DayOfWeek? StartWeekday, int Days, bool Weekend = false);

    /// <summary>
    /// The weather activity serves one day. For "the next N days" the workflow decides which days
    /// to fetch instead of trusting the model's calls: live runs showed the model sometimes emits
    /// one call, or today/tonight/tomorrow for "the next 3 days", and schema hints regressed other
    /// tools. Rewrites only when every call is GetWeather for the same place.
    /// </summary>
    internal static bool TryGetMultiDayWeather(
        IReadOnlyList<FunctionCallContent> calls,
        string userMessage,
        out MultiDayWeather plan)
    {
        plan = default;
        if (calls.Count is < 1 or > MaxParallelWeatherCalls
            || calls.Any(call => !StringComparer.Ordinal.Equals(call.Name, ConciergeToolDefinitions.GetWeather)
                || !IsSafeFunctionCallId(call.CallId)
                || call.CallId.Length > 100
                || call.Arguments is null
                || !TryOptionalString(call.Arguments, "place", 200, out _)))
        {
            return false;
        }

        _ = TryOptionalString(calls[0].Arguments!, "place", 200, out var place);
        if (calls.Any(call => TryOptionalString(call.Arguments!, "place", 200, out var other)
                && !string.Equals(other?.Trim(), place?.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return TryGetWeatherPlan(userMessage, out plan);
    }

    /// <summary>
    /// The model sometimes answers a multi-day or weekend weather question from earlier turns
    /// without calling GetWeather (live 2026-10-22). When the guest names no other place, the
    /// workflow makes the calls itself; with a named place it cannot know which, so it does not.
    /// </summary>
    internal static bool TrySynthesizeWeatherCalls(
        string userMessage,
        string callIdPrefix,
        out FunctionCallContent[] calls)
    {
        calls = [];
        var utterance = NormalizeUtterance(userMessage);
        if (!WeatherWordPattern.IsMatch(utterance)
            || PlacePrepositionPattern.IsMatch(HotelPlacePattern.Replace(utterance, " "))
            || !TryGetWeatherPlan(userMessage, out var plan))
        {
            return false;
        }

        calls = ExpandToMultiDayWeather(
            [
                new FunctionCallContent(
                    callIdPrefix,
                    ConciergeToolDefinitions.GetWeather,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["place"] = null, ["period"] = "today" }),
            ],
            plan);
        return true;
    }

    private static bool TryGetWeatherPlan(string userMessage, out MultiDayWeather plan)
    {
        plan = default;
        var utterance = NormalizeUtterance(userMessage);
        var match = MultiDayWeatherPattern.Match(utterance);
        if (!match.Success)
        {
            if (WeekendPattern.IsMatch(utterance) && !OtherWeekendPattern.IsMatch(utterance))
            {
                plan = new(0, null, 2, Weekend: true);
                return true;
            }

            return false;
        }

        var days = match.Groups["pair"].Success
            ? 2
            : match.Groups["count"].Value.ToLowerInvariant() switch
            {
                "two" or "couple" or "couple of" => 2,
                "three" or "few" => 3,
                "four" => 4,
                "five" => 5,
                "six" => 6,
                "seven" => 7,
                var digits => int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture),
            };

        // "Today and tomorrow" names its own days. Otherwise keep an explicit first day, and clamp
        // the span to the forecast horizon (day+6) rather than shifting it back to today. Weekday
        // spans are not clamped here; the activity reports days beyond the horizon.
        if (match.Groups["pair"].Success)
        {
            plan = new(0, null, days);
            return true;
        }

        var start = MultiDayWeatherStartPattern.Match(utterance);
        if (!start.Success)
        {
            if (UnmappedWeatherStartPattern.IsMatch(utterance))
            {
                return false;
            }

            plan = new(0, null, days);
            return true;
        }

        var startDay = start.Groups["start"].Value.ToLowerInvariant();
        if (startDay is "today" or "tomorrow")
        {
            var startOffset = startDay == "tomorrow" ? 1 : 0;
            plan = new(startOffset, null, Math.Min(days, Location.OpenMeteoWeatherService.MaxDaysAhead + 1 - startOffset));
            return true;
        }

        plan = new(0, Enum.Parse<DayOfWeek>(startDay, ignoreCase: true), days);
        return true;
    }

    internal const int MaxParallelWeatherCalls = Location.OpenMeteoWeatherService.MaxDaysAhead + 1;

    internal static FunctionCallContent[] ExpandToMultiDayWeather(
        IReadOnlyList<FunctionCallContent> calls,
        MultiDayWeather plan)
    {
        var first = calls[0];
        _ = TryOptionalString(first.Arguments!, "place", 200, out var place);
        return Enumerable.Range(plan.StartOffset, plan.Days)
            .Select(offset => new FunctionCallContent(
                $"{first.CallId}-d{offset}",
                first.Name,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["place"] = place,
                    ["period"] = plan.Weekend
                        ? $"weekend+{offset}"
                        : plan.StartWeekday is { } weekday
                            ? WeatherPeriodForWeekday(weekday, offset)
                            : WeatherPeriodForOffset(offset),
                }))
            .ToArray();
    }

    internal static string WeatherPeriodForWeekday(DayOfWeek weekday, int offset) =>
        $"{weekday.ToString().ToLowerInvariant()}+{offset}";

    /// <summary>
    /// A weekday name (the model-facing form, any case) or the workflow-generated "{weekday}+{n}":
    /// n days after the weekday's next occurrence, today included.
    /// </summary>
    internal static bool TryParseWeatherWeekdayPeriod(string? period, out DayOfWeek weekday, out int offset)
    {
        weekday = default;
        offset = 0;
        var match = period is null ? Match.Empty : WeatherWeekdayPeriodPattern.Match(period);
        if (!match.Success)
        {
            return false;
        }

        weekday = Enum.Parse<DayOfWeek>(match.Groups["weekday"].Value, ignoreCase: true);
        offset = match.Groups["offset"].Success ? match.Groups["offset"].Value[0] - '0' : 0;
        return true;
    }

    /// <summary>
    /// Workflow-generated "weekend+0" (Saturday) and "weekend+1" (Sunday) of the current or coming
    /// weekend. Not advertised to the model.
    /// </summary>
    internal static bool TryParseWeatherWeekendPeriod(string? period, out int offset)
    {
        offset = 0;
        var match = period is null ? Match.Empty : WeatherWeekendPeriodPattern.Match(period);
        if (!match.Success)
        {
            return false;
        }

        offset = match.Groups["offset"].Value[0] - '0';
        return true;
    }

    internal static string WeatherPeriodForOffset(int offset) => offset switch
    {
        0 => "today",
        1 => "tomorrow",
        _ => $"day+{offset}",
    };

    /// <summary>Workflow-generated periods for days 2-6; not advertised to the model.</summary>
    internal static bool TryParseWeatherDayOffset(string? period, out int offset)
    {
        offset = 0;
        var match = period is null ? Match.Empty : WeatherDayOffsetPattern.Match(period);
        if (!match.Success)
        {
            return false;
        }

        offset = match.Groups["offset"].Value[0] - '0';
        return true;
    }

    internal static bool TryValidateArguments(
        string name,
        IDictionary<string, object?> arguments,
        out string? error)
    {
        error = null;
        switch (name)
        {
            case ConciergeToolDefinitions.GetWeather:
                if (!HasOnlyKeys(arguments, "place", "period")
                    || !TryOptionalString(arguments, "place", 200, out _)
                    || !TryOptionalString(arguments, "period", 16, out var period)
                    || period is not (null or "today" or "tonight" or "tomorrow")
                        && !TryParseWeatherDayOffset(period, out _)
                        && !TryParseWeatherWeekdayPeriod(period, out _, out _)
                        && !TryParseWeatherWeekendPeriod(period, out _))
                {
                    error = "I couldn't safely read the requested weather period or place. Please clarify.";
                    return false;
                }
                return true;

            case ConciergeToolDefinitions.GetRoute:
                if (!HasOnlyKeys(arguments, "places")
                    || !TryStringArray(arguments, "places", 1, 2, 200, out _))
                {
                    error = "I couldn't safely read those route locations. Please clarify the origin and destination.";
                    return false;
                }
                return true;

            case ConciergeToolDefinitions.FindNearbyPlaces:
                if (!HasOnlyKeys(arguments, "category")
                    || !TryString(arguments, "category", 32, out var category)
                    || !Location.NearbyPlaceCategories.TryGet(category, out _))
                {
                    error = "I can't search for that kind of place. I can find nearby pharmacies, grocery stores, restaurants, cafes, bars, ATMs, banks, hospitals, clinics, gas stations, shopping malls, museums, parks, beaches, attractions, and post offices.";
                    return false;
                }
                return true;

            case ConciergeToolDefinitions.GetPlaceOpeningHours:
                if (!HasOnlyKeys(arguments, "placeName")
                    || !TryString(arguments, "placeName", 200, out _))
                {
                    error = "Please provide the name of one place whose opening hours you want.";
                    return false;
                }
                return true;

            case ConciergeToolDefinitions.ProposeRide:
                if (!HasOnlyKeys(arguments, "pickup", "dropoff", "immediate", "pickupAt")
                    || !TryOptionalString(arguments, "pickup", 200, out _)
                    || !TryString(arguments, "dropoff", 200, out _)
                    || !TryBoolean(arguments, "immediate", out var immediate)
                    || !TryOptionalString(arguments, "pickupAt", 64, out var pickupAt)
                    || immediate == (pickupAt is not null))
                {
                    error = "I couldn't safely read the ride locations or timing. Please clarify them.";
                    return false;
                }
                return true;

            case ConciergeToolDefinitions.ConfirmRide:
                if (arguments.Count != 0)
                {
                    error = "No ride was booked. A ride confirmation must not include arguments.";
                    return false;
                }
                return true;

            case ConciergeToolDefinitions.ProposeRideCancellation:
                if (!HasOnlyKeys(arguments, "rideId")
                    || !TryString(arguments, "rideId", 36, out var proposedRideId)
                    || !Guid.TryParse(proposedRideId, out var parsedProposedRideId)
                    || parsedProposedRideId == Guid.Empty)
                {
                    error = "I couldn't safely identify the ride to offer for cancellation.";
                    return false;
                }
                return true;

            case ConciergeToolDefinitions.ConfirmRideCancellation:
                if (arguments.Count != 0)
                {
                    error = "I couldn't safely match the cancellation to the pending offer.";
                    return false;
                }
                return true;

            case ConciergeToolDefinitions.GetRideStatus:
                if (!HasOnlyKeys(arguments, "rideId")
                    || !TryString(arguments, "rideId", 36, out var rideId)
                    || !Guid.TryParse(rideId, out var parsedRideId)
                    || parsedRideId == Guid.Empty)
                {
                    error = "I couldn't safely identify that ride.";
                    return false;
                }
                return true;

            case ConciergeToolDefinitions.GetRideHistory:
                if (!HasOnlyKeys(arguments, "when", "purpose")
                    || !TryString(arguments, "when", 16, out var when)
                    || when is not ("past" or "upcoming")
                    || !TryOptionalString(arguments, "purpose", 16, out var purpose)
                    || purpose is not (null or "show" or "select"))
                {
                    error = "Please specify whether you want past or upcoming rides.";
                    return false;
                }
                return true;

            default:
                error = "I couldn't safely process that unsupported action.";
                return false;
        }
    }

    internal static bool IsRideTool(string name) =>
        name is ConciergeToolDefinitions.ProposeRide
            or ConciergeToolDefinitions.ConfirmRide
            or ConciergeToolDefinitions.GetRideStatus
            or ConciergeToolDefinitions.GetRideHistory
            or ConciergeToolDefinitions.ProposeRideCancellation
            or ConciergeToolDefinitions.ConfirmRideCancellation;

    internal static bool HasTrustedRideIdentity(ConciergeVerifiedGuest? guest) =>
        guest is { GuestId: var guestId, StayId: var stayId, CheckoutDate: var checkoutDate }
        && guestId != Guid.Empty
        && stayId != Guid.Empty
        && checkoutDate != default;

    private static string NormalizeUtterance(string message) =>
        string.Join(
            ' ',
            new string(message.Trim()
                .Select(character => char.IsPunctuation(character) ? ' ' : char.ToLowerInvariant(character))
                .ToArray())
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static bool IsSafeFunctionCallId(string? callId) =>
        !string.IsNullOrWhiteSpace(callId)
        && callId.Length <= 128
        && callId.All(character =>
            character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '_' or '-');

    internal static bool ArgumentsFit(IDictionary<string, object?> arguments)
    {
        try
        {
            return System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(arguments)) <= 2_000;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool HasOnlyKeys(IDictionary<string, object?> arguments, params string[] allowed) =>
        arguments.Keys.All(key => allowed.Contains(key, StringComparer.Ordinal));

    private static bool TryString(
        IDictionary<string, object?> arguments,
        string key,
        int maximumLength,
        out string value)
    {
        value = string.Empty;
        return arguments.TryGetValue(key, out var raw)
            && TryReadString(raw, out value)
            && !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximumLength;
    }

    internal static bool TryOptionalString(
        IDictionary<string, object?> arguments,
        string key,
        int maximumLength,
        out string? value)
    {
        value = null;
        if (!arguments.TryGetValue(key, out var raw) || raw is null
            || raw is JsonElement { ValueKind: JsonValueKind.Null })
        {
            return true;
        }

        if (!TryReadString(raw, out var text) || text.Length > maximumLength)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryReadString(object? value, out string result)
    {
        result = string.Empty;
        switch (value)
        {
            case string text:
                result = text;
                return true;
            case JsonElement { ValueKind: JsonValueKind.String } element:
                result = element.GetString() ?? string.Empty;
                return true;
            default:
                return false;
        }
    }

    private static bool TryBoolean(IDictionary<string, object?> arguments, string key, out bool result)
    {
        result = false;
        if (!arguments.TryGetValue(key, out var raw))
        {
            return false;
        }

        if (raw is bool boolean)
        {
            result = boolean;
            return true;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.True })
        {
            result = true;
            return true;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.False })
        {
            result = false;
            return true;
        }

        return false;
    }

    internal static bool TryStringArray(
        IDictionary<string, object?> arguments,
        string key,
        int minimumCount,
        int maximumCount,
        int maxItemLength,
        out string[] values)
    {
        values = [];
        if (!arguments.TryGetValue(key, out var raw))
        {
            return false;
        }

        if (raw is string[] array)
        {
            values = array;
        }
        else if (raw is IEnumerable<string> sequence)
        {
            values = sequence.ToArray();
        }
        else if (raw is JsonElement { ValueKind: JsonValueKind.Array } element)
        {
            var items = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (!TryReadString(item, out var text))
                {
                    return false;
                }
                items.Add(text);
            }
            values = items.ToArray();
        }
        else
        {
            return false;
        }

        return values.Length >= minimumCount
            && values.Length <= maximumCount
            && values.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= maxItemLength);
    }
}
