using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Concierge.Contracts;
using Concierge.WorkflowServer.Location;
using Microsoft.Extensions.AI;

namespace Concierge.WorkflowServer.AI;

/// <summary>Decodes one tool result and renders its bounded model context without workflow state.</summary>
internal static class ConciergeToolResultInterpreter
{
    private const int MaxCompactToolResultUtf8Bytes = 2_048;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed record InterpretedResult(
        string CompactModelContext,
        string GuestFacingText,
        ConciergeRideProposal? ProposedStateChange,
        ConciergeRideCancellationProposal? ProposedCancellationStateChange);

    internal sealed record Interpretation(
        string ToolName,
        object? Decoded,
        bool JsonDecodeFailed,
        bool ShouldReturnRenderedToolAnswer,
        ConciergeRideProposal? ProposedRideProposal,
        ConciergeRideCancellationProposal? ProposedRideCancellationProposal)
    {
        internal InterpretedResult Interpret(
            ConciergeTurnSubmission request,
            IDictionary<string, object?> arguments,
            bool includeRideIdsForCancellation)
        {
            if (JsonDecodeFailed)
            {
                throw new JsonException("The serialized tool result could not be decoded.");
            }

            var context = ToolName switch
            {
                ConciergeToolDefinitions.GetWeather =>
                    FormatWeather((ConciergeWeatherToolResult?)Decoded, request, arguments),
                ConciergeToolDefinitions.GetRoute =>
                    FormatRoute((ConciergeRouteToolResult?)Decoded, request, arguments),
                ConciergeToolDefinitions.FindNearbyPlaces =>
                    FormatNearbyPlaces((ConciergeNearbyPlacesToolResult?)Decoded, request),
                ConciergeToolDefinitions.GetPlaceOpeningHours =>
                    FormatPlaceOpeningHours((ConciergePlaceOpeningHoursToolResult?)Decoded, arguments),
                ConciergeToolDefinitions.ProposeRide =>
                    FormatRideProposal(
                        (ConciergeRideProposalToolResult?)Decoded,
                        arguments),
                _ => FormatRideAction(
                    ToolName,
                    (ConciergeRideActionResult?)Decoded,
                    request,
                    arguments,
                    includeRideIdsForCancellation),
            };
            context = LimitUtf8(context, MaxCompactToolResultUtf8Bytes);
            return new InterpretedResult(
                context,
                context,
                ProposedRideProposal,
                ProposedRideCancellationProposal);
        }
    }

    internal static Interpretation Decode(string name, string serialized)
    {
        object? decoded;
        var jsonDecodeFailed = false;
        try
        {
            decoded = name switch
            {
                ConciergeToolDefinitions.GetWeather =>
                    JsonSerializer.Deserialize<ConciergeWeatherToolResult>(serialized, JsonOptions),
                ConciergeToolDefinitions.GetRoute =>
                    JsonSerializer.Deserialize<ConciergeRouteToolResult>(serialized, JsonOptions),
                ConciergeToolDefinitions.FindNearbyPlaces =>
                    JsonSerializer.Deserialize<ConciergeNearbyPlacesToolResult>(serialized, JsonOptions),
                ConciergeToolDefinitions.GetPlaceOpeningHours =>
                    JsonSerializer.Deserialize<ConciergePlaceOpeningHoursToolResult>(serialized, JsonOptions),
                ConciergeToolDefinitions.ProposeRide =>
                    JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(serialized, JsonOptions),
                ConciergeToolDefinitions.ConfirmRide
                    or ConciergeToolDefinitions.GetRideStatus
                    or ConciergeToolDefinitions.GetRideHistory
                    or ConciergeToolDefinitions.ProposeRideCancellation
                    or ConciergeToolDefinitions.ConfirmRideCancellation =>
                    JsonSerializer.Deserialize<ConciergeRideActionResult>(serialized, JsonOptions),
                _ => null,
            };
        }
        catch (JsonException)
        {
            // Interpret() rethrows, so the workflow decides how to report an undecodable result.
            decoded = null;
            jsonDecodeFailed = true;
        }
        var shouldRender = name switch
        {
            ConciergeToolDefinitions.GetWeather =>
                (decoded as ConciergeWeatherToolResult)?.Status is "Available" or "BeyondForecast" or "Elapsed",
            ConciergeToolDefinitions.GetRoute =>
                (decoded as ConciergeRouteToolResult)?.Status == "Available",
            ConciergeToolDefinitions.FindNearbyPlaces =>
                decoded is ConciergeNearbyPlacesToolResult { Status: "Available" or "NoResults" } nearby
                && NearbyPlaceCategories.TryGet(nearby.Category, out _),
            ConciergeToolDefinitions.GetPlaceOpeningHours =>
                decoded is ConciergePlaceOpeningHoursToolResult
                {
                    Status: "Available" or "NotListed" or "NoResults" or "NeedsClarification",
                },
            ConciergeToolDefinitions.ProposeRide => true,
            ConciergeToolDefinitions.ConfirmRide =>
                (decoded as ConciergeRideActionResult)?.Ride is not null,
            ConciergeToolDefinitions.GetRideStatus =>
                (decoded as ConciergeRideActionResult) is { Ride: not null } or { Message: not null },
            ConciergeToolDefinitions.GetRideHistory =>
                decoded is ConciergeRideActionResult,
            ConciergeToolDefinitions.ProposeRideCancellation =>
                decoded is ConciergeRideActionResult cancellationResult
                && (cancellationResult.Ride is not null || cancellationResult.Message is not null),
            ConciergeToolDefinitions.ConfirmRideCancellation =>
                decoded is ConciergeRideActionResult,
            _ => false,
        };
        return new Interpretation(
            name,
            decoded,
            jsonDecodeFailed,
            shouldRender,
            null,
            null);
    }

