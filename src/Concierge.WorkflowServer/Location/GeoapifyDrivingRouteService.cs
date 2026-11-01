using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Location;

public sealed record DrivingRouteRequest(
    IReadOnlyList<string> Places, DrivingRouteEndpoint? VerifiedDestination = null);

public enum DrivingRouteStatus
{
    Available,
    NeedsClarification,
}

public sealed record DrivingRoute(
    double DistanceMeters,
    double EstimatedDurationSeconds,
    string VoiceSummary,
    string WebSummary,
    IReadOnlyList<string> VoiceInstructions,
    IReadOnlyList<string> WebInstructions);

public sealed record DrivingRouteResult(
    DrivingRouteStatus Status,
    string? ClarificationQuery,
    IReadOnlyList<ResolvedPlace> Candidates,
    DrivingRoute? Route,
    DrivingRouteEndpoint? Origin = null,
    DrivingRouteEndpoint? Destination = null,
    bool ClarifyingOrigin = false);

public sealed record DrivingRouteEndpoint(string Name, double Latitude, double Longitude);

/// <summary>Resolves route endpoints and requests server-estimated Geoapify driving directions.</summary>
public sealed class GeoapifyDrivingRouteService(
    IHttpClientFactory httpClientFactory,
    GeoapifyPlaceResolver placeResolver,
    IOptions<HotelLocationOptions> hotelOptions,
    HotelLocation hotelLocation,
    IOptions<GeoapifyOptions> geoapifyOptions)
{
    private const string Endpoint = "https://api.geoapify.com/v1/routing";
    private const double MetersPerMile = 1609.344;

    // ADR 0009 D4: voice summarises the first few steps; web may show the full list.
    internal const int MaxVoiceInstructions = 3;
    internal const int MaxVoiceInstructionLength = 120;
    internal const int MaxWebInstructions = 100;
    internal const int MaxWebInstructionLength = 500;

    public async Task<DrivingRouteResult> GetRouteAsync(
        DrivingRouteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Places);

        if (request.Places.Count is < 1 or > 2
            || request.Places.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "Provide one destination or two places in origin-to-destination order.",
                nameof(request));
        }

        var hotel = hotelOptions.Value;
        var coordinates = hotelLocation.Coordinates;
        var hotelEndpoint = new DrivingRouteEndpoint(hotel.Label, coordinates.Latitude, coordinates.Longitude);
        var endpoints = new List<DrivingRouteEndpoint>(request.Places.Count);
        for (var index = 0; index < request.Places.Count; index++)
        {
            var query = request.Places[index];
            if (index == request.Places.Count - 1 && request.VerifiedDestination is { } verified)
            {
                if (string.IsNullOrWhiteSpace(verified.Name)
                    || !double.IsFinite(verified.Latitude) || verified.Latitude is < -90 or > 90
                    || !double.IsFinite(verified.Longitude) || verified.Longitude is < -180 or > 180)
                {
                    throw new ArgumentException("Verified destination has invalid coordinates.", nameof(request));
                }
                endpoints.Add(verified);
                continue;
            }
            if (HotelLocationOptions.IsHotelReference(query))
            {
                endpoints.Add(hotelEndpoint);
                continue;
            }

            var resolution = await placeResolver.ResolveAsync(query, cancellationToken);
            if (resolution.Status != PlaceResolutionStatus.Resolved)
            {
                return new(
                    DrivingRouteStatus.NeedsClarification,
                    resolution.Query,
                    resolution.Candidates,
                    Route: null,
                    ClarifyingOrigin: request.Places.Count == 2 && index == 0);
            }

            endpoints.Add(ToEndpoint(resolution.Place!));
        }

        var origin = endpoints.Count == 1 ? hotelEndpoint : endpoints[0];
        var destination = endpoints[^1];

        var key = geoapifyOptions.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Geoapify:ApiKey is required to calculate driving routes.");
        }

        var waypoints = string.Create(
            CultureInfo.InvariantCulture,
            $"{origin.Latitude},{origin.Longitude}|{destination.Latitude},{destination.Longitude}");
        var uri = $"{Endpoint}?waypoints={Uri.EscapeDataString(waypoints)}"
            + "&mode=drive&details=instruction_details&units=metric&format=json&lang=en"
            + $"&apiKey={Uri.EscapeDataString(key)}";
        using var document = await LocationProviderHttp.GetJsonAsync(
            httpClientFactory, uri, "Geoapify", cancellationToken);

        var route = ReadRoute(document.RootElement);
        return new(DrivingRouteStatus.Available, null, [], route, origin, destination);
    }

    private static DrivingRoute ReadRoute(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Geoapify returned no driving route.");
        }

        var result = results[0];
        var distanceMeters = ReadRequiredPositiveNumber(result, "distance");
        var durationSeconds = ReadRequiredPositiveNumber(result, "time");
        if (!result.TryGetProperty("legs", out var legs) || legs.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Geoapify route is missing its direction steps.");
        }

        var steps = new List<string>();
        var voiceInstructions = new List<string>(MaxVoiceInstructions);
        foreach (var leg in legs.EnumerateArray())
        {
            if (leg.ValueKind != JsonValueKind.Object
                || !leg.TryGetProperty("steps", out var legSteps)
                || legSteps.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var step in legSteps.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object
                    || !step.TryGetProperty("instruction", out var instruction)
                    || instruction.ValueKind != JsonValueKind.Object
                    || !instruction.TryGetProperty("text", out var textElement)
                    || textElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var fullText = textElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(fullText))
                {
                    continue;
                }

                var stepDistance = TryReadNonNegativeNumber(step, "distance", out var meters)
                    ? $" ({FormatDistance(meters)})"
                    : string.Empty;
                if (steps.Count < MaxWebInstructions)
                {
                    steps.Add(ShortenForWeb(fullText) + stepDistance);
                }
                if (voiceInstructions.Count < MaxVoiceInstructions)
                {
                    voiceInstructions.Add(ShortenForVoice(fullText));
                }
            }
        }

        if (steps.Count == 0)
        {
            throw new InvalidDataException("Geoapify route contains no usable driving instructions.");
        }

        var distance = FormatDistance(distanceMeters);
        var durationMinutes = Math.Max(1, (int)Math.Ceiling(durationSeconds / 60));
        return new DrivingRoute(
            distanceMeters,
            durationSeconds,
            $"About {distance}; around {durationMinutes.ToString(CultureInfo.InvariantCulture)} minutes by car.",
            $"{distance} · about {durationMinutes.ToString(CultureInfo.InvariantCulture)} min by car (estimated; no live traffic).",
            voiceInstructions,
            steps);
    }

    private static double ReadRequiredPositiveNumber(JsonElement element, string propertyName) =>
        TryReadNonNegativeNumber(element, propertyName, out var value) && value > 0
            ? value
            : throw new InvalidDataException($"Geoapify route has an invalid '{propertyName}' value.");

    private static bool TryReadNonNegativeNumber(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value)
            && double.IsFinite(value)
            && value >= 0;
    }

    private static string FormatDistance(double meters)
    {
        var miles = meters / MetersPerMile;
        return miles < 0.1
            ? $"{Math.Round(meters * 3.28084, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)} ft"
            : $"{miles.ToString("0.0", CultureInfo.InvariantCulture)} mi";
    }

    private static string ShortenForVoice(string instruction)
    {
        if (instruction.Length <= MaxVoiceInstructionLength)
        {
            return instruction;
        }

        var cutoff = instruction.LastIndexOf(' ', MaxVoiceInstructionLength - 1);
        return instruction[..(cutoff > 0 ? cutoff : MaxVoiceInstructionLength)].TrimEnd() + "…";
    }

    private static DrivingRouteEndpoint ToEndpoint(ResolvedPlace place) =>
        new(place.Formatted, place.Latitude, place.Longitude);

    private static string ShortenForWeb(string instruction)
    {
        if (instruction.Length <= MaxWebInstructionLength)
        {
            return instruction;
        }

        var cutoff = instruction.LastIndexOf(' ', MaxWebInstructionLength - 1);
        return instruction[..(cutoff > 0 ? cutoff : MaxWebInstructionLength)].TrimEnd() + "…";
    }
}
