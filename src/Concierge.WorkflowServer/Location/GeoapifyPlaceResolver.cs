using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Location;

public enum PlaceResolutionStatus
{
    Resolved,
    NeedsClarification,
}

public sealed record ResolvedPlace(
    string PlaceId,
    string Name,
    string Formatted,
    double Latitude,
    double Longitude,
    string? City,
    string? County,
    string? State,
    string? CountryCode,
    double Confidence);

public sealed record PlaceResolutionResult(
    PlaceResolutionStatus Status,
    string Query,
    ResolvedPlace? Place,
    IReadOnlyList<ResolvedPlace> Candidates);

/// <summary>
/// Resolves places consistently for location-dependent activities. Geoapify's API key is
/// required by the provider in its query string, so requests use a bare HttpClient and are
/// excluded from outbound HTTP tracing to prevent the credential entering telemetry.
/// </summary>
public sealed class GeoapifyPlaceResolver(
    HttpClient httpClient,
    IOptions<HotelLocationOptions> hotelOptions,
    IOptions<GeoapifyOptions> geoapifyOptions)
{
    private const string Endpoint = "https://api.geoapify.com/v1/geocode/search";
    private const int ResultLimit = 20;
    private const double MinimumConfidence = 0.5;
    private const double MiamiAreaRadiusKilometers = 60;

    public async Task<PlaceResolutionResult> ResolveAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length > 200)
        {
            throw new ArgumentException("Place search text cannot exceed 200 characters.", nameof(query));
        }

        var hotel = hotelOptions.Value;
        var key = geoapifyOptions.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "Geoapify:ApiKey is required to resolve named places.");
        }

        var bias = string.Create(
            CultureInfo.InvariantCulture,
            $"proximity:{hotel.Longitude},{hotel.Latitude}");
        var uri = $"{Endpoint}?text={Uri.EscapeDataString(trimmedQuery)}"
            + $"&bias={Uri.EscapeDataString(bias)}&limit={ResultLimit}&format=json"
            + $"&apiKey={Uri.EscapeDataString(key)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await LocationProviderHttp.SendAsync(httpClient, request, "Geoapify", cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var candidates = ReadResults(document.RootElement)
            .Select(ReadPlace)
            .Where(place => place is not null)
            .Select(place => place!)
            .Where(place => place.Confidence >= MinimumConfidence && IsInMiamiArea(place, hotel))
            .OrderByDescending(place => place.Confidence)
            .DistinctBy(place => place.PlaceId, StringComparer.Ordinal)
            .ToArray();

        return candidates.Length switch
        {
            1 => new(PlaceResolutionStatus.Resolved, trimmedQuery, candidates[0], candidates),
            _ => new(PlaceResolutionStatus.NeedsClarification, trimmedQuery, null, candidates),
        };
    }

    private static IEnumerable<JsonElement> ReadResults(JsonElement root)
    {
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            return results.EnumerateArray().ToArray();
        }

        if (root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
        {
            return features.EnumerateArray().ToArray();
        }

        return [];
    }

    private static ResolvedPlace? ReadPlace(JsonElement result)
    {
        var properties = result.TryGetProperty("properties", out var featureProperties)
            ? featureProperties
            : result;
        var latitude = 0d;
        var longitude = 0d;
        var hasCoordinates = TryReadDouble(properties, "lat", out latitude)
            && TryReadDouble(properties, "lon", out longitude);
        if (!hasCoordinates && !TryReadCoordinates(result, out latitude, out longitude))
        {
            return null;
        }

        if (!double.IsFinite(latitude)
            || latitude is < -90 or > 90
            || !double.IsFinite(longitude)
            || longitude is < -180 or > 180)
        {
            return null;
        }

        var formatted = ReadString(properties, "formatted")
            ?? ReadString(properties, "name")
            ?? string.Empty;
        if (formatted.Length == 0)
        {
            return null;
        }

        var rank = properties.TryGetProperty("rank", out var rankElement)
            ? rankElement
            : default;
        if (!TryReadDouble(rank, "confidence", out var confidence)
            || confidence is < 0 or > 1)
        {
            return null;
        }

        var placeId = ReadString(properties, "place_id")
            ?? $"{formatted}:{latitude.ToString("F5", CultureInfo.InvariantCulture)}:"
            + longitude.ToString("F5", CultureInfo.InvariantCulture);

        return new ResolvedPlace(
            placeId,
            ReadString(properties, "name") ?? formatted,
            formatted,
            latitude,
            longitude,
            ReadString(properties, "city"),
            ReadString(properties, "county"),
            ReadString(properties, "state"),
            ReadString(properties, "country_code"),
            confidence);
    }

    private static bool IsInMiamiArea(ResolvedPlace place, HotelLocationOptions hotel)
    {
        if (!string.Equals(place.CountryCode, "us", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(place.State, "Florida", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(place.State, "FL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (place.County?.Contains("Miami-Dade", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return DistanceKilometers(hotel.Latitude, hotel.Longitude, place.Latitude, place.Longitude)
            <= MiamiAreaRadiusKilometers;
    }

    private static double DistanceKilometers(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double earthRadiusKm = 6371.0088;
        var latitudeDelta = DegreesToRadians(latitude2 - latitude1);
        var longitudeDelta = DegreesToRadians(longitude2 - longitude1);
        var halfChord = Math.Pow(Math.Sin(latitudeDelta / 2), 2)
            + Math.Cos(DegreesToRadians(latitude1))
            * Math.Cos(DegreesToRadians(latitude2))
            * Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusKm * 2 * Math.Asin(Math.Sqrt(halfChord));
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180);

    private static bool TryReadCoordinates(JsonElement result, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        return result.TryGetProperty("geometry", out var geometry)
            && geometry.TryGetProperty("coordinates", out var coordinates)
            && coordinates.ValueKind == JsonValueKind.Array
            && coordinates.GetArrayLength() >= 2
            && coordinates[0].TryGetDouble(out longitude)
            && coordinates[1].TryGetDouble(out latitude);
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var property)
            && property.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