    internal static Interpretation WithProposalSequence(
        Interpretation interpretation,
        string turnId,
        long sequence)
    {
        if (StringComparer.Ordinal.Equals(
                interpretation.ToolName,
                ConciergeToolDefinitions.ProposeRideCancellation)
            && interpretation.Decoded is ConciergeRideActionResult
            {
                Message: null,
                Ride: { Status: "Booked" or "DriverAssigned" } ride,
            })
        {
            return interpretation with
            {
                ProposedRideCancellationProposal = new ConciergeRideCancellationProposal(
                    ride.Id,
                    sequence,
                    ride.Status,
                    ride.Pickup,
                    ride.Dropoff,
                    ride.PickupAt),
            };
        }

        if (interpretation.Decoded is not ConciergeRideProposalToolResult
            {
                Status: "Available",
                Proposal: { } draft,
            })
        {
            return interpretation;
        }

        var proposalId = CreateProposalId(turnId, sequence, draft);
        return interpretation with
        {
            ProposedRideProposal = new ConciergeRideProposal(
                proposalId,
                sequence,
                draft.Pickup,
                draft.Dropoff,
                draft.Immediate,
                draft.PickupAtUtc,
                draft.RouteDurationSeconds),
        };
    }

    internal static string Limit(string value, int maximumBytes) => LimitUtf8(value, maximumBytes);

