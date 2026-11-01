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

    internal sealed record RideRenderingContext(
        ConciergeRideProposal? Offer,
        Guid? RideId,
        IReadOnlyList<ConciergeCanonicalPlace> Places);

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
            bool includeRideIdsForCancellation,
            RideRenderingContext? rideContext = null)
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
                    FormatNearbyPlaces((ConciergeNearbyPlacesToolResult?)Decoded, request, arguments),
                ConciergeToolDefinitions.GetPlaceOpeningHours =>
                    FormatPlaceOpeningHours((ConciergePlaceOpeningHoursToolResult?)Decoded, request, arguments),
                ConciergeToolDefinitions.ProposeRide =>
                    FormatRideProposal(
                        (ConciergeRideProposalToolResult?)Decoded,
                        request.Channel,
                        arguments),
                _ => FormatRideAction(
                    ToolName,
                    (ConciergeRideActionResult?)Decoded,
                    request,
                    arguments,
                    includeRideIdsForCancellation,
                    rideContext),
            };
            context = LimitUtf8(context, MaxCompactToolResultUtf8Bytes);
            var modelContext = ToolName == ConciergeToolDefinitions.FindNearbyPlaces
                && Decoded is ConciergeNearbyPlacesToolResult nearby
                ? CompactNearby(nearby)
                : context;
            return new InterpretedResult(
                modelContext,
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

    private static string CompactNearby(ConciergeNearbyPlacesToolResult nearby)
    {
        // Bound the serialized projection, not just its strings: JSON escaping can expand them.
        // Keep all three branches and valid JSON rather than cutting off the alternate's address.
        for (var budget = 160; budget >= 10; budget /= 2)
        {
            var json = JsonSerializer.Serialize(nearby with
            {
                Places = nearby.Places.Take(3).Select(place => place with
                {
                    Name = LimitUtf8(place.Name, Math.Min(120, budget)),
                    Address = place.Address is null ? null : LimitUtf8(place.Address, budget),
                    ProviderId = place.ProviderId is null ? null : LimitUtf8(place.ProviderId, Math.Min(80, budget)),
                }).ToArray(),
            }, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) <= MaxCompactToolResultUtf8Bytes) return json;
        }
        return """{"status":"Unavailable","places":[]}""";
    }

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
        if (period is "current" or "now" or "right now")
        {
            return FormatCurrentWeather(weather, request);
        }

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

    private static string FormatCurrentWeather(
        ConciergeWeatherToolResult weather,
        ConciergeTurnSubmission request)
    {
        var conditions = string.IsNullOrWhiteSpace(weather.Conditions)
            ? "conditions unavailable"
            : weather.Conditions.Trim().TrimEnd('.');
        var location = ShortPlaceName(weather.Location ?? "the area");
        var sentence = conditions.Equals("clear sky", StringComparison.OrdinalIgnoreCase)
            ? $"Currently clear at {location}"
            : $"Currently {char.ToLowerInvariant(conditions[0])}{conditions[1..]} at {location}";
        var details = new List<string>();
        if (weather.CurrentTemperatureFahrenheit is { } temperatureF)
        {
            details.Add($"around {Math.Round(temperatureF, MidpointRounding.AwayFromZero):0}°F");
        }

        if (weather.ApparentTemperatureFahrenheit is { } apparentF)
        {
            details.Add($"feels like {Math.Round(apparentF, MidpointRounding.AwayFromZero):0}°F");
        }

        if (weather.RelativeHumidityPercent is { } humidity)
        {
            details.Add($"{humidity}% humidity");
        }

        if (weather.CurrentWindMph is { } windMph)
        {
            details.Add($"winds at {Math.Round(windMph, MidpointRounding.AwayFromZero):0} mph");
        }

        if (weather.CurrentPrecipitationInches is { } precipitationInches)
        {
            details.Add(
                $"{Math.Round(precipitationInches, 2, MidpointRounding.AwayFromZero).ToString("0.##", CultureInfo.InvariantCulture)} inches of precipitation in the preceding hour");
        }

        if (details.Count > 0)
        {
            sentence += ", " + string.Join(", ", details);
        }

        sentence += ".";
        if (weather.ObservedAt is { } observedAt)
        {
            sentence += $" Observed at {observedAt.ToString("h:mm tt", CultureInfo.InvariantCulture)} local time.";
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
        ConciergeTurnSubmission request,
        IDictionary<string, object?> arguments)
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
        if (arguments.TryGetValue("presentation", out var presentation)
            && StringComparer.Ordinal.Equals(presentation?.ToString(), "nearest"))
        {
            places = places.Take(1).ToArray();
        }
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
            .Select(place => $"{place.Name}"
                + (place.Address is not null && places.Count(other =>
                    string.Equals(other.Name, place.Name, StringComparison.OrdinalIgnoreCase)) > 1
                    ? $" at {place.Address}" : string.Empty)
                + $", {FormatMiles(place.DistanceMiles)} away")
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
        ConciergeTurnSubmission request,
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
                // Place hours are already local to the hotel area; no conversion is needed.
                if (!OpeningHoursFormatter.TryParse(result.OpeningHours, out var week))
                {
                    return $"{result.PlaceName}'s listed opening hours are {result.OpeningHours}.";
                }

                return StringComparer.Ordinal.Equals(request.Channel, "web")
                    ? OpeningHoursFormatter.FormatWeb(EscapeMarkdown(result.PlaceName), week, result.TimeZoneName)
                    : OpeningHoursFormatter.FormatVoice(result.PlaceName, week);
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
        string channel,
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
                var endpoint = result.ClarifyingPickup ? "pickup" : "destination";
                return result.Candidates.Count == 0
                    ? $"I couldn't identify the {endpoint}. Please clarify it. No ride has been booked."
                    : $"Which {endpoint} do you mean: {string.Join("; ", result.Candidates.Take(3).Select(ShortPlaceName))}? No ride has been booked.";
            }

            if (StringComparer.Ordinal.Equals(result.Status, "InvalidSchedule"))
            {
                // Only the activity's structured policy value is rendered, never provider text
                // or model arguments. Recording it in the result keeps workflow replay deterministic.
                if (result.CheckoutCutoffDate is { } checkoutDate)
                {
                    var checkoutDay = checkoutDate.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
                    return result.EarliestPickupAt is null
                        ? $"Scheduled pickups must be before the end of your checkout day, {checkoutDay}, and there is no longer time to schedule one. No ride has been booked."
                        : $"Scheduled pickups must be before the end of your checkout day, {checkoutDay}. Please choose an earlier time. No ride has been booked.";
                }

                if (result.EarliestPickupAt is { } earliest)
                {
                    return $"I can't schedule a pickup for that time. The earliest scheduled pickup is {HotelClockTime(earliest)}. No ride has been booked.";
                }

                // Results recorded before EarliestPickupAt existed keep their original reply.
                return result.MinimumAdvanceNoticeMinutes is > 0
                    ? $"A scheduled pickup must be at least {result.MinimumAdvanceNoticeMinutes} minutes from now. Please choose a later time. No ride has been booked."
                    : "The scheduled pickup is too soon or has an invalid time. Please choose a later time. No ride has been booked.";
            }

            if (StringComparer.Ordinal.Equals(result.Status, "ActiveRidesUnavailable"))
            {
                return "I couldn't check your existing rides, so I haven't offered a new one. Please try again shortly. No ride has been booked.";
            }

            return ToolFailureReply(ConciergeToolDefinitions.ProposeRide);
        }

        // An adjusted pickup is stated up front so the guest confirms the actual (later) time.
        var adjusted = result.PickupAdjusted && !draft.Immediate && result.EarliestPickupAt is { } earliestLocal
            ? earliestLocal
            : (DateTimeOffset?)null;
        var when = draft.Immediate || draft.PickupAtUtc is not { } pickupAtUtc
            ? "now"
            : adjusted is { } adjustedLocal
                ? $"on {FormatRideTime(adjustedLocal)}"
                : $"on {FormatPickupTime(pickupAtUtc, arguments)}";
        var minutes = Math.Max(1, (int)Math.Ceiling(draft.RouteDurationSeconds / 60));
        var article = minutes is 8 or 11 or 18 or (>= 80 and <= 89) ? "an" : "a";
        var adjustment = adjusted is { } moved
            ? $"The earliest available pickup is {HotelClockTime(moved)}. "
            : string.Empty;
        var warning = result.ExistingRides is { Count: > 0 } existing
            ? $"{(existing.Count == 1 ? "You already have an active ride" : "You already have active rides")} to this destination: "
                + string.Join("; ", existing.Take(3).Select(ride =>
                    $"{OfferPlaceName(ride.Dropoff)}, pickup on {FormatRideTime(ride.PickupAt)} (status: {ride.Status})"))
                + ". This offer is for an ADDITIONAL ride, not a change to any existing ride, and requires your explicit confirmation on a later turn. "
            : string.Empty;
        var fare = channel == "voice" ? "It's complimentary." : "It's complimentary for current hotel guests.";
        return $"{warning}{adjustment}{OfferPlaceName(draft.Dropoff.Name)} is about {article} {minutes}-minute ride from {OfferPlaceName(draft.Pickup.Name)}. {fare} No ride has been booked. Shall I book it {when}?";
    }

    // Reserve room for all three active-ride warnings and the final confirmation question.
    private static string OfferPlaceName(string name) => LimitUtf8(RidePlaceName(name), 160);

    private static string FormatRideAction(
        string name,
        ConciergeRideActionResult? action,
        ConciergeTurnSubmission request,
        IDictionary<string, object?> arguments,
        bool includeRideIdsForCancellation,
        RideRenderingContext? rideContext)
    {
        if (action is null)
        {
            return ToolFailureReply(name);
        }

        if (StringComparer.Ordinal.Equals(name, ConciergeToolDefinitions.ConfirmRide))
        {
            if (action.Message is not null) return action.Message;
            if (action.Ride is { } booked)
            {
                if (booked.Status is not ("Booked" or "DriverAssigned"))
                    return FormatRideStatus(booked, request.Channel, rideContext);
                var when = IsImmediateVoiceRide(booked, request.Channel, rideContext)
                    ? "now" : $"on {FormatRideTime(booked.PickupAt)}";
                return $"You're all set. A car will pick you up at {RidePlaceName(booked.Pickup)} {when} and take you to {RideDestination(booked, request.Channel, rideContext)}.";
            }

            return action.Message
                ?? "The ride service did not confirm the outcome. Check your ride history before trying again.";
        }

        if (StringComparer.Ordinal.Equals(name, ConciergeToolDefinitions.GetRideStatus))
        {
            if (action.Ride is { } status)
            {
                return FormatRideStatus(status, request.Channel, rideContext);
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
            return FormatRideHistory(rides, when ?? "upcoming", request.Channel, rideContext);
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

    internal static string DescribeConfirmedRideBooking(ConciergeRideConfirmationOutcome outcome)
    {
        if (!outcome.Confirmed
            || outcome.RideId is not { } rideId
            || rideId == Guid.Empty
            || string.IsNullOrWhiteSpace(outcome.Status)
            || string.IsNullOrWhiteSpace(outcome.Pickup)
            || string.IsNullOrWhiteSpace(outcome.Dropoff)
            || outcome.PickupAt is not { } pickupAt)
        {
            return "I couldn't verify whether the ride was booked. Please check ride status or history before trying again.";
        }

        var status = outcome.Status switch
        {
            "Booked" => "is booked",
            "DriverAssigned" => "has a driver assigned",
            "PickedUp" => "is in progress",
            "Completed" => "is complete",
            "Cancelled" => "was canceled",
            _ => $"has the status {outcome.Status.ToLowerInvariant()}",
        };
        return $"Your ride from {ShortPlaceName(outcome.Pickup)} to {ShortPlaceName(outcome.Dropoff)} {status} for pickup on {FormatRideTime(pickupAt)} (status: {outcome.Status}).";
    }

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

    // Ride labels are endpoint facts. Do not infer a venue name from comma-separated prose.
    private static string RidePlaceName(string value)
    {
        if (value.Equals("Hotel ZGY", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Hotel ZGY, ", StringComparison.OrdinalIgnoreCase)) return "Hotel ZGY";
        const string country = ", United States of America";
        return value.EndsWith(country, StringComparison.Ordinal) ? value[..^country.Length] : value;
    }

    private static bool MatchesRide(RideSummary ride, RideRenderingContext? context) =>
        context is { Offer: { } offer, RideId: { } id }
        && id == ride.Id
        && StringComparer.Ordinal.Equals(offer.Pickup.Name, ride.Pickup)
        && StringComparer.Ordinal.Equals(offer.Dropoff.Name, ride.Dropoff);

    private static bool IsImmediateVoiceRide(RideSummary ride, string channel, RideRenderingContext? context) =>
        channel == "voice" && MatchesRide(ride, context) && context!.Offer!.Immediate;

    private static string RideDestination(RideSummary ride, string channel, RideRenderingContext? context)
    {
        if (channel == "voice" && MatchesRide(ride, context))
        {
            var endpoint = context!.Offer!.Dropoff;
            var matches = context.Places.Where(place =>
                place.Latitude == endpoint.Latitude && place.Longitude == endpoint.Longitude
                && (StringComparer.Ordinal.Equals(endpoint.Name, place.Name)
                    || place.Address is not null
                        && StringComparer.Ordinal.Equals(endpoint.Name, $"{place.Name}, {place.Address}"))).ToArray();
            if (matches.Length == 1) return matches[0].Name;
        }
        return RidePlaceName(ride.Dropoff);
    }

    private static string RideStatusClause(RideSummary ride, string channel, RideRenderingContext? context) =>
        ride.Status switch
        {
            "Booked" => IsImmediateVoiceRide(ride, channel, context)
                ? "is booked" : $"is booked for {FormatRideTime(ride.PickupAt)}",
            "DriverAssigned" => IsImmediateVoiceRide(ride, channel, context)
                ? "has a driver assigned" : $"has a driver assigned for pickup on {FormatRideTime(ride.PickupAt)}",
            "PickedUp" => $"is in progress{ArrivalClause(ride, " and should arrive around ")}",
            "Completed" => $"is complete{ArrivalClause(ride, "; it arrived around ")}",
            "Cancelled" => "was canceled",
            _ => $"has the status {ride.Status.ToLowerInvariant()}",
        };

    private static string FormatRideStatus(RideSummary ride, string channel, RideRenderingContext? context)
    {
        var places = $"from {RidePlaceName(ride.Pickup)} to {RideDestination(ride, channel, context)}";
        return $"Your ride {places} {RideStatusClause(ride, channel, context)}.";
    }

    private static string FormatRideHistory(
        IReadOnlyList<RideSummary> rides,
        string when,
        string channel,
        RideRenderingContext? context)
    {
        var isPast = StringComparer.Ordinal.Equals(when, "past");
        var period = isPast ? "past" : "upcoming";
        if (rides.Count == 0)
        {
            return $"You don't have any {period} rides.";
        }

        var descriptions = rides.Take(5).Select(ride =>
        {
            var destination = RideDestination(ride, channel, context);
            var status = RideStatusClause(ride, channel, context);
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
