namespace Concierge.WorkflowServer.Location;

/// <summary>
/// Compares Geoapify venue names and street addresses. Geoapify spells one street differently
/// across records (building "NW 24th Street", venue "Northwest 24th Street"), so streets are
/// compared after expanding common US abbreviations.
/// </summary>
internal static class PlaceIdentity
{
    private static readonly Dictionary<string, string> StreetAbbreviations = new(StringComparer.Ordinal)
    {
        ["n"] = "north", ["s"] = "south", ["e"] = "east", ["w"] = "west",
        ["ne"] = "northeast", ["nw"] = "northwest", ["se"] = "southeast", ["sw"] = "southwest",
        ["st"] = "street", ["ave"] = "avenue", ["av"] = "avenue", ["blvd"] = "boulevard",
        ["rd"] = "road", ["dr"] = "drive", ["ct"] = "court", ["pl"] = "place", ["ln"] = "lane",
        ["hwy"] = "highway", ["pkwy"] = "parkway", ["ter"] = "terrace", ["cswy"] = "causeway",
    };

    internal static string NormalizeName(string? value) =>
        value is null ? string.Empty : string.Concat(value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    /// <summary>Lower-cased words with street abbreviations expanded, space-separated.</summary>
    internal static string NormalizeAddressText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var words = new string(value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => StreetAbbreviations.GetValueOrDefault(word, word));
        return string.Join(' ', words);
    }

    /// <summary>A comparable "housenumber street" key, or null when either part is missing.</summary>
    internal static string? AddressKey(string? houseNumber, string? street)
    {
        var number = NormalizeAddressText(houseNumber);
        var normalizedStreet = NormalizeAddressText(street);
        return number.Length == 0 || normalizedStreet.Length == 0 ? null : $"{number} {normalizedStreet}";
    }

    /// <summary>True when the request text names this house number and street.</summary>
    internal static bool RequestNamesAddress(string request, string? houseNumber, string? street) =>
        RequestNamesAddressKey(request, AddressKey(houseNumber, street));

    /// <summary>True when the request text contains this <see cref="AddressKey"/>.</summary>
    internal static bool RequestNamesAddressKey(string request, string? addressKey) =>
        addressKey is { Length: > 0 }
        && $" {NormalizeAddressText(request)} ".Contains($" {addressKey} ", StringComparison.Ordinal);

    /// <summary>True when the request text contains this venue name.</summary>
    internal static bool RequestNamesVenue(string request, string? name) =>
        NormalizeName(name) is { Length: > 0 } normalized
        && NormalizeName(request).Contains(normalized, StringComparison.Ordinal);
}