    private static string FormatWeather(
        ConciergeWeatherToolResult? weather,
        ConciergeTurnSubmission request,
        IDictionary<string, object?> arguments)
    {
        if (weather is null)
        {
            return ToolFailureReply(ConciergeToolDefinitions.GetWeather);
        }

        if (StringComparer.Ordinal.Equals(weather.Status, "BeyondForecast"))
        {
            var day = weather.LocalDate is { } beyond
                ? beyond.ToString("dddd, MMM dd", CultureInfo.InvariantCulture)
                : "that day";
            return $"The forecast for {day} isn't available yet; forecasts reach {OpenMeteoWeatherService.MaxDaysAhead} days ahead.";
        }

        // Only Saturday, asked on Sunday, can have passed.
        if (StringComparer.Ordinal.Equals(weather.Status, "Elapsed"))
        {
            var day = weather.LocalDate?.ToString("dddd", CultureInfo.InvariantCulture) ?? "Saturday";
            return $"{day} has passed; today is the remaining weekend day.";
        }

        if (!StringComparer.Ordinal.Equals(weather.Status, "Available"))
        {
            return weather.Candidates.Count == 0
                ? $"I couldn't identify {weather.ClarificationQuery ?? "that place"}. Ask the guest to clarify; do not guess."
                : $"I found several possible matches for {weather.ClarificationQuery}: {string.Join("; ", weather.Candidates.Take(4))}. Ask the guest which one they mean.";
        }

        _ = ConciergeToolCallPolicy.TryOptionalString(arguments, "period", 16, out var period);
        period ??= "today";
        if (ConciergeToolCallPolicy.TryParseWeatherDayOffset(period, out _)
            || ConciergeToolCallPolicy.TryParseWeatherWeekdayPeriod(period, out _, out _)
            || ConciergeToolCallPolicy.TryParseWeatherWeekendPeriod(period, out _))
        {
            period = weather.LocalDate is { } localDate
                ? $"on {localDate.ToString("dddd", CultureInfo.InvariantCulture)}"
                : "later this week";
        }

        var conditions = (string.IsNullOrWhiteSpace(weather.Conditions)
            ? "mixed conditions"
            : weather.Conditions).Trim().TrimEnd('.');
        var lead = conditions.Equals("clear sky", StringComparison.OrdinalIgnoreCase)
            ? "Expect clear skies"
            : $"Expect {char.ToLowerInvariant(conditions[0])}{conditions[1..]}";
        var location = ShortPlaceName(weather.Location ?? "the area");
        var parts = new List<string>
        {
            $"{lead} {period} {(location == "Hotel ZGY" ? "at" : "in")} {location}",
        };
        if (weather.HighFahrenheit is { } highF)
        {
            parts.Add($"a high around {Math.Round(highF, MidpointRounding.AwayFromZero):0}°F");
        }

        if (weather.LowFahrenheit is { } lowF)
        {
            parts.Add($"a low near {Math.Round(lowF, MidpointRounding.AwayFromZero):0}°F");
        }

        var sentence = parts.Count == 1
            ? parts[0] + "."
            : $"{parts[0]}, with {string.Join(" and ", parts.Skip(1))}.";
        var additions = new List<string>();
        if (weather.PrecipitationProbability is { } probability)
        {
            additions.Add($"There's about a {probability}% chance of precipitation");
        }

        if (weather.MaxWindMph is { } windMph)
        {
            additions.Add($"winds could reach {Math.Round(windMph, MidpointRounding.AwayFromZero):0} mph");
        }

        if (additions.Count > 0)
        {
            sentence += " " + string.Join(", and ", additions) + ".";
        }

        return StringComparer.Ordinal.Equals(request.Channel, "web")
            ? $"![{conditions}](/images/weather/{WeatherIconKey(conditions)}.svg)\n\n{sentence}"
            : sentence;
    }

    private static string FormatRoute(
        ConciergeRouteToolResult? route,
        ConciergeTurnSubmission request,
        IDictionary<string, object?> arguments)
    {
        if (route is null)
        {
            return ToolFailureReply(ConciergeToolDefinitions.GetRoute);
        }

        if (!StringComparer.Ordinal.Equals(route.Status, "Available"))
        {
            return route.Candidates.Count == 0
                ? $"I couldn't identify {route.ClarificationQuery ?? "that place"}. Ask the guest to clarify; do not guess."
                : $"I found several possible route matches for {route.ClarificationQuery}: {string.Join("; ", route.Candidates.Take(4))}. Ask the guest which one they mean.";
        }

        _ = ConciergeToolCallPolicy.TryStringArray(arguments, "places", 1, 2, 200, out var requestedPlaces);
        var origin = requestedPlaces.Length > 1
            ? GuestPlaceName(route.Origin?.Name, requestedPlaces[0])
            : ShortPlaceName(route.Origin?.Name ?? "Hotel ZGY");
        var destination = requestedPlaces.Length > 0
            ? GuestPlaceName(route.Destination?.Name, requestedPlaces[^1])
            : ShortPlaceName(route.Destination?.Name ?? route.ClarificationQuery ?? "your destination");
        var distance = route.DistanceMeters is { } meters
            ? $"about {Math.Round(meters / 1609.344, MidpointRounding.AwayFromZero):0} miles away"
            : null;
        var duration = route.EstimatedDurationSeconds is { } seconds
            ? $"roughly {Math.Max(1, (int)Math.Ceiling(seconds / 60))} minutes by car"
            : null;
        var details = string.Join(", ", new[] { distance, duration }.Where(value => value is not null));
        var summary = details.Length == 0
            ? $"Driving directions from {origin} to {destination} are ready."
            : $"{destination} is {details} from {origin}.";
        var instructions = StringComparer.Ordinal.Equals(request.Channel, "web")
            ? route.WebInstructions.Take(3).Select(step => step.Trim()).ToArray()
            : route.VoiceInstructions.Take(1).ToArray();
        var steps = instructions.Length == 0
            ? string.Empty
            : StringComparer.Ordinal.Equals(request.Channel, "web")
                ? "\n\n" + string.Join("\n", instructions.Select((step, index) =>
                    $"{index + 1}. {EnsureTerminalPunctuation(step.Trim())}"))
                : $" {EnsureTerminalPunctuation(instructions[0].Trim())}";
        return summary + steps;
    }

