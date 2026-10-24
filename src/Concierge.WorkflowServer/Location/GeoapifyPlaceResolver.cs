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
/// required by the provider in its query string, so requests use a logger-free client and are
/// excluded from outbound HTTP tracing to prevent the credential entering telemetry.
/// </summary>
public sealed class GeoapifyPlaceResolver(
    IHttpClientFactory httpClientFactory,
    HotelLocation hotelLocation,
    IOptions<GeoapifyOptions> geoapifyOptions)
{
    private const string Endpoint = "https://api.geoapify.com/v1/geocode/search";
    private const int ResultLimit = 20;
    private const double MinimumConfidence = 0.5;
    private const double MiamiAreaRadiusKilometers = 60;

    // Geoapify's building and venue records for one address sat ~45 m apart.
    private const double SameSiteMeters = 75;

    public Task<PlaceResolutionResult> ResolveAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(query, restrictToMiamiArea: true, cancellationToken: cancellationToken);

    /// <summary>
    /// Resolves a named weather destination without the hotel-area restriction used for local
    /// routes. Confidence and uniqueness checks still apply; ambiguous matches require clarification.
    /// </summary>
    public Task<PlaceResolutionResult> ResolveForWeatherAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(query, restrictToMiamiArea: false, cancellationToken: cancellationToken);

    private async Task<PlaceResolutionResult> ResolveCoreAsync(
        string query,
        bool restrictToMiamiArea,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var trimmedQuery = query.Trim();
        if (trimmedQuery.Length > 200)
        {
            throw new ArgumentException("Place search text cannot exceed 200 characters.", nameof(query));
        }

        var hotel = hotelLocation.Coordinates;
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
        using var document = await LocationProviderHttp.GetJsonAsync(
            httpClientFactory, uri, "Geoapify", cancellationToken);

        var records = ReadResults(document.RootElement)
            .Select(result => ReadPlace(result, trimmedQuery))
            .Where(record => record is not null)
            .Select(record => record!)
            .Where(record => record.Place.Confidence >= MinimumConfidence
                && (!restrictToMiamiArea || IsInMiamiArea(record.Place, hotel)))
            .OrderByDescending(record => record.Place.Confidence)
            .DistinctBy(record => record.Place.PlaceId, StringComparer.Ordinal)
            .ToArray();

        // Geoapify returns nameless building records and the named venue for one address
        // (e.g. "Wayan Miami, 50 Northwest 24th Street"); asking the guest to choose between
        // them is meaningless. Geoapify gives these records no shared id, so merge only when the
        // guest's own words name the identity they share; nearby venues or branches still need
        // clarification.
        if (records.Length > 1 && SelectRequestedIdentity(records, trimmedQuery) is { } requested)
        {
            records = [requested];
        }

        var candidates = records.Select(record => record.Place).ToArray();

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

    private static PlaceRecord? ReadPlace(JsonElement result, string query)
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

        var name = ReadString(properties, "name");
        var houseNumber = ReadString(properties, "housenumber");
        var street = ReadString(properties, "street");

        // Geoapify scores "Name, street address" text (no city) at confidence 0 even when the
        // named place and its address both match, which is how guests repeat a nearby-search
        // result. Building-level confidence only says the building position is correct, so it
        // stands in only when the request independently names this venue and its address.
        if (confidence < MinimumConfidence
            && string.Equals(ReadString(rank, "match_type"), "full_match", StringComparison.Ordinal)
            && TryReadDouble(rank, "confidence_building_level", out var buildingConfidence)
            && buildingConfidence is >= 0 and <= 1
            && PlaceIdentity.RequestNamesVenue(query, name)
            && PlaceIdentity.RequestNamesAddress(query, houseNumber, street))
        {
            confidence = buildingConfidence;
        }

        var placeId = ReadString(properties, "place_id")
            ?? $"{formatted}:{latitude.ToString("F5", CultureInfo.InvariantCulture)}:"
            + longitude.ToString("F5", CultureInfo.InvariantCulture);

        return new PlaceRecord(new ResolvedPlace(
            placeId,
            name ?? formatted,
            formatted,
            latitude,
            longitude,
            ReadString(properties, "city"),
            ReadString(properties, "county"),
            ReadString(properties, "state"),
            ReadString(properties, "country_code"),
            confidence),
            name,
            PlaceIdentity.AddressKey(houseNumber, street));
    }

    /// <summary>
    /// The one record the request identifies when every record is at the same house number and
    /// street within one site. When the request names a venue, every named record must be that
    /// venue (nameless records at its address are its building). When it names no venue but
    /// names the address, the address itself is the destination. Otherwise null: the guest chooses.
    /// </summary>
    private static PlaceRecord? SelectRequestedIdentity(IReadOnlyList<PlaceRecord> records, string query)
    {
        if (records[0].AddressKey is not { } address
            || records.Any(record => !StringComparer.Ordinal.Equals(record.AddressKey, address))
            || !LieWithinOneSite(records))
        {
            return null;
        }

        var named = records.Where(record => record.VenueName is not null).ToArray();
        var requestedVenue = named.FirstOrDefault(record => PlaceIdentity.RequestNamesVenue(query, record.VenueName));
        if (requestedVenue is not null)
        {
            var venue = PlaceIdentity.NormalizeName(requestedVenue.VenueName);
            return named.All(record => StringComparer.Ordinal.Equals(PlaceIdentity.NormalizeName(record.VenueName), venue))
                ? requestedVenue
                : null;
        }

        return PlaceIdentity.RequestNamesAddressKey(query, address)
            ? records.FirstOrDefault(record => record.VenueName is null)
            : null;
    }

    private static bool LieWithinOneSite(IReadOnlyList<PlaceRecord> records)
    {
        for (var i = 0; i < records.Count; i++)
        {
            for (var j = i + 1; j < records.Count; j++)
            {
                var first = records[i].Place;
                var second = records[j].Place;
                if (DistanceKilometers(first.Latitude, first.Longitude, second.Latitude, second.Longitude) * 1000
                    > SameSiteMeters)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>A resolved candidate with the raw Geoapify identity used to merge records.</summary>
    private sealed record PlaceRecord(ResolvedPlace Place, string? VenueName, string? AddressKey);

    private static bool IsInMiamiArea(ResolvedPlace place, HotelCoordinates hotel)
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
