using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Location;

public enum WeatherDay
{
    Today,
    Tonight,
    Tomorrow,
}

public enum WeatherResultStatus
{
    Available,
    NeedsClarification,
}

public sealed record WeatherRequest(string? Place = null, WeatherDay Day = WeatherDay.Today);

public sealed record WeatherLocation(
    string Name,
    double Latitude,
    double Longitude,
    bool IsHotel);

public sealed record WeatherForecast(
    DateOnly LocalDate,
    string TimeZone,
    double? HighFahrenheit,
    double? LowFahrenheit,
    int? WeatherCode,
    double? MaxWindMph,
    int? PrecipitationProbability,
    string TemperatureUnit,
    string WindSpeedUnit);

public sealed record WeatherResult(
    WeatherResultStatus Status,
    string? ClarificationQuery,
    IReadOnlyList<ResolvedPlace> Candidates,
    WeatherLocation? Location,
    WeatherForecast? Forecast);

/// <summary>Retrieves read-only forecasts from Open-Meteo at resolved or hotel coordinates.</summary>
public sealed class OpenMeteoWeatherService(
    HttpClient httpClient,
    GeoapifyPlaceResolver placeResolver,
    IOptions<HotelLocationOptions> hotelOptions,
    TimeProvider timeProvider)
{
    private const string Endpoint = "https://api.open-meteo.com/v1/forecast";
    private const string DailyFields =
        "temperature_2m_max,temperature_2m_min,weather_code,wind_speed_10m_max,precipitation_probability_max";

    public async Task<WeatherResult> GetWeatherAsync(
        WeatherRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Day))
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Day,
                "Weather day must be Today, Tonight or Tomorrow.");
        }

        var hotel = hotelOptions.Value;
        WeatherLocation location;
        if (string.IsNullOrWhiteSpace(request.Place))
        {
            location = new WeatherLocation(
                "Hotel ZGY, Miami",
                hotel.Latitude,
                hotel.Longitude,
                IsHotel: true);
        }
        else
        {
            var resolution = await placeResolver.ResolveAsync(request.Place, cancellationToken);
            if (resolution.Status != PlaceResolutionStatus.Resolved)
            {
                return new(
                    WeatherResultStatus.NeedsClarification,
                    resolution.Query,
                    resolution.Candidates,
                    Location: null,
                    Forecast: null);
            }

            var place = resolution.Place!;
            location = new WeatherLocation(place.Formatted, place.Latitude, place.Longitude, IsHotel: false);
        }

        var hotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById(hotel.TimeZoneId);
        var hotelLocalDate = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), hotelTimeZone).DateTime);
        var targetDate = request.Day == WeatherDay.Tomorrow
            ? hotelLocalDate.AddDays(1)
            : hotelLocalDate;
        var url = $"{Endpoint}?latitude={location.Latitude.ToString(CultureInfo.InvariantCulture)}"
            + $"&longitude={location.Longitude.ToString(CultureInfo.InvariantCulture)}"
            + $"&daily={Uri.EscapeDataString(DailyFields)}"
            + "&temperature_unit=fahrenheit&wind_speed_unit=mph"
            + $"&timezone={Uri.EscapeDataString(hotel.TimeZoneId)}&forecast_days=7";
        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await LocationProviderHttp.SendAsync(
            httpClient,
            requestMessage,
            "Open-Meteo",
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);

        var forecast = ReadForecast(document.RootElement, targetDate, hotel.TimeZoneId);
        return new(
            WeatherResultStatus.Available,
            ClarificationQuery: null,
            [],
            location,
            forecast);
    }

    private static WeatherForecast ReadForecast(
        JsonElement root,
        DateOnly targetDate,
        string expectedTimeZone)
    {
        var responseTimeZone = ReadRequiredString(root, "timezone");
        if (!string.Equals(responseTimeZone, expectedTimeZone, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Open-Meteo returned a forecast for an unexpected time zone.");
        }

        var dailyUnits = root.GetProperty("daily_units");
        var temperatureUnit = ReadRequiredString(dailyUnits, "temperature_2m_max");
        var windSpeedUnit = ReadRequiredString(dailyUnits, "wind_speed_10m_max");
        if (temperatureUnit != "°F" || windSpeedUnit != "mp/h")
        {
            throw new InvalidDataException("Open-Meteo did not return the requested Fahrenheit and mph units.");
        }

        var daily = root.GetProperty("daily");
        var dates = daily.GetProperty("time").EnumerateArray()
            .Select(value => DateOnly.Parse(value.GetString()!, CultureInfo.InvariantCulture))
            .ToArray();
        var index = Array.IndexOf(dates, targetDate);
        if (index < 0)
        {
            throw new InvalidDataException(
                $"Open-Meteo returned no forecast for hotel-local date {targetDate:yyyy-MM-dd}.");
        }

        return new WeatherForecast(
            targetDate,
            responseTimeZone,
            ReadDoubleAt(daily, "temperature_2m_max", index),
            ReadDoubleAt(daily, "temperature_2m_min", index),
            ReadIntAt(daily, "weather_code", index),
            ReadDoubleAt(daily, "wind_speed_10m_max", index),
            ReadIntAt(daily, "precipitation_probability_max", index),
            temperatureUnit,
            "mph");
    }

    private static string ReadRequiredString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"Open-Meteo response is missing '{propertyName}'.");

    private static double? ReadDoubleAt(JsonElement element, string propertyName, int index)
    {
        var array = element.GetProperty(propertyName);
        return index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number
            ? array[index].GetDouble()
            : null;
    }

    private static int? ReadIntAt(JsonElement element, string propertyName, int index)
    {
        var array = element.GetProperty(propertyName);
        return index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number
            ? array[index].GetInt32()
            : null;
    }
}
