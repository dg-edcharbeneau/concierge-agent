using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Location;

public enum PlaceOpeningHoursStatus
{
    Available,
    NotListed,
    NoResults,
    NeedsClarification,
}

public sealed record PlaceOpeningHoursResult(
    PlaceOpeningHoursStatus Status,
    string? PlaceName,
    string? OpeningHours,
    IReadOnlyList<string> Candidates);

/// <summary>Looks up one named place and its provider-listed hours near Hotel ZGY.</summary>
public sealed class GeoapifyPlaceOpeningHoursService(
    IHttpClientFactory httpClientFactory,
    HotelLocation hotelLocation,
    IOptions<GeoapifyOptions> geoapifyOptions)
{
    private const string Endpoint = "https://api.geoapify.com/v2/places";
    private const int SearchRadiusMeters = 60_000;
    private const int ResultLimit = 20;

    // The Places API requires categories even for name searches. Search the categories already
    // exposed by the concierge rather than guessing the named venue's type:
    // https://apidocs.geoapify.com/docs/places/#api
    private static readonly string SearchCategories = string.Join(
        ',',
        NearbyPlaceCategories.All
            .SelectMany(category => category.GeoapifyCategories.Split(','))
            .Distinct(StringComparer.Ordinal));

    public async Task<PlaceOpeningHoursResult> FindAsync(
        string placeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placeName);
        var query = placeName.Trim();
        if (query.Length > 200)
        {
            throw new ArgumentException("Place name cannot exceed 200 characters.", nameof(placeName));
        }

        var key = geoapifyOptions.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Geoapify:ApiKey is required to look up place opening hours.");
        }

        var hotel = hotelLocation.Coordinates;
        var bias = string.Create(
            CultureInfo.InvariantCulture,
            $"proximity:{hotel.Longitude},{hotel.Latitude}");
        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"circle:{hotel.Longitude},{hotel.Latitude},{SearchRadiusMeters}");
        var uri = $"{Endpoint}?categories={Uri.EscapeDataString(SearchCategories)}"
            + $"&name={Uri.EscapeDataString(query)}&filter={Uri.EscapeDataString(filter)}"
            + $"&bias={Uri.EscapeDataString(bias)}&limit={ResultLimit}&lang=en"
            + $"&apiKey={Uri.EscapeDataString(key)}";
        using var document = await LocationProviderHttp.GetJsonAsync(
            httpClientFactory, uri, "Geoapify", cancellationToken);

        return ReadPlaces(document.RootElement, query, hotel);
    }

    internal static PlaceOpeningHoursResult ReadPlaces(
        JsonElement root,
        string query,
        HotelCoordinates hotel)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("features", out var features)
            || features.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Geoapify returned no place features.");
        }

        var requestedName = Normalize(query);
        var places = new List<PlaceRecord>();
        foreach (var feature in features.EnumerateArray())
        {
            if (feature.ValueKind != JsonValueKind.Object
                || !feature.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object
                || ReadString(properties, "name") is not { } name
                || !TryReadDouble(properties, "lat", out var latitude)
                || !TryReadDouble(properties, "lon", out var longitude)
                || latitude is < -90 or > 90
                || longitude is < -180 or > 180
                || DistanceMeters(hotel.Latitude, hotel.Longitude, latitude, longitude) > SearchRadiusMeters
                || !NamesMatch(requestedName, Normalize(name)))
            {
                continue;
            }

            var placeId = ReadString(properties, "place_id")
                ?? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name}:{latitude:F5}:{longitude:F5}");
            var hours = ReadOpeningHours(properties);
            var houseNumber = ReadString(properties, "housenumber");
            var street = ReadString(properties, "street");
            places.Add(new PlaceRecord(
                placeId,
                name,
                // Geoapify sets address_line1 to the venue name for named places, so the street
                // address is what tells nearby branches apart in a clarification.
                street is null ? null : houseNumber is null ? street : $"{houseNumber} {street}",
                PlaceIdentity.AddressKey(houseNumber, street),
                latitude,
                longitude,
                hours));
        }

        if (places.Count == 0)
        {
            return new(PlaceOpeningHoursStatus.NoResults, null, null, []);
        }

        // Geoapify can list one venue twice (an OSM node and its building way). Merge records only
        // when they share the venue name, house number, and street and lie within 75 metres,
        // preferring the record that carries opening hours. Nearby branches stay separate.
        var sites = new List<PlaceRecord>();
        foreach (var place in places
            .DistinctBy(place => place.PlaceId, StringComparer.Ordinal)
            .OrderByDescending(place => string.Equals(Normalize(place.Name), requestedName, StringComparison.Ordinal))
            .ThenByDescending(place => place.OpeningHours is not null)
            .ThenBy(place => DistanceMeters(hotel.Latitude, hotel.Longitude, place.Latitude, place.Longitude)))
        {
            if (!sites.Any(existing => IsSameVenue(existing, place)))
            {
                sites.Add(place);
            }
        }

        if (sites.Count > 1)
        {
            return new(
                PlaceOpeningHoursStatus.NeedsClarification,
                query,
                null,
                sites.Take(3).Select(place =>
                    place.Address is null ? place.Name : $"{place.Name}, {place.Address}").ToArray());
        }

        var selected = sites[0];
        return new(
            selected.OpeningHours is null
                ? PlaceOpeningHoursStatus.NotListed
                : PlaceOpeningHoursStatus.Available,
            selected.Name,
            selected.OpeningHours,
            []);
    }

    private static string? ReadOpeningHours(JsonElement properties)
    {
        var hours = ReadString(properties, "opening_hours");
        if (hours is not null)
        {
            return hours;
        }

        return properties.TryGetProperty("datasource", out var datasource)
            && datasource.ValueKind == JsonValueKind.Object
            && datasource.TryGetProperty("raw", out var raw)
            && raw.ValueKind == JsonValueKind.Object
                ? ReadString(raw, "opening_hours")
                : null;
    }

    private static bool IsSameVenue(PlaceRecord first, PlaceRecord second) =>
        first.AddressKey is not null
        && StringComparer.Ordinal.Equals(first.AddressKey, second.AddressKey)
        && StringComparer.Ordinal.Equals(Normalize(first.Name), Normalize(second.Name))
        && DistanceMeters(first, second) <= 75;

    private static bool NamesMatch(string requested, string candidate) =>
        requested.Length > 0
        && candidate.Length > 0
        && (StringComparer.Ordinal.Equals(requested, candidate)
            || candidate.Contains(requested, StringComparison.Ordinal)
            || requested.Contains(candidate, StringComparison.Ordinal));

    private static string Normalize(string value) => PlaceIdentity.NormalizeName(value);

    private static double DistanceMeters(PlaceRecord first, PlaceRecord second)
        => DistanceMeters(first.Latitude, first.Longitude, second.Latitude, second.Longitude);

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
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private sealed record PlaceRecord(
        string PlaceId,
        string Name,
        string? Address,
        string? AddressKey,
        double Latitude,
        double Longitude,
        string? OpeningHours);
}
