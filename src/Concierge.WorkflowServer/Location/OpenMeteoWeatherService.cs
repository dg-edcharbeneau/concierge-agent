using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Location;

public enum WeatherDay
{
    Today,
    Tonight,
    Tomorrow,
    Current,
}

public enum WeatherResultStatus
{
    Available,
    NeedsClarification,

    /// <summary>The requested day is past the forecast horizon; <see cref="WeatherResult.RequestedDate"/> names it.</summary>
    BeyondForecast,

    /// <summary>
    /// The requested weekend day has already passed (Saturday, asked on Sunday);
    /// <see cref="WeatherResult.RequestedDate"/> names it.
    /// </summary>
    Elapsed,
}

/// <param name="DaysAhead">
/// Overrides <paramref name="Day"/> with a hotel-local day offset (0-6) for multi-day forecasts.
/// </param>
/// <param name="StartWeekday">
/// Counts <paramref name="DaysAhead"/> from this weekday's next hotel-local occurrence (today
/// included) instead of from today.
/// </param>
/// <param name="WeekendDay">
/// 0 for Saturday, 1 for Sunday of the current weekend (on Saturday or Sunday) or the coming one
/// (Monday to Friday). Overrides the other day fields.
/// </param>
/// <param name="AsOfUtc">
/// The instant that defines "today". The workflow passes its turn start so every call in a turn
/// resolves against the same hotel-local date, even across midnight.
/// </param>
public sealed record WeatherRequest(
    string? Place = null,
    WeatherDay Day = WeatherDay.Today,
    int? DaysAhead = null,
    DayOfWeek? StartWeekday = null,
    int? WeekendDay = null,
    DateTimeOffset? AsOfUtc = null);

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

public sealed record WeatherCurrentObservation(
    DateTimeOffset ObservedAt,
    double? TemperatureFahrenheit,
    double? ApparentTemperatureFahrenheit,
    int? RelativeHumidityPercent,
    int? WeatherCode,
    double? WindSpeedMph,
    double? PrecipitationInches);

public sealed record WeatherResult(
    WeatherResultStatus Status,
    string? ClarificationQuery,
    IReadOnlyList<ResolvedPlace> Candidates,
    WeatherLocation? Location,
    WeatherForecast? Forecast,
    DateOnly? RequestedDate = null,
    WeatherCurrentObservation? Current = null);

