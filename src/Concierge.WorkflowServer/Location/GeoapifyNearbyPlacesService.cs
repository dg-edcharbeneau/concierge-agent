using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Location;

/// <summary>A guest-facing place type and the Geoapify Places categories it searches.</summary>
public sealed record NearbyPlaceCategory(string Key, string GeoapifyCategories, string Singular, string Plural);

/// <summary>
/// The fixed set of place types the model may request. Category keys are from the Geoapify
/// Places API "Supported categories" list: https://apidocs.geoapify.com/docs/places/#categories
/// </summary>
public static class NearbyPlaceCategories
{
    public static readonly IReadOnlyList<NearbyPlaceCategory> All =
    [
        new("pharmacy", "healthcare.pharmacy", "pharmacy", "pharmacies"),
        new("grocery", "commercial.supermarket", "grocery store", "grocery stores"),
        new("convenience", "commercial.convenience", "convenience store", "convenience stores"),
        new("restaurant", "catering.restaurant", "restaurant", "restaurants"),
        new("cafe", "catering.cafe", "cafe", "cafes"),
        new("bar", "catering.bar,catering.pub", "bar", "bars"),
        new("fast_food", "catering.fast_food", "fast food place", "fast food places"),
        new("atm", "service.financial.atm", "ATM", "ATMs"),
        new("bank", "service.financial.bank", "bank", "banks"),
        new("hospital", "healthcare.hospital", "hospital", "hospitals"),
        new("clinic", "healthcare.clinic_or_praxis", "clinic", "clinics"),
        new("gas_station", "service.vehicle.fuel", "gas station", "gas stations"),
        new("mall", "commercial.shopping_mall", "shopping mall", "shopping malls"),
        new("museum", "entertainment.museum", "museum", "museums"),
        new("cinema", "entertainment.cinema", "movie theater", "movie theaters"),
        new("park", "leisure.park", "park", "parks"),
        new("beach", "beach", "beach", "beaches"),
        new("attraction", "tourism.attraction,tourism.sights", "attraction", "attractions"),
        new("post_office", "service.post.office", "post office", "post offices"),
    ];

    /// <summary>Model-visible keys, in declaration order.</summary>
    public static readonly string[] Keys = All.Select(category => category.Key).ToArray();

    public static bool TryGet(string? key, out NearbyPlaceCategory category)
    {
        category = All.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Key, key))!;
        return category is not null;
    }
}

public sealed record NearbyPlace(
    string Name, string? Address, double DistanceMeters,
    string? ProviderId = null, double? Latitude = null, double? Longitude = null);

/// <summary>
/// Finds the closest places of one category around Hotel ZGY with the Geoapify Places API.
/// Uses the no-retry location provider client; the API key travels in the query string, so
/// failures are rebuilt without the request URI by <see cref="LocationProviderHttp"/>.
/// </summary>
public sealed class GeoapifyNearbyPlacesService(
    IHttpClientFactory httpClientFactory,
    HotelLocation hotelLocation,
    IOptions<GeoapifyOptions> geoapifyOptions)
{
    private const string Endpoint = "https://api.geoapify.com/v2/places";

    /// <summary>About five miles: nearby for a guest without a car, inside the Miami area.</summary>
    internal const int SearchRadiusMeters = 8_000;

    // Request extra rows because unnamed places are dropped before the closest few are kept.
    internal const int RequestLimit = 10;
    internal const int MaxResults = 3;

    public async Task<IReadOnlyList<NearbyPlace>> FindAsync(
        NearbyPlaceCategory category,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);

        var key = geoapifyOptions.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Geoapify:ApiKey is required to find nearby places.");
        }

        var hotel = hotelLocation.Coordinates;
        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"circle:{hotel.Longitude},{hotel.Latitude},{SearchRadiusMeters}");
        var bias = string.Create(
            CultureInfo.InvariantCulture,
            $"proximity:{hotel.Longitude},{hotel.Latitude}");
        var uri = $"{Endpoint}?categories={Uri.EscapeDataString(category.GeoapifyCategories)}"
            + $"&filter={Uri.EscapeDataString(filter)}&bias={Uri.EscapeDataString(bias)}"
            + $"&limit={RequestLimit.ToString(CultureInfo.InvariantCulture)}&lang=en"
            + $"&apiKey={Uri.EscapeDataString(key)}";
        using var document = await LocationProviderHttp.GetJsonAsync(
            httpClientFactory, uri, "Geoapify", cancellationToken);

        return ReadPlaces(document.RootElement, hotel);
    }

    internal static IReadOnlyList<NearbyPlace> ReadPlaces(JsonElement root, HotelCoordinates hotel)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Geoapify returned no place features.");
        }

        var places = new List<(string Id, NearbyPlace Place)>();
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != JsonValueKind.Object
                || !feature.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // A guest cannot act on an unnamed place, so it is not worth a sentence.
            var placeName = ReadString(properties, "name");
            if (placeName is null
                || !TryReadDouble(properties, "lat", out var latitude)
                || !TryReadDouble(properties, "lon", out var longitude)
                || latitude is < -90 or > 90
                || longitude is < -180 or > 180)
            {
                continue;
            }

            var distance = TryReadDouble(properties, "distance", out var reported) && reported >= 0
                ? reported
                : DistanceMeters(hotel.Latitude, hotel.Longitude, latitude, longitude);
            var id = ReadString(properties, "place_id")
                ?? string.Create(CultureInfo.InvariantCulture, $"{placeName}:{latitude:F5}:{longitude:F5}");
            places.Add((id, new NearbyPlace(placeName, ReadAddress(properties, placeName), distance,
                id, latitude, longitude)));
        }

        return places
            .DistinctBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(entry => entry.Place)
            .OrderBy(place => place.DistanceMeters)
            .Take(MaxResults)
            .ToArray();
    }

    /// <summary>Street address only; city, postcode, and country add nothing for a nearby place.</summary>
    private static string? ReadAddress(JsonElement properties, string placeName)
    {
        var street = ReadString(properties, "street");
        var houseNumber = ReadString(properties, "housenumber");
        if (street is not null)
        {
            return houseNumber is null ? street : $"{houseNumber} {street}";
        }

        var line1 = ReadString(properties, "address_line1");
        return line1 is not null && !StringComparer.OrdinalIgnoreCase.Equals(line1, placeName)
            ? line1
            : null;
    }

    private static double DistanceMeters(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double earthRadiusMeters = 6_371_008.8;
        var latitudeDelta = DegreesToRadians(latitude2 - latitude1);
        var longitudeDelta = DegreesToRadians(longitude2 - longitude1);
        var halfChord = Math.Pow(Math.Sin(latitudeDelta / 2), 2)
            + Math.Cos(DegreesToRadians(latitude1))
            * Math.Cos(DegreesToRadians(latitude2))
            * Math.Pow(Math.Sin(longitudeDelta / 2), 2);
        return earthRadiusMeters * 2 * Math.Asin(Math.Sqrt(halfChord));
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180);

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value)
            && double.IsFinite(value);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