    private static string FormatNearbyPlaces(
        ConciergeNearbyPlacesToolResult? result,
        ConciergeTurnSubmission request)
    {
        if (result is null
            || !NearbyPlaceCategories.TryGet(result.Category, out var category)
            || result.Status is not ("Available" or "NoResults"))
        {
            return ToolFailureReply(ConciergeToolDefinitions.FindNearbyPlaces);
        }

        var places = (result.Places ?? [])
            .Where(place => !string.IsNullOrWhiteSpace(place.Name)
                && double.IsFinite(place.DistanceMiles) && place.DistanceMiles >= 0)
            .OrderBy(place => place.DistanceMiles)
            .Take(GeoapifyNearbyPlacesService.MaxResults)
            .ToArray();
        if (places.Length == 0)
        {
            return $"I couldn't find {IndefiniteArticle(category.Singular)} {category.Singular} within about five miles of Hotel ZGY. The front desk may be able to suggest one.";
        }

        if (StringComparer.Ordinal.Equals(request.Channel, "web"))
        {
            var heading = places.Length == 1
                ? $"Here is the closest {category.Singular} to Hotel ZGY:"
                : $"Here are the closest {category.Plural} to Hotel ZGY:";
            return heading + "\n\n" + string.Join("\n", places.Select((place, index) =>
                $"{index + 1}. **{EscapeMarkdown(place.Name)}**"
                + (place.Address is null ? string.Empty : $", {EscapeMarkdown(place.Address)}")
                + $" ({FormatMiles(place.DistanceMiles)})"));
        }

        var closest = places[0];
        var sentence = $"The closest {category.Singular} is {closest.Name}"
            + (closest.Address is null ? string.Empty : $" at {closest.Address}")
            + $", {FormatMiles(closest.DistanceMiles)} from Hotel ZGY.";
        var others = places.Skip(1)
            .Select(place => $"{place.Name}, {FormatMiles(place.DistanceMiles)} away")
            .ToArray();
        return others.Length switch
        {
            0 => sentence,
            1 => $"{sentence} Another option is {others[0]}.",
            _ => $"{sentence} Other options are {string.Join(" and ", others)}.",
        };
    }

    private static string FormatPlaceOpeningHours(
        ConciergePlaceOpeningHoursToolResult? result,
        IDictionary<string, object?> arguments)
    {
        if (result is null)
        {
            return ToolFailureReply(ConciergeToolDefinitions.GetPlaceOpeningHours);
        }

        _ = ConciergeToolCallPolicy.TryOptionalString(arguments, "placeName", 200, out var requestedName);
        requestedName = string.IsNullOrWhiteSpace(requestedName) ? "that place" : requestedName.Trim();
        switch (result.Status)
        {
            case "Available" when !string.IsNullOrWhiteSpace(result.PlaceName)
                && !string.IsNullOrWhiteSpace(result.OpeningHours):
                return $"{result.PlaceName}'s listed opening hours are {result.OpeningHours}.";
            case "NotListed" when !string.IsNullOrWhiteSpace(result.PlaceName):
                return $"Opening hours for {result.PlaceName} are not listed.";
            case "NoResults":
                return $"I couldn't find {requestedName} near Hotel ZGY. Please check the place name.";
            case "NeedsClarification":
                return $"I found more than one place matching {requestedName} near Hotel ZGY. Which location do you mean?";
            default:
                return ToolFailureReply(ConciergeToolDefinitions.GetPlaceOpeningHours);
        }
    }

    private static string FormatMiles(double miles) =>
        miles < 0.1
            ? "less than 0.1 miles"
            : $"{miles.ToString("0.0", CultureInfo.InvariantCulture)} miles";

    private static string IndefiniteArticle(string noun) =>
        noun.Length > 0 && "aeiouAEIOU".Contains(noun[0]) ? "an" : "a";

    private static string EscapeMarkdown(string value) =>
        Regex.Replace(value, @"([\\`*_\[\]()#!<>|~])", @"\$1");