/// <summary>Retrieves read-only current conditions and forecasts from Open-Meteo.</summary>
public sealed class OpenMeteoWeatherService(
    IHttpClientFactory httpClientFactory,
    GeoapifyPlaceResolver placeResolver,
    IOptions<HotelLocationOptions> hotelOptions,
    HotelLocation hotelLocation,
    TimeProvider timeProvider)
{
    // forecast_days=7 covers today plus six days; past_days=1 keeps a turn anchored just before
    // hotel-local midnight answerable when its activity runs just after.
    public const int MaxDaysAhead = 6;
    private const string Endpoint = "https://api.open-meteo.com/v1/forecast";
    private const string DailyFields =
        "temperature_2m_max,temperature_2m_min,weather_code,wind_speed_10m_max,precipitation_probability_max";
    private const string CurrentFields =
        "temperature_2m,relative_humidity_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m";

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
                "Weather period must be Current, Today, Tonight or Tomorrow.");
        }

        if (request.DaysAhead is < 0 or > MaxDaysAhead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.DaysAhead,
                $"Weather days ahead must be between 0 and {MaxDaysAhead}.");
        }

        if (request.StartWeekday is { } weekday && !Enum.IsDefined(weekday))
        {
            throw new ArgumentOutOfRangeException(nameof(request), weekday, "Weather start weekday is not a valid day.");
        }

        if (request.WeekendDay is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), request.WeekendDay, "Weather weekend day must be 0 (Saturday) or 1 (Sunday).");
        }
        if (request.Day == WeatherDay.Current
            && (request.DaysAhead is not null || request.StartWeekday is not null || request.WeekendDay is not null))
        {
            throw new ArgumentException("Current weather cannot be combined with a forecast day.", nameof(request));
        }

        var hotel = hotelOptions.Value;
        var hotelTimeZone = TimeZoneInfo.FindSystemTimeZoneById(hotel.TimeZoneId);
        var asOfUtc = request.AsOfUtc ?? timeProvider.GetUtcNow();
        var hotelLocalDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(asOfUtc, hotelTimeZone).DateTime);
        if (request.WeekendDay is { } weekendDay)
        {
            var saturdayOffset = hotelLocalDate.DayOfWeek == DayOfWeek.Sunday
                ? -1
                : DayOfWeek.Saturday - hotelLocalDate.DayOfWeek;
            var weekendOffset = saturdayOffset + weekendDay;
            if (weekendOffset < 0)
            {
                return new(WeatherResultStatus.Elapsed, null, [], null, null, hotelLocalDate.AddDays(weekendOffset));
            }

            return await GetWeatherAsync(
                request with { WeekendDay = null, StartWeekday = null, DaysAhead = weekendOffset, AsOfUtc = asOfUtc },
                cancellationToken);
        }

        var daysAhead = request.StartWeekday is { } startWeekday
            ? ((int)startWeekday - (int)hotelLocalDate.DayOfWeek + 7) % 7 + (request.DaysAhead ?? 0)
            : request.DaysAhead ?? (request.Day == WeatherDay.Tomorrow ? 1 : 0);
        var targetDate = hotelLocalDate.AddDays(daysAhead);
        if (request.Day != WeatherDay.Current && daysAhead > MaxDaysAhead)
        {
            return new(WeatherResultStatus.BeyondForecast, null, [], null, null, targetDate);
        }

        WeatherLocation location;
        if (string.IsNullOrWhiteSpace(request.Place)
            || HotelLocationOptions.IsHotelReference(request.Place))
        {
            location = new WeatherLocation(
                hotel.Label,
                hotelLocation.Coordinates.Latitude,
                hotelLocation.Coordinates.Longitude,
                IsHotel: true);
        }
        else
        {
            var resolution = await placeResolver.ResolveForWeatherAsync(request.Place, cancellationToken);
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

        var url = $"{Endpoint}?latitude={location.Latitude.ToString(CultureInfo.InvariantCulture)}"
            + $"&longitude={location.Longitude.ToString(CultureInfo.InvariantCulture)}"
            + (request.Day == WeatherDay.Current
                ? $"&current={Uri.EscapeDataString(CurrentFields)}"
                : $"&daily={Uri.EscapeDataString(DailyFields)}")
            + "&temperature_unit=fahrenheit&wind_speed_unit=mph"
            + (request.Day == WeatherDay.Current
                ? "&precipitation_unit=inch&timezone=auto"
                : $"&timezone={Uri.EscapeDataString(hotel.TimeZoneId)}&forecast_days=7&past_days=1");
        using var document = await LocationProviderHttp.GetJsonAsync(
            httpClientFactory, url, "Open-Meteo", cancellationToken);

        if (request.Day == WeatherDay.Current)
        {
            var current = ReadCurrentObservation(document.RootElement);
            return new(
                WeatherResultStatus.Available,
                ClarificationQuery: null,
                [],
                location,
                Forecast: null,
                RequestedDate: DateOnly.FromDateTime(current.ObservedAt.DateTime),
                Current: current);
        }

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

    private static WeatherCurrentObservation ReadCurrentObservation(JsonElement root)
    {
        _ = ReadRequiredString(root, "timezone");
        if (!root.TryGetProperty("utc_offset_seconds", out var offsetElement)
            || offsetElement.ValueKind != JsonValueKind.Number
            || !offsetElement.TryGetInt32(out var offsetSeconds)
            || Math.Abs((long)offsetSeconds) > TimeSpan.FromHours(14).TotalSeconds)
        {
            throw new InvalidDataException("Open-Meteo returned an invalid current-observation time-zone offset.");
        }

        var units = root.GetProperty("current_units");
        if (ReadRequiredString(units, "temperature_2m") != "°F"
            || ReadRequiredString(units, "relative_humidity_2m") != "%"
            || ReadRequiredString(units, "apparent_temperature") != "°F"
            || ReadRequiredString(units, "precipitation") != "inch"
            || ReadRequiredString(units, "wind_speed_10m") != "mp/h")
        {
            throw new InvalidDataException("Open-Meteo did not return the requested current weather units.");
        }

        var current = root.GetProperty("current");
        var localTimeText = ReadRequiredString(current, "time");
        if (!DateTime.TryParseExact(
                localTimeText,
                "yyyy-MM-dd'T'HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTime))
        {
            throw new InvalidDataException("Open-Meteo returned an invalid current-observation local time.");
        }

        var observedAt = new DateTimeOffset(
            DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified),
            TimeSpan.FromSeconds(offsetSeconds));
        return new WeatherCurrentObservation(
            observedAt,
            ReadDouble(current, "temperature_2m"),
            ReadDouble(current, "apparent_temperature"),
            ReadInt(current, "relative_humidity_2m"),
            ReadInt(current, "weather_code"),
            ReadDouble(current, "wind_speed_10m"),
            ReadDouble(current, "precipitation"));
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

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
    }
}
