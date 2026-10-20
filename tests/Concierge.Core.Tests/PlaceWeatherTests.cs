using System.Net;
using System.Text;
using System.Text.Json;
using Concierge.WorkflowServer.Location;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Concierge.Core.Tests;

public sealed class PlaceWeatherTests
{
    private static readonly HotelLocationOptions Hotel = new()
    {
        Latitude = 25.7984634,
        Longitude = -80.1956016,
        TimeZoneId = "America/New_York",
    };

    private static readonly GeoapifyOptions Geoapify = new()
    {
        ApiKey = "test-key",
    };

    [Fact]
    public async Task SouthBeachSelectsMiamiResultEvenWhenOregonRanksFirst()
    {
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new[]
            {
                GeoResult("South Beach, Oregon, United States", 43.36, -124.22, "Oregon", "Coos County", "US", .99),
                GeoResult("South Beach, Miami Beach, Miami-Dade County, Florida, United States",
                    25.7745, -80.1329, "Florida", "Miami-Dade County", "US", .82),
            },
        }));
        var resolver = CreateResolver(http);

        var result = await resolver.ResolveAsync("South Beach");

        Assert.Equal(PlaceResolutionStatus.Resolved, result.Status);
        Assert.Equal("South Beach, Miami Beach, Miami-Dade County, Florida, United States",
            result.Place!.Formatted);
        Assert.Equal(25.7745, result.Place.Latitude);
        Assert.Equal(-80.1329, result.Place.Longitude);
        Assert.Contains("bias=proximity", http.LastRequestUri!.Query);
        Assert.Contains("-80.1956016", http.LastRequestUri.Query);
        Assert.Contains("25.7984634", http.LastRequestUri.Query);
    }

    [Fact]
    public async Task AirportWithSeveralLocalMatchesRequiresClarification()
    {
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new[]
            {
                GeoResult("Miami International Airport", 25.7959, -80.2870, "Florida", "Miami-Dade County", "US", .91),
                GeoResult("Miami Executive Airport", 25.6479, -80.4328, "Florida", "Miami-Dade County", "US", .88),
            },
        }));
        var resolver = CreateResolver(http);

        var result = await resolver.ResolveAsync("airport");

        Assert.Equal(PlaceResolutionStatus.NeedsClarification, result.Status);
        Assert.Null(result.Place);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task OutOfAreaAndMissingResultsDoNotGuess()
    {
        using var outsideHttp = CreateClient(_ => JsonResponse(new
        {
            results = new[]
            {
                GeoResult("South Beach, Oregon, United States", 43.36, -124.22, "Oregon", "Coos County", "US", .99),
            },
        }));
        using var emptyHttp = CreateClient(_ => JsonResponse(new { results = Array.Empty<object>() }));

        var outside = await CreateResolver(outsideHttp).ResolveAsync("South Beach");
        var empty = await CreateResolver(emptyHttp).ResolveAsync("not-a-place");

        Assert.Equal(PlaceResolutionStatus.NeedsClarification, outside.Status);
        Assert.Empty(outside.Candidates);
        Assert.Equal(PlaceResolutionStatus.NeedsClarification, empty.Status);
    }

    [Fact]
    public async Task MalformedProviderResultsAreNotPromotedToConfidentMatches()
    {
        using var missingConfidenceHttp = CreateClient(_ => JsonResponse(new
        {
            results = new[]
            {
                new
                {
                    place_id = "missing-confidence",
                    formatted = "South Beach, Miami-Dade County, Florida, United States",
                    lat = 25.7745,
                    lon = -80.1329,
                    state = "Florida",
                    county = "Miami-Dade County",
                    country_code = "us",
                },
            },
        }));
        using var invalidCoordinatesHttp = CreateClient(_ => JsonResponse(new
        {
            features = new[]
            {
                new
                {
                    properties = new
                    {
                        place_id = "invalid-coordinates",
                        formatted = "South Beach, Miami-Dade County, Florida, United States",
                        state = "Florida",
                        county = "Miami-Dade County",
                        country_code = "us",
                        rank = new { confidence = 0.99 },
                    },
                    geometry = new { coordinates = new[] { -80.1329, 95.0 } },
                },
            },
        }));

        var missingConfidence = await CreateResolver(missingConfidenceHttp).ResolveAsync("South Beach");
        var invalidCoordinates = await CreateResolver(invalidCoordinatesHttp).ResolveAsync("South Beach");

        Assert.Equal(PlaceResolutionStatus.NeedsClarification, missingConfidence.Status);
        Assert.Empty(missingConfidence.Candidates);
        Assert.Equal(PlaceResolutionStatus.NeedsClarification, invalidCoordinates.Status);
        Assert.Empty(invalidCoordinates.Candidates);
    }

    [Fact]
    public async Task AmbiguousWeatherPlaceReturnsClarificationWithoutCallingOpenMeteo()
    {
        var openMeteoCalls = 0;
        using var http = CreateClient(request =>
        {
            if (request.RequestUri!.Host == "api.geoapify.com")
            {
                return JsonResponse(new
                {
                    results = new[]
                    {
                        GeoResult("Miami International Airport", 25.7959, -80.2870,
                            "Florida", "Miami-Dade County", "US", .91),
                        GeoResult("Miami Executive Airport", 25.6479, -80.4328,
                            "Florida", "Miami-Dade County", "US", .88),
                    },
                });
            }

            openMeteoCalls++;
            return WeatherResponse(["2026-10-19"]);
        });
        var service = CreateWeatherService(
            http,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 16, 0, 0, TimeSpan.Zero)));

        var result = await service.GetWeatherAsync(new WeatherRequest("airport"));

        Assert.Equal(WeatherResultStatus.NeedsClarification, result.Status);
        Assert.Equal("airport", result.ClarificationQuery);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Null(result.Forecast);
        Assert.Equal(0, openMeteoCalls);
    }

    [Fact]
    public async Task WeatherForNamedPlaceUsesResolvedCoordinatesAndRequiredUnitsAndHotelTimeZone()
    {
        var weatherUri = (Uri?)null;
        using var http = CreateClient(request =>
        {
            if (request.RequestUri!.Host == "api.geoapify.com")
            {
                return JsonResponse(new
                {
                    results = new[]
                    {
                        GeoResult("South Beach, Miami Beach, Florida", 25.7745, -80.1329,
                            "Florida", "Miami-Dade County", "US", .95),
                    },
                });
            }

            weatherUri = request.RequestUri;
            return WeatherResponse(["2026-10-19", "2026-10-20"]);
        });
        var service = CreateWeatherService(
            http,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 16, 0, 0, TimeSpan.Zero)));

        var result = await service.GetWeatherAsync(new WeatherRequest("South Beach"));

        Assert.Equal(WeatherResultStatus.Available, result.Status);
        Assert.Equal(25.7745, result.Location!.Latitude);
        Assert.Equal(-80.1329, result.Location.Longitude);
        Assert.Equal(new DateOnly(2026, 10, 19), result.Forecast!.LocalDate);
        Assert.Equal("America/New_York", result.Forecast.TimeZone);
        Assert.Equal("°F", result.Forecast.TemperatureUnit);
        Assert.Equal("mph", result.Forecast.WindSpeedUnit);
        Assert.Contains("latitude=25.7745", weatherUri!.Query);
        Assert.Contains("longitude=-80.1329", weatherUri.Query);
        Assert.Contains("temperature_unit=fahrenheit", weatherUri.Query);
        Assert.Contains("wind_speed_unit=mph", weatherUri.Query);
        Assert.Contains("timezone=America%2FNew_York", weatherUri.Query);
        Assert.DoesNotContain("apiKey=", weatherUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WeatherWithoutPlaceUsesHotelCoordinatesWithoutCallingGeoapify()
    {
        var geoapifyCalls = 0;
        Uri? weatherUri = null;
        using var http = CreateClient(request =>
        {
            if (request.RequestUri!.Host == "api.geoapify.com")
            {
                geoapifyCalls++;
                return JsonResponse(new { results = Array.Empty<object>() });
            }

            weatherUri = request.RequestUri;
            return WeatherResponse(["2026-10-19", "2026-10-20"]);
        });
        var service = CreateWeatherService(
            http,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 16, 0, 0, TimeSpan.Zero)));

        var result = await service.GetWeatherAsync(new WeatherRequest());

        Assert.Equal(WeatherResultStatus.Available, result.Status);
        Assert.True(result.Location!.IsHotel);
        Assert.Equal(Hotel.Latitude, result.Location.Latitude);
        Assert.Equal(Hotel.Longitude, result.Location.Longitude);
        Assert.Equal(0, geoapifyCalls);
        Assert.Contains($"latitude={Hotel.Latitude}", weatherUri!.Query);
        Assert.Contains($"longitude={Hotel.Longitude}", weatherUri.Query);
    }

    [Fact]
    public async Task TodayAndTomorrowFollowHotelLocalCalendarDateAcrossUtcDateBoundary()
    {
        var requestedDates = new List<string>();
        using var http = CreateClient(request =>
        {
            requestedDates.Add(request.RequestUri!.Host == "api.open-meteo.com"
                ? Uri.UnescapeDataString(request.RequestUri.Query)
                : "geocode");
            return WeatherResponse(["2026-10-31", "2026-11-01"]);
        });
        // At 03:30Z the hotel is still on October 31 in Miami.
        var service = CreateWeatherService(
            http,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 3, 30, 0, TimeSpan.Zero)));

        var today = await service.GetWeatherAsync(new WeatherRequest(Day: WeatherDay.Today));
        var tonight = await service.GetWeatherAsync(new WeatherRequest(Day: WeatherDay.Tonight));
        var tomorrow = await service.GetWeatherAsync(new WeatherRequest(Day: WeatherDay.Tomorrow));

        Assert.Equal(new DateOnly(2026, 10, 31), today.Forecast!.LocalDate);
        Assert.Equal(today.Forecast.LocalDate, tonight.Forecast!.LocalDate);
        Assert.Equal(new DateOnly(2026, 11, 1), tomorrow.Forecast!.LocalDate);
        Assert.Equal(3, requestedDates.Count);
    }

    [Fact]
    public async Task UndefinedWeatherDayIsRejectedBeforeAnyProviderCall()
    {
        var calls = 0;
        using var http = CreateClient(_ =>
        {
            calls++;
            return WeatherResponse(["2026-10-19"]);
        });
        var service = CreateWeatherService(
            http,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 16, 0, 0, TimeSpan.Zero)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.GetWeatherAsync(new WeatherRequest(Day: (WeatherDay)99)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task WeatherProviderFailureIsSafeToRetryAndDoesNotExposeGeoapifyKey()
    {
        var calls = 0;
        using var http = CreateClient(_ =>
        {
            calls++;
            return calls == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : WeatherResponse(["2026-10-19"]);
        });
        var service = CreateWeatherService(
            http,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 16, 0, 0, TimeSpan.Zero)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetWeatherAsync(new WeatherRequest()));
        var retry = await service.GetWeatherAsync(new WeatherRequest());

        Assert.DoesNotContain(Geoapify.ApiKey, failure.Message);
        Assert.Equal(WeatherResultStatus.Available, retry.Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GeoapifyProviderFailureDoesNotExposeApiKey()
    {
        using var http = CreateClient(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("provider diagnostic"),
            });

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateResolver(http).ResolveAsync("South Beach"));

        Assert.Contains("403", failure.Message);
        Assert.DoesNotContain(Geoapify.ApiKey, failure.Message);
    }

    [Fact]
    public void LocationOptionsRequireValidCoordinatesTimeZoneAndGeoapifySecret()
    {
        var validator = new LocationOptionsValidator();

        Assert.True(validator.Validate(null, Hotel).Succeeded);
        Assert.True(validator.Validate(null, Geoapify).Succeeded);
        Assert.Contains("Latitude", validator.Validate(
            null, new HotelLocationOptions { Latitude = 91, Longitude = 0, TimeZoneId = Hotel.TimeZoneId })
            .FailureMessage);
        Assert.Contains("TimeZoneId", validator.Validate(
            null, new HotelLocationOptions { Latitude = 0, Longitude = 0, TimeZoneId = "not-a-zone" })
            .FailureMessage);
        Assert.Contains("ApiKey", validator.Validate(null, new GeoapifyOptions()).FailureMessage);
    }

    private static GeoapifyPlaceResolver CreateResolver(HttpClient http) =>
        new(http, Options.Create(Hotel), Options.Create(Geoapify));

    private static OpenMeteoWeatherService CreateWeatherService(HttpClient http, TimeProvider timeProvider)
    {
        var resolver = CreateResolver(http);
        return new OpenMeteoWeatherService(
            http,
            resolver,
            Options.Create(Hotel),
            timeProvider);
    }

    private static RecordingHttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new DelegateHttpMessageHandler(responder);
        var client = new HttpClient(handler);
        return new RecordingHttpClient(client, handler);
    }

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage WeatherResponse(string[] dates) =>
        JsonResponse(new
        {
            timezone = Hotel.TimeZoneId,
            daily_units = new
            {
                time = "iso8601",
                temperature_2m_max = "°F",
                temperature_2m_min = "°F",
                weather_code = "wmo code",
                wind_speed_10m_max = "mp/h",
                precipitation_probability_max = "%",
            },
            daily = new
            {
                time = dates,
                temperature_2m_max = dates.Select(_ => (double?)84).ToArray(),
                temperature_2m_min = dates.Select(_ => (double?)72).ToArray(),
                weather_code = dates.Select(_ => (int?)2).ToArray(),
                wind_speed_10m_max = dates.Select(_ => (double?)11).ToArray(),
                precipitation_probability_max = dates.Select(_ => (int?)20).ToArray(),
            },
        });

    private static object GeoResult(
        string formatted,
        double latitude,
        double longitude,
        string state,
        string county,
        string countryCode,
        double confidence) =>
        new
        {
            place_id = $"{latitude},{longitude}",
            formatted,
            lat = latitude,
            lon = longitude,
            state,
            county,
            country_code = countryCode,
            rank = new { confidence },
        };

    private sealed class RecordingHttpClient(HttpClient client, DelegateHttpMessageHandler handler)
        : IDisposable
    {
        private readonly HttpClient clientValue = client;

        public Uri? LastRequestUri => handler.LastRequestUri;

        public static implicit operator HttpClient(RecordingHttpClient recording) => recording.clientValue;

        public void Dispose() => clientValue.Dispose();
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(responder(request));
        }
    }
}
