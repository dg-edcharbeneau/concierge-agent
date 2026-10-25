using System.Net;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Location;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Concierge.Core.Tests;

public sealed class DrivingRoutesTests
{
    [Theory]
    [InlineData(null, 5)]
    [InlineData(12, 12)]
    public async Task RideProposalAcceptsExactMinimumAndRendersConfiguredJustUnderRejection(
        int? configuredMinutes, int expectedMinutes)
    {
        var now = new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);
        var providerCalls = 0;
        using var http = CreateClient(request =>
        {
            providerCalls++;
            return request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal)
                ? Geocode("South Beach, Miami-Dade County, Florida", 25.7745, -80.1329)
                : RouteResponse();
        });
        var options = new RideSchedulingOptions();
        if (configuredMinutes is { } minutes)
        {
            options.MinimumAdvanceNoticeMinutes = minutes;
        }
        var tool = new ConciergeActionToolService(
            null!, CreateService(http.Client), null!, null!, null!,
            new FakeTimeProvider(now), Options.Create(Hotel), Options.Create(options));
        var boundary = now.AddMinutes(expectedMinutes);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var accepted = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(
            await tool.ProposeRideAsync(null, "South Beach", false, boundary.ToString("O"), default),
            jsonOptions)!;
        Assert.Equal("Available", accepted.Status);
        Assert.Equal(boundary, accepted.Proposal!.PickupAtUtc);
        Assert.False(accepted.Proposal.Immediate);
        var callsAfterAccepted = providerCalls;
        Assert.True(callsAfterAccepted > 0);

        var rejected = await tool.ProposeRideAsync(
            null, "South Beach", false, boundary.AddTicks(-1).ToString("O"), default);
        var result = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(rejected, jsonOptions)!;
        Assert.Equal("InvalidSchedule", result.Status);
        Assert.Equal(expectedMinutes, result.MinimumAdvanceNoticeMinutes);
        Assert.Contains($"at least {expectedMinutes} minutes", result.Message, StringComparison.Ordinal);
        Assert.Null(result.Proposal);
        Assert.Equal(callsAfterAccepted, providerCalls);
        var rendered = ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.ProposeRide, rejected)
            .Interpret(new ConciergeTurnSubmission("schedule", "Book a ride.", "voice"),
                new Dictionary<string, object?>(), false);
        Assert.Equal(
            $"A scheduled pickup must be at least {expectedMinutes} minutes from now. Please choose a later time. No ride has been booked.",
            rendered.GuestFacingText);
        Assert.Equal(rendered.GuestFacingText, rendered.CompactModelContext);

        var immediate = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(
            await tool.ProposeRideAsync(null, "South Beach", true, null, default), jsonOptions)!;
        Assert.Equal("Available", immediate.Status);
        Assert.True(immediate.Proposal!.Immediate);
        Assert.Null(immediate.Proposal.PickupAtUtc);
    }

    private static readonly HotelLocationOptions Hotel = new()
    {
        TimeZoneId = "America/New_York",
        Address = "2217 NW Miami Court, Miami, FL 33127",
    };

    private static readonly GeoapifyOptions Geoapify = new() { ApiKey = "test-route-key" };

    private static readonly HotelCoordinates Coordinates = new(25.7984634, -80.1956016);

    [Fact]
    public async Task OnePlaceRoutesFromHotelToResolvedDestination()
    {
        var routeUri = (Uri?)null;
        var resolutionCalls = 0;
        using var http = CreateClient(request =>
        {
            if (request.RequestUri!.Host == "api.geoapify.com"
                && request.RequestUri.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal))
            {
                resolutionCalls++;
                return Geocode("South Beach, Miami-Dade County, Florida", 25.7745, -80.1329);
            }

            routeUri = request.RequestUri;
            return RouteResponse();
        });
        var service = CreateService(http.Client);

        var result = await service.GetRouteAsync(new DrivingRouteRequest(["South Beach"]));

        Assert.Equal(DrivingRouteStatus.Available, result.Status);
        Assert.Equal(1, resolutionCalls);
        Assert.NotNull(result.Route);
        Assert.Contains("mode=drive", routeUri!.Query);
        Assert.Contains("details=instruction_details", routeUri.Query);
        Assert.Contains("units=metric", routeUri.Query);
        Assert.Contains(Uri.EscapeDataString($"{Coordinates.Latitude},{Coordinates.Longitude}|25.7745,-80.1329"),
            routeUri.Query);
        Assert.DoesNotContain("traffic=", routeUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Hotel ZGY", "South Beach", "25.7984634,-80.1956016|25.7745,-80.1329")]
    [InlineData("South Beach", "the hotel", "25.7745,-80.1329|25.7984634,-80.1956016")]
    public async Task HotelNamedAsEitherEndpointUsesConfiguredAddressWithoutGeocoding(
        string origin,
        string destination,
        string expectedWaypoints)
    {
        var resolvedQueries = new List<string>();
        Uri? routeUri = null;
        using var http = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal))
            {
                resolvedQueries.Add(Uri.UnescapeDataString(request.RequestUri.Query));
                return Geocode("South Beach, Miami-Dade County, Florida", 25.7745, -80.1329);
            }

            routeUri = request.RequestUri;
            return RouteResponse();
        });
        var service = CreateService(http.Client);

        var result = await service.GetRouteAsync(new DrivingRouteRequest([origin, destination]));

        Assert.Equal(DrivingRouteStatus.Available, result.Status);
        var query = Assert.Single(resolvedQueries);
        Assert.Contains("South Beach", query);
        Assert.Contains(Uri.EscapeDataString(expectedWaypoints), routeUri!.Query);
        var hotelEndpoint = origin == "Hotel ZGY" ? result.Origin : result.Destination;
        Assert.Equal("Hotel ZGY, 2217 NW Miami Court, Miami, FL 33127", hotelEndpoint!.Name);
    }

    [Theory]
    [InlineData("Hotel ZGY", true)]
    [InlineData("hotel zgy, Miami", true)]
    [InlineData("The hotel", true)]
    [InlineData("  my hotel. ", true)]
    [InlineData("Hotel Dorado", false)]
    [InlineData("South Beach", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void HotelReferencesAreRecognizedWithoutMatchingOtherHotels(string? place, bool expected) =>
        Assert.Equal(expected, HotelLocationOptions.IsHotelReference(place));

    [Fact]
    public async Task TwoPlacesRouteInInputOrderAfterResolvingBoth()
    {
        var resolvedQueries = new List<string>();
        Uri? routeUri = null;
        using var http = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                resolvedQueries.Add(query);
                return query.Contains("text=First", StringComparison.Ordinal)
                    ? Geocode("First, Miami-Dade County, Florida", 25.75, -80.25)
                    : Geocode("Second, Miami-Dade County, Florida", 25.80, -80.10);
            }

            routeUri = request.RequestUri;
            return RouteResponse();
        });
        var service = CreateService(http.Client);

        var result = await service.GetRouteAsync(new DrivingRouteRequest(["First", "Second"]));

        Assert.Equal(DrivingRouteStatus.Available, result.Status);
        Assert.Equal(2, resolvedQueries.Count);
        Assert.Contains("First", resolvedQueries[0]);
        Assert.Contains("Second", resolvedQueries[1]);
        Assert.Contains(Uri.EscapeDataString("25.75,-80.25|25.8,-80.1"), routeUri!.Query);
    }

    [Fact]
    public async Task AmbiguousPlaceReturnsCandidatesWithoutCallingRoutingProvider()
    {
        var routeCalls = 0;
        using var http = CreateClient(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal))
            {
                return JsonResponse(new
                {
                    results = new[]
                    {
                        GeoResult("Miami International Airport", 25.7959, -80.2870),
                        GeoResult("Miami Executive Airport", 25.6479, -80.4328),
                    },
                });
            }

            routeCalls++;
            return RouteResponse();
        });
        var service = CreateService(http.Client);

        var result = await service.GetRouteAsync(new DrivingRouteRequest(["airport"]));

        Assert.Equal(DrivingRouteStatus.NeedsClarification, result.Status);
        Assert.Equal("airport", result.ClarificationQuery);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Null(result.Route);
        Assert.Equal(0, routeCalls);
    }

    [Fact]
    public async Task RoutingProviderErrorIsSafeAndDoesNotExposeApiKey()
    {
        using var http = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal)
                ? Geocode("South Beach, Miami-Dade County, Florida", 25.7745, -80.1329)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("provider error"),
                });
        var service = CreateService(http.Client);

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.GetRouteAsync(new DrivingRouteRequest(["South Beach"])));

        Assert.Contains("503", error.Message);
        Assert.DoesNotContain(Geoapify.ApiKey, error.Message);
        Assert.DoesNotContain("provider error", error.Message);
    }

    [Fact]
    public async Task RouteOutputKeepsServerDurationAndShapesVoiceAndWebInstructions()
    {
        using var http = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal)
                ? Geocode("South Beach, Miami-Dade County, Florida", 25.7745, -80.1329)
                : RouteResponse());
        var service = CreateService(http.Client);

        var result = await service.GetRouteAsync(new DrivingRouteRequest(["South Beach"]));

        var route = Assert.IsType<DrivingRoute>(result.Route);
        Assert.Equal(5000, route.DistanceMeters);
        Assert.Equal(720, route.EstimatedDurationSeconds);
        Assert.Equal("About 3.1 mi; around 12 minutes by car.", route.VoiceSummary);
        Assert.Equal("3.1 mi · about 12 min by car (estimated; no live traffic).", route.WebSummary);
        Assert.Equal(2, route.WebInstructions.Count);
        Assert.Equal(2, route.VoiceInstructions.Count);
        Assert.Equal("Head north on NW 2nd Avenue.", route.VoiceInstructions[0]);
        Assert.Equal("Head north on NW 2nd Avenue. (0.2 mi)", route.WebInstructions[0]);
        Assert.True(route.VoiceInstructions[1].Length <= 120);
        Assert.EndsWith("…", route.VoiceInstructions[1]);
        Assert.Contains("Turn right", route.WebInstructions[1]);
        Assert.True(route.WebInstructions[1].Length > route.VoiceInstructions[1].Length);
    }

    [Fact]
    public async Task VoiceDirectionsAreCappedToTheFirstFewStepsWhileWebKeepsAll()
    {
        const int stepCount = 12;
        var longText = string.Join(' ', Enumerable.Repeat("continue along the boulevard", 10));
        using var http = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal)
                ? Geocode("South Beach, Miami-Dade County, Florida", 25.7745, -80.1329)
                : JsonResponse(new
                {
                    results = new[]
                    {
                        new
                        {
                            distance = 9000d,
                            time = 900d,
                            legs = new[]
                            {
                                new
                                {
                                    steps = Enumerable.Range(1, stepCount)
                                        .Select(i => new
                                        {
                                            distance = 750d,
                                            instruction = new { text = $"Step {i}: {longText}" },
                                        })
                                        .ToArray(),
                                },
                            },
                        },
                    },
                }));
        var service = CreateService(http.Client);

        var route = (await service.GetRouteAsync(new DrivingRouteRequest(["South Beach"]))).Route!;

        Assert.Equal(stepCount, route.WebInstructions.Count);
        Assert.Equal(
            ["Step 1:", "Step 2:", "Step 3:"],
            route.VoiceInstructions.Select(text => text[..7]));
        Assert.True(route.VoiceInstructions.Sum(text => text.Length) <= 3 * 120);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task RouteRequiresOneOrTwoNonEmptyPlaces(int placeCount)
    {
        using var http = CreateClient(_ => throw new InvalidOperationException("No provider call expected."));
        var service = CreateService(http.Client);
        var places = Enumerable.Repeat("place", placeCount).ToArray();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.GetRouteAsync(new DrivingRouteRequest(places)));
    }

    private static GeoapifyDrivingRouteService CreateService(HttpClient httpClient)
    {
        var hotelLocation = CreateHotelLocation();
        var resolver = new GeoapifyPlaceResolver(new FixedHttpClientFactory(httpClient), hotelLocation, Options.Create(Geoapify));
        return new GeoapifyDrivingRouteService(
            new FixedHttpClientFactory(httpClient),
            resolver,
            Options.Create(Hotel),
            hotelLocation,
            Options.Create(Geoapify));
    }

    private static HotelLocation CreateHotelLocation()
    {
        var hotelLocation = new HotelLocation();
        hotelLocation.Initialize(Coordinates);
        return hotelLocation;
    }

    private static RecordingHttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new DelegateHttpMessageHandler(responder);
        return new RecordingHttpClient(new HttpClient(handler));
    }

    private static HttpResponseMessage Geocode(string formatted, double latitude, double longitude) =>
        JsonResponse(new
        {
            results = new[] { GeoResult(formatted, latitude, longitude) },
        });

    private static object GeoResult(string formatted, double latitude, double longitude) =>
        new
        {
            place_id = $"{latitude},{longitude}",
            formatted,
            lat = latitude,
            lon = longitude,
            state = "Florida",
            county = "Miami-Dade County",
            country_code = "us",
            rank = new { confidence = 0.95 },
        };

    private static HttpResponseMessage RouteResponse() =>
        JsonResponse(new
        {
            results = new[]
            {
                new
                {
                    distance = 5000d,
                    time = 720d,
                    legs = new[]
                    {
                        new
                        {
                            steps = new[]
                            {
                                new
                                {
                                    distance = 300d,
                                    instruction = new { text = "Head north on NW 2nd Avenue." },
                                },
                                new
                                {
                                    distance = 4700d,
                                    instruction = new
                                    {
                                        text = "Turn right onto a very long road name and continue for several blocks until you reach the destination entrance on the far side of the park.",
                                    },
                                },
                            },
                        },
                    },
                },
            },
        });

    private static HttpResponseMessage JsonResponse<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

    private sealed class RecordingHttpClient(HttpClient client) : IDisposable
    {
        public HttpClient Client { get; } = client;

        public void Dispose() => Client.Dispose();
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