    private static string FormatRideProposal(
        ConciergeRideProposalToolResult? result,
        IDictionary<string, object?> arguments)
    {
        if (result is null)
        {
            return ToolFailureReply(ConciergeToolDefinitions.ProposeRide);
        }

        if (!StringComparer.Ordinal.Equals(result.Status, "Available")
            || result.Proposal is not { } draft)
        {
            if (StringComparer.Ordinal.Equals(result.Status, "NeedsClarification"))
            {
                return result.Candidates.Count == 0
                    ? "I couldn't identify the destination. Please clarify it. No ride has been booked."
                    : $"Which destination do you mean: {string.Join("; ", result.Candidates.Take(4))}? No ride has been booked.";
            }

            if (StringComparer.Ordinal.Equals(result.Status, "InvalidSchedule"))
            {
                // Only the activity's structured policy value is rendered, never provider text
                // or model arguments. Recording it in the result keeps workflow replay deterministic.
                return result.MinimumAdvanceNoticeMinutes is > 0
                    ? $"A scheduled pickup must be at least {result.MinimumAdvanceNoticeMinutes} minutes from now. Please choose a later time. No ride has been booked."
                    : "The scheduled pickup is too soon or has an invalid time. Please choose a later time. No ride has been booked.";
            }

            return ToolFailureReply(ConciergeToolDefinitions.ProposeRide);
        }

        var when = draft.Immediate || draft.PickupAtUtc is not { } pickupAtUtc
            ? "now"
            : $"on {FormatPickupTime(pickupAtUtc, arguments)}";
        var minutes = Math.Max(1, (int)Math.Ceiling(draft.RouteDurationSeconds / 60));
        var article = minutes is 8 or 11 or 18 or (>= 80 and <= 89) ? "an" : "a";
        return $"I can have a car pick you up at {ShortPlaceName(draft.Pickup.Name)} {when} and take you to {ShortPlaceName(draft.Dropoff.Name)}. It's about {article} {minutes}-minute ride and is complimentary for current hotel guests. No ride has been booked.";
    }

    private static string FormatRideAction(
        string name,
        ConciergeRideActionResult? action,
        ConciergeTurnSubmission request,
        IDictionary<string, object?> arguments,
        bool includeRideIdsForCancellation)
    {
        if (action is null)
        {
            return ToolFailureReply(name);
        }

        if (StringComparer.Ordinal.Equals(name, ConciergeToolDefinitions.ConfirmRide))
        {
            if (action.Ride is { } booked)
            {
                return $"You're all set. A car will pick you up at {ShortPlaceName(booked.Pickup)} on {FormatRideTime(booked.PickupAt)} and take you to {ShortPlaceName(booked.Dropoff)}.";
            }

            return action.Message
                ?? "The ride service did not confirm the outcome. Check your ride history before trying again.";
        }

        if (StringComparer.Ordinal.Equals(name, ConciergeToolDefinitions.GetRideStatus))
        {
            if (action.Ride is { } status)
            {
                return FormatRideStatus(status);
            }

            return action.Message ?? "Ride status is unavailable.";
        }

        if (StringComparer.Ordinal.Equals(name, ConciergeToolDefinitions.GetRideHistory))
        {
            var rides = action.Rides ?? [];
            // A failed lookup also has no rides; reporting "no rides" would misstate an outage.
            if (action.Message is { } historyError && rides.Count == 0)
            {
                return historyError;
            }

            if (includeRideIdsForCancellation)
            {
                return rides.Count == 0
                    ? "No upcoming rides were found."
                    : "Upcoming rides for cancellation: " + string.Join(
                        "; ",
                        rides.Take(10).Select(ride =>
                            $"{ride.Id:D} {ride.Status}: {ShortPlaceName(ride.Pickup)} to {ShortPlaceName(ride.Dropoff)}, {FormatRideTime(ride.PickupAt)}"));
            }

            _ = ConciergeToolCallPolicy.TryOptionalString(arguments, "when", 16, out var when);
            return FormatRideHistory(rides, when ?? "upcoming", request.Channel);
        }

        if (StringComparer.Ordinal.Equals(name, ConciergeToolDefinitions.ProposeRideCancellation))
        {
            if (action.Ride is not { } ride)
            {
                return action.Message ?? "I couldn't verify that ride, so I haven't offered to cancel it.";
            }

            if (ride.Status is not ("Booked" or "DriverAssigned"))
            {
                return $"That ride is currently {ride.Status} and can no longer be cancelled.";
            }

            return $"I found your ride to {ShortPlaceName(ride.Dropoff)}, scheduled for {FormatRideTime(ride.PickupAt)}. Would you like me to cancel it? No cancellation has been made.";
        }

        if (name is ConciergeToolDefinitions.ConfirmRideCancellation)
        {
            return action.Message is null
                ? "Your ride is canceled."
                : action.Message;
        }

        return ToolFailureReply(name);
    }

