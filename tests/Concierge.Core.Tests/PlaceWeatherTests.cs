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
        TimeZoneId = "America/New_York",
        Address = "2217 NW Miami Court, Miami, FL 33127",
    };

    private static readonly HotelCoordinates Coordinates = new(25.7984634, -80.1956016);

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
    public async Task NamedPlaceWithBuildingLevelAddressMatchResolvesDespiteZeroOverallConfidence()
    {
        // Shape recorded from Geoapify for "Walgreens, 3103 Biscayne Boulevard" (no city).
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new object[]
            {
                RankedResult("walgreens-biscayne", "Walgreens, 3103 Biscayne Boulevard, Miami, FL 33137, United States of America",
                    25.8063, -80.1897, new { confidence = 0, confidence_street_level = 1, confidence_building_level = 1, match_type = "full_match" },
                    name: "Walgreens", housenumber: "3103", street: "Biscayne Boulevard"),
                RankedResult("walgreens-flagler", "Walgreens, East Flagler Street, Miami, FL 33131, United States of America",
                    25.7743, -80.1906, new { confidence = 0, confidence_street_level = 0, match_type = "full_match" },
                    name: "Walgreens", street: "East Flagler Street"),
                RankedResult("walgreens-54th", "Walgreens, 161 Northeast 54th Street, Miami, FL 33137, United States of America",
                    25.8255, -80.1937, new { confidence = 0, confidence_street_level = 0, confidence_building_level = 0, match_type = "full_match" },
                    name: "Walgreens", housenumber: "161", street: "Northeast 54th Street"),
                RankedResult("walgreens-west-flagler", "Walgreens, 2700 West Flagler Street, Miami, FL 33135, United States of America",
                    25.7726, -80.2408, new { confidence = 0, confidence_street_level = 0.125, confidence_building_level = 0.0019, match_type = "full_match" },
                    name: "Walgreens", housenumber: "2700", street: "West Flagler Street"),
            },
        }));

        var result = await CreateResolver(http).ResolveAsync("Walgreens, 3103 Biscayne Boulevard");

        Assert.Equal(PlaceResolutionStatus.Resolved, result.Status);
        Assert.Equal("walgreens-biscayne", result.Place!.PlaceId);
        Assert.Equal(1, result.Place.Confidence);
    }

    [Fact]
    public async Task RecordsForOneSiteResolveToTheNamedVenue()
    {
        // Shape recorded from Geoapify on 2026-10-22 for "Wayan Miami, 50 Northwest 24th Street".
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new object[]
            {
                RankedResult("building-a", "50 NW 24th Street, Miami, FL 33127, United States of America",
                    25.799374, -80.195962, new { confidence = 0.9, confidence_building_level = 1, match_type = "match_by_building" },
                    housenumber: "50", street: "NW 24th Street"),
                RankedResult("building-b", "50 NW 24th Street, Miami, FL 331270000, United States of America",
                    25.799768, -80.196055, new { confidence = 0.9, confidence_building_level = 1, match_type = "match_by_building" },
                    housenumber: "50", street: "NW 24th Street"),
                RankedResult("wayan", "Wayan Miami, 50 Northwest 24th Street, Miami, FL 33127, United States of America",
                    25.799596, -80.195854, new { confidence = 0, confidence_building_level = 1, match_type = "full_match" },
                    name: "Wayan Miami", housenumber: "50", street: "Northwest 24th Street"),
            },
        }));

        var result = await CreateResolver(http).ResolveAsync("Wayan Miami, 50 Northwest 24th Street");

        Assert.Equal(PlaceResolutionStatus.Resolved, result.Status);
        Assert.Equal("wayan", result.Place!.PlaceId);
        Assert.Equal("Wayan Miami", result.Place.Name);
        Assert.Single(result.Candidates);
    }

    [Theory]
    // The request names the venue, so the nameless records at its address are its building.
    [InlineData("Wayan Miami", PlaceResolutionStatus.Resolved, "wayan")]
    // The request names only the address, so the address record is the destination.
    [InlineData("50 NW 24th St", PlaceResolutionStatus.Resolved, "building-a")]
    // The request names neither: sharing an address is not evidence the guest meant this venue.
    [InlineData("dinner in Wynwood", PlaceResolutionStatus.NeedsClarification, null)]
    public async Task SameSiteRecordsMergeOnlyOnTheIdentityTheRequestNames(
        string query, PlaceResolutionStatus expectedStatus, string? expectedPlaceId)
    {
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new object[]
            {
                RankedResult("building-a", "50 NW 24th Street, Miami, FL 33127, United States of America",
                    25.799374, -80.195962, new { confidence = 0.9, match_type = "match_by_building" },
                    housenumber: "50", street: "NW 24th Street"),
                RankedResult("wayan", "Wayan Miami, 50 Northwest 24th Street, Miami, FL 33127, United States of America",
                    25.799596, -80.195854, new { confidence = 0.8, match_type = "full_match" },
                    name: "Wayan Miami", housenumber: "50", street: "Northwest 24th Street"),
            },
        }));

        var result = await CreateResolver(http).ResolveAsync(query);

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(expectedPlaceId, result.Place?.PlaceId);
        Assert.Equal(expectedPlaceId is null ? 2 : 1, result.Candidates.Count);
    }

    [Fact]
    public async Task RequestedVenueSharingAnAddressWithAnotherVenueNeedsClarification()
    {
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new object[]
            {
                RankedResult("wayan", "Wayan Miami, 50 Northwest 24th Street, Miami, FL 33127, United States of America",
                    25.799596, -80.195854, new { confidence = 0.9, match_type = "full_match" },
                    name: "Wayan Miami", housenumber: "50", street: "Northwest 24th Street"),
                RankedResult("bar", "Wayan Bar, 50 Northwest 24th Street, Miami, FL 33127, United States of America",
                    25.799500, -80.195900, new { confidence = 0.8, match_type = "full_match" },
                    name: "Wayan Bar", housenumber: "50", street: "Northwest 24th Street"),
            },
        }));

        var result = await CreateResolver(http).ResolveAsync("Wayan Miami, 50 NW 24th St");

        Assert.Equal(PlaceResolutionStatus.NeedsClarification, result.Status);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task CandidatesAtSeparateSitesStillNeedClarification()
    {
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new object[]
            {
                RankedResult("site-a", "Cafe One, 100 NW 24th Street, Miami, FL 33127, United States of America",
                    25.7994, -80.1960, new { confidence = 0.9, match_type = "full_match" }, name: "Cafe One"),
                RankedResult("site-b", "Cafe One, 900 Brickell Avenue, Miami, FL 33131, United States of America",
                    25.7650, -80.1920, new { confidence = 0.9, match_type = "full_match" }, name: "Cafe One"),
            },
        }));

        var result = await CreateResolver(http).ResolveAsync("Cafe One");

        Assert.Equal(PlaceResolutionStatus.NeedsClarification, result.Status);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Theory]
    // Two different venues on neighbouring lots, ~35 m apart.
    [InlineData("Panther Coffee", "2390", "NW 2nd Avenue", "Zak the Baker", "295", "NW 26th Street")]
    // Two venues sharing one building address but with different names (a food hall).
    [InlineData("Panther Coffee", "50", "NW 24th Street", "Wayan Miami", "50", "NW 24th Street")]
    // Two branches of one chain at different addresses, ~35 m apart.
    [InlineData("Cafe One", "100", "NW 24th Street", "Cafe One", "130", "NW 24th Street")]
    public async Task NeighbouringRecordsWithoutSharedVenueIdentityNeedClarification(
        string firstName, string firstNumber, string firstStreet,
        string secondName, string secondNumber, string secondStreet)
    {
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new object[]
            {
                RankedResult("first", $"{firstName}, {firstNumber} {firstStreet}, Miami, FL 33127, United States of America",
                    25.79940, -80.19600, new { confidence = 0.9, match_type = "full_match" },
                    name: firstName, housenumber: firstNumber, street: firstStreet),
                RankedResult("second", $"{secondName}, {secondNumber} {secondStreet}, Miami, FL 33127, United States of America",
                    25.79970, -80.19590, new { confidence = 0.8, match_type = "full_match" },
                    name: secondName, housenumber: secondNumber, street: secondStreet),
            },
        }));

        var result = await CreateResolver(http).ResolveAsync("coffee in Wynwood");

        Assert.Equal(PlaceResolutionStatus.NeedsClarification, result.Status);
        Assert.Null(result.Place);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task BuildingLevelScoreDoesNotAuthorizeADifferentVenueAtTheRequestedAddress()
    {
        // Geoapify: confidence_building_level only says the building position is correct, and
        // match_type only reports the address level matched; neither confirms the venue name.
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new object[]
            {
                RankedResult("wayan", "Wayan Miami, 50 Northwest 24th Street, Miami, FL 33127, United States of America",
                    25.799596, -80.195854, new { confidence = 0, confidence_building_level = 1, match_type = "full_match" },
                    name: "Wayan Miami", housenumber: "50", street: "Northwest 24th Street"),
            },
        }));

        var result = await CreateResolver(http).ResolveAsync("Joe's Pizza, 50 Northwest 24th Street");

        Assert.Equal(PlaceResolutionStatus.NeedsClarification, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task BuildingLevelScoreDoesNotAuthorizeTheNamedVenueAtADifferentAddress()
    {
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new object[]
            {
                RankedResult("walgreens-9020", "Walgreens, 9020 Biscayne Boulevard, Miami Shores, FL 33138, United States of America",
                    25.858079, -80.1846636, new { confidence = 0, confidence_building_level = 1, match_type = "full_match" },
                    name: "Walgreens", housenumber: "9020", street: "Biscayne Boulevard"),
            },
        }));

        var result = await CreateResolver(http).ResolveAsync("Walgreens, 3103 Biscayne Boulevard");

        Assert.Equal(PlaceResolutionStatus.NeedsClarification, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task BuildingLevelScoreAcceptsAbbreviatedStreetInTheRequest()
    {
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new object[]
            {
                RankedResult("wayan", "Wayan Miami, 50 Northwest 24th Street, Miami, FL 33127, United States of America",
                    25.799596, -80.195854, new { confidence = 0, confidence_building_level = 1, match_type = "full_match" },
                    name: "Wayan Miami", housenumber: "50", street: "Northwest 24th Street"),
            },
        }));

        var result = await CreateResolver(http).ResolveAsync("Wayan Miami, 50 NW 24th St.");

        Assert.Equal(PlaceResolutionStatus.Resolved, result.Status);
        Assert.Equal("wayan", result.Place!.PlaceId);
    }

    [Fact]
    public async Task BuildingLevelScoreIsIgnoredWithoutFullMatch()
    {
        using var http = CreateClient(_ => JsonResponse(new
        {
            results = new object[]
            {
                RankedResult("partial", "3103 Biscayne Boulevard, Miami, FL 33137, United States of America",
                    25.8063, -80.1897, new { confidence = 0.2, confidence_building_level = 1, match_type = "inner_part" }),
            },
        }));

        var result = await CreateResolver(http).ResolveAsync("Walgreens, 3103 Biscayne Boulevard");

        Assert.Equal(PlaceResolutionStatus.NeedsClarification, result.Status);
        Assert.Empty(result.Candidates);
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

    [Theory]
    [InlineData("Key West")]
    [InlineData("Key West, FL")]
    [InlineData("Key West, Florida")]
    public async Task KeyWestWeekendWeatherResolvesOutsideTheLocalRouteArea(string place)
    {
        var geoapifyCalls = 0;
        var openMeteoCalls = 0;
        using var http = CreateClient(request =>
        {
            if (request.RequestUri!.Host == "api.geoapify.com")
            {
                geoapifyCalls++;
                return JsonResponse(new
                {
                    results = new[]
                    {
                        GeoResult(
                            "Key West, Monroe County, Florida, United States",
                            24.5551,
                            -81.78,
                            "Florida",
                            "Monroe County",
                            "US",
                            .95),
                    },
                });
            }

            openMeteoCalls++;
            return WeatherResponse(["2026-10-22", "2026-10-23", "2026-10-24", "2026-10-25", "2026-10-26"]);
        });

        // The shared place resolver intentionally limits local routes to the hotel area.
        var localRouteResolution = await CreateResolver(http).ResolveAsync(place);
        Assert.Equal(PlaceResolutionStatus.NeedsClarification, localRouteResolution.Status);
        Assert.Empty(localRouteResolution.Candidates);

        var anchor = new DateTimeOffset(2026, 10, 23, 16, 0, 0, TimeSpan.Zero);
        var service = CreateWeatherService(http, new FakeTimeProvider(anchor));
        var saturday = await service.GetWeatherAsync(new WeatherRequest(place, WeekendDay: 0, AsOfUtc: anchor));
        var sunday = await service.GetWeatherAsync(new WeatherRequest(place, WeekendDay: 1, AsOfUtc: anchor));

        Assert.Equal(WeatherResultStatus.Available, saturday.Status);
        Assert.Equal(WeatherResultStatus.Available, sunday.Status);
        Assert.Equal("Key West, Monroe County, Florida, United States", saturday.Location!.Name);
        Assert.Equal(24.5551, saturday.Location.Latitude);
        Assert.Equal(-81.78, saturday.Location.Longitude);
        Assert.Equal(new DateOnly(2026, 10, 24), saturday.Forecast!.LocalDate);
        Assert.Equal(new DateOnly(2026, 10, 25), sunday.Forecast!.LocalDate);
        Assert.Equal(3, geoapifyCalls);
        Assert.Equal(2, openMeteoCalls);
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

    [Theory]
    [InlineData(null)]
    [InlineData("Hotel ZGY")]
    [InlineData("the hotel")]
    public async Task WeatherForHotelUsesConfiguredAddressCoordinatesWithoutCallingGeoapify(string? place)
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

        var result = await service.GetWeatherAsync(new WeatherRequest(place));

        Assert.Equal(WeatherResultStatus.Available, result.Status);
        Assert.True(result.Location!.IsHotel);
        Assert.Equal("Hotel ZGY, 2217 NW Miami Court, Miami, FL 33127", result.Location.Name);
        Assert.Equal(Coordinates.Latitude, result.Location.Latitude);
        Assert.Equal(Coordinates.Longitude, result.Location.Longitude);
        Assert.Equal(0, geoapifyCalls);
        Assert.Contains($"latitude={Coordinates.Latitude}", weatherUri!.Query);
        Assert.Contains($"longitude={Coordinates.Longitude}", weatherUri.Query);
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
    public async Task WeekdayStartsResolveAgainstTheHotelCalendarAndReportDaysPastTheHorizon()
    {
        var calls = 0;
        using var http = CreateClient(_ =>
        {
            calls++;
            return WeatherResponse(["2026-10-31", "2026-11-01", "2026-11-02", "2026-11-03", "2026-11-04", "2026-11-05", "2026-11-06"]);
        });
        // At 03:30Z the hotel is still on Saturday, October 31 in Miami.
        var service = CreateWeatherService(
            http,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 3, 30, 0, TimeSpan.Zero)));

        var saturday = await service.GetWeatherAsync(new WeatherRequest(DaysAhead: 0, StartWeekday: DayOfWeek.Saturday));
        var sunday = await service.GetWeatherAsync(new WeatherRequest(DaysAhead: 1, StartWeekday: DayOfWeek.Saturday));
        var friday = await service.GetWeatherAsync(new WeatherRequest(DaysAhead: 0, StartWeekday: DayOfWeek.Friday));
        Assert.Equal(3, calls);

        var nextSaturday = await service.GetWeatherAsync(new WeatherRequest(DaysAhead: 1, StartWeekday: DayOfWeek.Friday));

        Assert.Equal(new DateOnly(2026, 10, 31), saturday.Forecast!.LocalDate);
        Assert.Equal(new DateOnly(2026, 11, 1), sunday.Forecast!.LocalDate);
        Assert.Equal(new DateOnly(2026, 11, 6), friday.Forecast!.LocalDate);
        Assert.Equal(WeatherResultStatus.BeyondForecast, nextSaturday.Status);
        Assert.Equal(new DateOnly(2026, 11, 7), nextSaturday.RequestedDate);
        Assert.Null(nextSaturday.Forecast);
        Assert.Equal(3, calls);
    }

    // Monday 2026-10-26 through Sunday 2026-11-01 in Miami. Expected weekend: Saturday, Sunday.
    [Theory]
    [InlineData("2026-10-26", "2026-10-31", "2026-11-01")]
    [InlineData("2026-10-27", "2026-10-31", "2026-11-01")]
    [InlineData("2026-10-28", "2026-10-31", "2026-11-01")]
    [InlineData("2026-10-29", "2026-10-31", "2026-11-01")]
    [InlineData("2026-10-30", "2026-10-31", "2026-11-01")]
    [InlineData("2026-10-31", "2026-10-31", "2026-11-01")]
    // On Sunday, Saturday has passed and Sunday is the remaining weekend day.
    [InlineData("2026-11-01", null, "2026-11-01")]
    public async Task WeekendAndBareWeekdaysResolveAgainstTheTurnsHotelDateOnEveryWeekday(
        string hotelDate,
        string? expectedSaturday,
        string expectedSunday)
    {
        var today = DateOnly.Parse(hotelDate, System.Globalization.CultureInfo.InvariantCulture);
        using var http = CreateClient(_ => WeatherResponse(
            Enumerable.Range(-1, 8).Select(offset => today.AddDays(offset).ToString("yyyy-MM-dd")).ToArray()));
        // 16:00Z is midday in Miami. The clock is a day later to prove the turn anchor wins.
        var anchor = new DateTimeOffset(today.ToDateTime(new TimeOnly(16, 0)), TimeSpan.Zero);
        var service = CreateWeatherService(http, new FakeTimeProvider(anchor.AddDays(1)));

        var saturday = await service.GetWeatherAsync(new WeatherRequest(WeekendDay: 0, AsOfUtc: anchor));
        var sunday = await service.GetWeatherAsync(new WeatherRequest(WeekendDay: 1, AsOfUtc: anchor));

        if (expectedSaturday is null)
        {
            Assert.Equal(WeatherResultStatus.Elapsed, saturday.Status);
            Assert.Equal(today.AddDays(-1), saturday.RequestedDate);
            Assert.Null(saturday.Forecast);
        }
        else
        {
            Assert.Equal(DateOnly.Parse(expectedSaturday, System.Globalization.CultureInfo.InvariantCulture), saturday.Forecast!.LocalDate);
        }

        Assert.Equal(DateOnly.Parse(expectedSunday, System.Globalization.CultureInfo.InvariantCulture), sunday.Forecast!.LocalDate);
        foreach (var weekday in Enum.GetValues<DayOfWeek>())
        {
            var bare = await service.GetWeatherAsync(new WeatherRequest(StartWeekday: weekday, AsOfUtc: anchor));
            var date = bare.Forecast!.LocalDate;
            var daysUntilWeekday = ((int)weekday - (int)today.DayOfWeek + 7) % 7;
            var expectedDate = today.AddDays(daysUntilWeekday);

            Assert.Equal(weekday, date.DayOfWeek);
            Assert.Equal(expectedDate, date);
        }

        Assert.Equal(
            today,
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(anchor, TimeZoneInfo.FindSystemTimeZoneById(Hotel.TimeZoneId)).DateTime));
    }

    [Fact]
    public async Task TurnAnchorKeepsTheGuestsDateWhenTheActivityRunsAfterHotelMidnight()
    {
        using var http = CreateClient(_ => WeatherResponse(
            ["2026-10-23", "2026-10-24", "2026-10-25", "2026-10-26", "2026-10-27", "2026-10-28", "2026-10-29", "2026-10-30"]));
        // The turn began at 23:59 Friday in Miami; the activity runs at 00:30 Saturday.
        var anchor = new DateTimeOffset(2026, 10, 24, 3, 59, 0, TimeSpan.Zero);
        var service = CreateWeatherService(http, new FakeTimeProvider(anchor.AddMinutes(31)));

        var today = await service.GetWeatherAsync(new WeatherRequest(AsOfUtc: anchor));
        var saturday = await service.GetWeatherAsync(new WeatherRequest(WeekendDay: 0, AsOfUtc: anchor));

        Assert.Equal(new DateOnly(2026, 10, 23), today.Forecast!.LocalDate);
        Assert.Equal(new DateOnly(2026, 10, 24), saturday.Forecast!.LocalDate);
        Assert.Contains("past_days=1", http.LastRequestUri!.Query, StringComparison.Ordinal);
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
    public void LocationOptionsRequireAddressTimeZoneAndGeoapifySecret()
    {
        var validator = new LocationOptionsValidator();

        Assert.True(validator.Validate(null, Hotel).Succeeded);
        Assert.True(validator.Validate(null, Geoapify).Succeeded);
        Assert.Contains("Address", validator.Validate(
            null, new HotelLocationOptions { TimeZoneId = Hotel.TimeZoneId })
            .FailureMessage);
        Assert.Contains("TimeZoneId", validator.Validate(
            null, new HotelLocationOptions { Address = Hotel.Address, TimeZoneId = "not-a-zone" })
            .FailureMessage);
        Assert.Contains("ApiKey", validator.Validate(null, new GeoapifyOptions()).FailureMessage);
    }

    private static GeoapifyPlaceResolver CreateResolver(HttpClient http) =>
        new(new FixedHttpClientFactory(http), CreateHotelLocation(), Options.Create(Geoapify));

    private static HotelLocation CreateHotelLocation()
    {
        var hotelLocation = new HotelLocation();
        hotelLocation.Initialize(Coordinates);
        return hotelLocation;
    }

    private static OpenMeteoWeatherService CreateWeatherService(HttpClient http, TimeProvider timeProvider)
    {
        var resolver = CreateResolver(http);
        return new OpenMeteoWeatherService(
            new FixedHttpClientFactory(http),
            resolver,
            Options.Create(Hotel),
            CreateHotelLocation(),
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

    private static object RankedResult(
        string placeId,
        string formatted,
        double latitude,
        double longitude,
        object rank,
        string? name = null,
        string? housenumber = null,
        string? street = null) =>
        new
        {
            place_id = placeId,
            name,
            housenumber,
            street,
            formatted,
            lat = latitude,
            lon = longitude,
            state = "Florida",
            county = "Miami-Dade County",
            country_code = "us",
            rank,
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
