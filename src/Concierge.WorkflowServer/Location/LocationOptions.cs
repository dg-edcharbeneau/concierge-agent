using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Location;

public sealed class HotelLocationOptions
{
    public const string SectionName = "Hotel";

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public string TimeZoneId { get; set; } = string.Empty;
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
        if (!double.IsFinite(options.Latitude) || options.Latitude is < -90 or > 90)
        {
            return ValidateOptionsResult.Fail(
                $"{HotelLocationOptions.SectionName}:Latitude must be between -90 and 90.");
        }

        if (!double.IsFinite(options.Longitude) || options.Longitude is < -180 or > 180)
        {
            return ValidateOptionsResult.Fail(
                $"{HotelLocationOptions.SectionName}:Longitude must be between -180 and 180.");
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