    /// <summary>Model-context description of a stored ride offer; contains no ids.</summary>
    internal static string DescribeRideOffer(ConciergeRideProposal offer) =>
        $"a car from {ShortPlaceName(offer.Pickup.Name)} to {ShortPlaceName(offer.Dropoff.Name)}"
        + (offer.Immediate || offer.PickupAtUtc is null
            ? " picking up now"
            : " at the scheduled pickup time stated in that offer");

    /// <summary>Model-context description of a stored cancellation offer; contains no ids.</summary>
    internal static string DescribeRideCancellationOffer(ConciergeRideCancellationProposal offer) =>
        $"the ride to {ShortPlaceName(offer.Dropoff)} scheduled for {FormatRideTime(offer.PickupAt)}";

    internal static string DescribeCancelledRide(ConciergeRideCancellationOutcome outcome) =>
        $"the ride to {ShortPlaceName(outcome.Dropoff)} scheduled for {FormatRideTime(outcome.PickupAt)}";

    internal static bool HaveSameSpokenPlaceName(string first, string second) =>
        StringComparer.OrdinalIgnoreCase.Equals(ShortPlaceName(first), ShortPlaceName(second));

    private static string FormatPickupTime(DateTimeOffset pickupAtUtc, IDictionary<string, object?> arguments)
    {
        var local = ConciergeToolCallPolicy.TryOptionalString(arguments, "pickupAt", 64, out var requested)
            && DateTimeOffset.TryParse(requested, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? pickupAtUtc.ToOffset(parsed.Offset)
                : (DateTimeOffset?)null;
        return local is { } hotelLocal
            ? FormatRideTime(hotelLocal)
            : pickupAtUtc.ToUniversalTime().ToString("dddd, MMMM d 'at' h:mm tt 'UTC'", CultureInfo.InvariantCulture);
    }

    private static string FormatRideTime(DateTimeOffset value) =>
        value.ToString("dddd, MMMM d 'at' h:mm tt 'hotel time'", CultureInfo.InvariantCulture);

    private static string ShortPlaceName(string value)
    {
        if (value.StartsWith("Hotel ZGY", StringComparison.OrdinalIgnoreCase))
        {
            return "Hotel ZGY";
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(", ", parts.Take(2));
    }

    private static string GuestPlaceName(string? resolved, string requested) =>
        ShortPlaceName(ConciergeActionToolService.PreferRequestedPlaceName(resolved, requested));

    private static string EnsureTerminalPunctuation(string value) =>
        Regex.IsMatch(value, @"[.!?]$") ? value : value + ".";

    // Without a usable route duration there is no arrival estimate, so report the pickup time instead.
    private static string ArrivalClause(RideSummary ride, string lead) =>
        ride.ArrivalAt is { } arrival
            ? lead + HotelClockTime(arrival)
            : "; pickup was at " + HotelClockTime(ride.PickupAt);

    private static string HotelClockTime(DateTimeOffset value) =>
        value.ToString("h:mm tt 'hotel time'", CultureInfo.InvariantCulture);

    private static string FormatRideStatus(RideSummary ride)
    {
        var places = $"from {ShortPlaceName(ride.Pickup)} to {ShortPlaceName(ride.Dropoff)}";
        return ride.Status switch
        {
            "Booked" => $"Your ride {places} is booked for {FormatRideTime(ride.PickupAt)}.",
            "DriverAssigned" => $"Your ride {places} has a driver assigned for pickup on {FormatRideTime(ride.PickupAt)}.",
            "PickedUp" => $"Your ride {places} is in progress{ArrivalClause(ride, " and should arrive around ")}.",
            "Completed" => $"Your ride {places} is complete{ArrivalClause(ride, "; it arrived around ")}.",
            "Cancelled" => $"Your ride {places} was canceled.",
            _ => $"Your ride {places} has the status {ride.Status.ToLowerInvariant()}.",
        };
    }

    private static string FormatRideHistory(
        IReadOnlyList<RideSummary> rides,
        string when,
        string channel)
    {
        var isPast = StringComparer.Ordinal.Equals(when, "past");
        var period = isPast ? "past" : "upcoming";
        if (rides.Count == 0)
        {
            return $"You don't have any {period} rides.";
        }

        var descriptions = rides.Take(5).Select(ride =>
        {
            var destination = ShortPlaceName(ride.Dropoff);
            var status = ride.Status switch
            {
                "Booked" => $"is booked for {FormatRideTime(ride.PickupAt)}",
                "DriverAssigned" => $"has a driver assigned for pickup on {FormatRideTime(ride.PickupAt)}",
                "PickedUp" => $"is in progress{ArrivalClause(ride, " and should arrive around ")}",
                "Completed" => $"is complete{ArrivalClause(ride, "; it arrived around ")}",
                "Cancelled" => "was canceled",
                _ => $"has the status {ride.Status.ToLowerInvariant()}",
            };
            // Past rides span days and their status text carries only a clock time.
            var day = isPast
                ? " on " + ride.PickupAt.ToString("MMM dd", CultureInfo.InvariantCulture)
                : string.Empty;
            return $"Your ride{day} to {destination} {status}.";
        }).ToArray();

        if (StringComparer.Ordinal.Equals(channel, "web"))
        {
            return $"Here are your {period} rides:\n\n"
                + string.Join("\n", descriptions.Select((description, index) => $"{index + 1}. {description}"));
        }

        if (rides.Count == 1)
        {
            return descriptions[0];
        }

        var count = rides.Count.ToString(CultureInfo.InvariantCulture);
        var descriptor = StringComparer.Ordinal.Equals(period, "past") ? "most recent" : "next";
        return $"You have {count} {period} rides. Your {descriptor} ride is: {descriptions[0]}";
    }

    private static string WeatherIconKey(string conditions)
    {
        var normalized = conditions.ToLowerInvariant();
        if (normalized.Contains("thunderstorm", StringComparison.Ordinal)) return "thunderstorm";
        if (normalized.Contains("snow", StringComparison.Ordinal)) return "snow";
        if (normalized.Contains("drizzle", StringComparison.Ordinal)) return "drizzle";
        if (normalized.Contains("rain", StringComparison.Ordinal) || normalized.Contains("shower", StringComparison.Ordinal)) return "rain";
        if (normalized.Contains("fog", StringComparison.Ordinal)) return "fog";
        if (normalized.Contains("partly cloudy", StringComparison.Ordinal) || normalized.Contains("mostly clear", StringComparison.Ordinal)) return "partly-cloudy";
        if (normalized.Contains("cloud", StringComparison.Ordinal) || normalized.Contains("overcast", StringComparison.Ordinal)) return "cloudy";
        return "clear";
    }

    internal static string ToolFailureReply(string toolName) =>
        toolName is ConciergeToolDefinitions.ProposeRide
            ? "I couldn't prepare the ride, so no ride has been booked. Please try again in a moment."
            : toolName is ConciergeToolDefinitions.ConfirmRide
                ? "The ride service did not confirm the outcome. Check ride history before trying again."
                : toolName is ConciergeToolDefinitions.ConfirmRideCancellation
                    ? "The ride service did not confirm cancellation. Check ride status before trying again."
                    : toolName is ConciergeToolDefinitions.FindNearbyPlaces
                        ? "I couldn't look up nearby places just now. Please try again in a moment."
                        : toolName is ConciergeToolDefinitions.GetPlaceOpeningHours
                            ? "I couldn't look up that place's opening hours just now. Please try again in a moment."
                            : "I couldn't retrieve a reliable result from that tool. Please try again or clarify the request.";

    private static string LimitUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return value;
        }

        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            if (Encoding.UTF8.GetByteCount(value.AsSpan(0, middle)) <= maximumBytes - 3)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return value[..low].TrimEnd() + "…";
    }

    private static Guid CreateProposalId(
        string turnId,
        long sequence,
        ConciergeRideProposalDraft proposal)
    {
        var material = string.Join(
            '\n',
            turnId,
            sequence.ToString(CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(proposal, JsonOptions));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new Guid(hash.AsSpan(0, 16));
    }

}
