using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Location;

public sealed class HotelLocationOptions
{
    public const string SectionName = "Hotel";

    public string TimeZoneId { get; set; } = string.Empty;

    public const string DisplayName = "Hotel ZGY";

    /// <summary>Street address of the real location the fictional hotel is pinned to.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Guest-facing label that names the real address used for weather, routes, and rides.</summary>
    public string Label => string.IsNullOrWhiteSpace(Address) ? DisplayName : $"{DisplayName}, {Address}";

    // Hotel ZGY is fictional, so a geocoder cannot resolve it; the model naturally names it.
    private static readonly HashSet<string> HotelReferences = new(StringComparer.Ordinal)
    {
        "hotel", "the hotel", "my hotel", "our hotel", "zgy", "hotel zgy", "the hotel zgy",
        "hotel zgy miami", "zgy hotel",
    };

    public static bool IsHotelReference(string? place)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return false;
        }

        var normalized = string.Join(
            ' ',
            new string(place.ToLowerInvariant()
                    .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
                    .ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return HotelReferences.Contains(normalized);
    }
}

public sealed class GeoapifyOptions
{
    public const string SectionName = "Geoapify";

    public string ApiKey { get; set; } = string.Empty;
}

public sealed class LocationOptionsValidator :
    IValidateOptions<HotelLocationOptions>,
    IValidateOptions<GeoapifyOptions>
{
    public ValidateOptionsResult Validate(string? name, HotelLocationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Address))
        {
            return ValidateOptionsResult.Fail(
                $"{HotelLocationOptions.SectionName}:Address is required for startup geocoding.");
        }

        if (string.IsNullOrWhiteSpace(options.TimeZoneId))
        {
            return ValidateOptionsResult.Fail(
                $"{HotelLocationOptions.SectionName}:TimeZoneId is required.");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return ValidateOptionsResult.Fail(
                $"{HotelLocationOptions.SectionName}:TimeZoneId must be a time zone supported by this host.");
        }
        catch (InvalidTimeZoneException)
        {
            return ValidateOptionsResult.Fail(
                $"{HotelLocationOptions.SectionName}:TimeZoneId must be a valid time zone.");
        }

        return ValidateOptionsResult.Success;
    }

    public ValidateOptionsResult Validate(string? name, GeoapifyOptions options) =>
        string.IsNullOrWhiteSpace(options.ApiKey)
            ? ValidateOptionsResult.Fail($"{GeoapifyOptions.SectionName}:ApiKey is required.")
            : ValidateOptionsResult.Success;
}
