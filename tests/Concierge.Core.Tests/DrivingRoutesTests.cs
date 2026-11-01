using System.Net;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Location;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Concierge.Core.Tests;

public sealed class DrivingRoutesTests
{
    [Theory]
    [InlineData(null, 0, "Panther Coffee", "2390 Northwest 2nd Avenue")]
    [InlineData("Hotel ZGY", 0, "Panther Coffee", "2390 Northwest 2nd Avenue")]
    [InlineData("Another pickup", 1, "Panther Coffee", "2390 Northwest 2nd Avenue")]
    [InlineData(null, 0, "Healthi Rx Pharmacy", "2318 Northeast 2nd Avenue")]
    public async Task VerifiedDestinationUsesFreshRouteAndActiveRideCheckWithoutDestinationGeocode(
        string? pickup, int expectedGeocodes, string name, string address)
    {
        var paths = new List<string>();
        string? routeQuery = null;
        using var location = CreateClient(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal))
            {
                Assert.Contains("Another%20pickup", request.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
                return Geocode("Another pickup, Miami", 25.799, -80.19);
            }
            routeQuery = Uri.UnescapeDataString(request.RequestUri.Query);
            return RouteResponse();
        });
        var rideReads = 0;
        using var rides = RideClient(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            rideReads++;
            return RidesResponse();
        });
        var tool = RideTool(location.Client, rides, new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero));
        var arguments = RideScope();
        var verified = new ConciergeCanonicalPlace(name, address, "verified-place", 25.801, -80.199);
        arguments[ConciergeToolDefinitions.TrustedDestinationKey] = JsonSerializer.Serialize(verified);

        var json = await tool.ProposeRideAsync(pickup, verified.Name, true, null, arguments, default);
        var offer = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(json, JsonOptions)!;

        Assert.Equal("Available", offer.Status);
        Assert.Equal(verified.Latitude, offer.Proposal!.Dropoff.Latitude);
        Assert.Equal(verified.Longitude, offer.Proposal.Dropoff.Longitude);
        Assert.Contains(verified.Address!, offer.Proposal.Dropoff.Name);
        Assert.Equal(expectedGeocodes, paths.Count(path => path.EndsWith("/geocode/search", StringComparison.Ordinal)));
        Assert.Single(paths, path => path.EndsWith("/routing", StringComparison.Ordinal));
        Assert.Contains("|25.801,-80.199", routeQuery);
        Assert.Equal(1, rideReads);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(12, 12)]
    public async Task RideProposalMovesTooSoonPickupToEarliestBookableMinuteAndRejectsPastPickups(
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
        using var rides = RideClient(_ => RidesResponse());
        var tool = new ConciergeActionToolService(
            null!, CreateService(http.Client), null!, null!, new FixedHttpClientFactory(rides),
            new FakeTimeProvider(now), Options.Create(Hotel), Options.Create(options));
        // Minimum notice plus the two-minute confirmation grace; now is already a whole minute.
        var earliest = now.AddMinutes(expectedMinutes + 2);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var accepted = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(
            await tool.ProposeRideAsync(null, "South Beach", false, earliest.ToString("O"), RideScope(), default),
            jsonOptions)!;
        Assert.Equal("Available", accepted.Status);
        Assert.Equal(earliest, accepted.Proposal!.PickupAtUtc);
        Assert.False(accepted.PickupAdjusted);
        Assert.False(accepted.Proposal.Immediate);

        var moved = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(
            await tool.ProposeRideAsync(
                null, "South Beach", false, now.AddMinutes(expectedMinutes).ToString("O"), RideScope(), default),
            jsonOptions)!;
        Assert.Equal("Available", moved.Status);
        Assert.True(moved.PickupAdjusted);
        Assert.Equal(earliest, moved.Proposal!.PickupAtUtc);
        var callsAfterProposals = providerCalls;
        Assert.True(callsAfterProposals > 0);

        var rejected = await tool.ProposeRideAsync(
            null, "South Beach", false, now.AddMinutes(-1).AddTicks(-1).ToString("O"), RideScope(), default);
        var result = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(rejected, jsonOptions)!;
        Assert.Equal("InvalidSchedule", result.Status);
        Assert.Equal(expectedMinutes, result.MinimumAdvanceNoticeMinutes);
        Assert.Contains($"at least {expectedMinutes} minutes", result.Message, StringComparison.Ordinal);
        Assert.Equal(earliest, result.EarliestPickupAt);
        Assert.Null(result.Proposal);
        Assert.Equal(callsAfterProposals, providerCalls);
        var rendered = ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.ProposeRide, rejected)
            .Interpret(new ConciergeTurnSubmission("schedule", "Book a ride.", "voice"),
                new Dictionary<string, object?>(), false);
        Assert.Equal(
            $"I can't schedule a pickup for that time. The earliest scheduled pickup is {earliest.ToOffset(TimeSpan.FromHours(-4)):h:mm tt} hotel time. No ride has been booked.",
            rendered.GuestFacingText);
        Assert.Equal(rendered.GuestFacingText, rendered.CompactModelContext);

        var immediate = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(
            await tool.ProposeRideAsync(null, "South Beach", true, null, RideScope(), default), jsonOptions)!;
        Assert.Equal("Available", immediate.Status);
        Assert.True(immediate.Proposal!.Immediate);
        Assert.Null(immediate.Proposal.PickupAtUtc);
    }

    [Fact]
    public async Task LeaveInFiveMinutesWithTruncatedPromptClockProposesEarliestBookableMinute()
    {
        // Live regression: at 01:00:27 local the model (seeing "01:00") asked for 01:05.
        var now = new DateTimeOffset(2026, 10, 20, 1, 0, 27, TimeSpan.FromHours(-4));
        var tool = RideTool(now, out var http, out var rides);
        using var _ = http;
        using var __ = rides;

        var serialized = await tool.ProposeRideAsync(
            null, "Healthy Rx Pharmacy", false, "2026-10-20T01:05:00-04:00", RideScope(), default);
        var result = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(serialized, JsonOptions)!;

        var expected = new DateTimeOffset(2026, 10, 20, 1, 8, 0, TimeSpan.FromHours(-4));
        Assert.Equal("Available", result.Status);
        Assert.True(result.PickupAdjusted);
        Assert.Equal(expected, result.Proposal!.PickupAtUtc);
        Assert.Equal(TimeSpan.Zero, result.Proposal.PickupAtUtc!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(-4), result.EarliestPickupAt!.Value.Offset);
        var rendered = ConciergeToolResultInterpreter.WithProposalSequence(
                ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.ProposeRide, serialized), "leave-soon", 3)
            .Interpret(new ConciergeTurnSubmission("leave-soon", "I need to leave in five minutes.", "voice"),
                new Dictionary<string, object?> { ["pickupAt"] = "2026-10-20T01:05:00-04:00" }, false);
        Assert.StartsWith(
            "The earliest available pickup is 1:08 AM hotel time.",
            rendered.GuestFacingText,
            StringComparison.Ordinal);
        Assert.Contains("No ride has been booked.", rendered.GuestFacingText, StringComparison.Ordinal);
        Assert.EndsWith("Shall I book it on Tuesday, October 20 at 1:08 AM hotel time?", rendered.GuestFacingText, StringComparison.Ordinal);
        Assert.Equal(expected, rendered.ProposedStateChange!.PickupAtUtc);
    }

    [Fact]
    public async Task ComfortablyLaterPickupIsProposedUnchanged()
    {
        var now = new DateTimeOffset(2026, 10, 20, 1, 0, 27, TimeSpan.FromHours(-4));
        var tool = RideTool(now, out var http, out var rides);
        using var _ = http;
        using var __ = rides;

        var serialized = await tool.ProposeRideAsync(
            null, "South Beach", false, "2026-10-20T01:30:00-04:00", RideScope(), default);
        var result = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(serialized, JsonOptions)!;

        Assert.Equal("Available", result.Status);
        Assert.False(result.PickupAdjusted);
        Assert.Null(result.EarliestPickupAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 5, 30, 0, TimeSpan.Zero), result.Proposal!.PickupAtUtc);
        var text = ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.ProposeRide, serialized)
            .Interpret(new ConciergeTurnSubmission("later", "Pick me up at 1:30.", "voice"),
                new Dictionary<string, object?> { ["pickupAt"] = "2026-10-20T01:30:00-04:00" }, false)
            .GuestFacingText;
        Assert.EndsWith(
            "Shall I book it on Tuesday, October 20 at 1:30 AM hotel time?",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("earliest", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("2026-10-20T00:58:00-04:00")]
    [InlineData("tomorrow at noon")]
    public async Task PastOrUnparseablePickupIsRejectedWithEarliestHotelTime(string pickupAt)
    {
        var now = new DateTimeOffset(2026, 10, 20, 1, 0, 27, TimeSpan.FromHours(-4));
        var providerCalls = new int[1];
        var tool = RideTool(now, out var http, out var rides, providerCalls);
        using var _ = http;
        using var __ = rides;

        var serialized = await tool.ProposeRideAsync(null, "South Beach", false, pickupAt, RideScope(), default);
        var result = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(serialized, JsonOptions)!;

        Assert.Equal("InvalidSchedule", result.Status);
        Assert.Null(result.Proposal);
        Assert.Null(result.CheckoutCutoffDate);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 1, 8, 0, TimeSpan.FromHours(-4)), result.EarliestPickupAt);
        Assert.Equal(0, providerCalls[0]);
        Assert.Equal(
            "I can't schedule a pickup for that time. The earliest scheduled pickup is 1:08 AM hotel time. No ride has been booked.",
            ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.ProposeRide, serialized)
                .Interpret(new ConciergeTurnSubmission("past", "Book a ride.", "voice"),
                    new Dictionary<string, object?> { ["pickupAt"] = pickupAt }, false)
                .GuestFacingText);
    }

    [Theory]
    [InlineData("2026-10-25T23:57:00-04:00", null)]
    [InlineData("2026-10-26T00:30:00-04:00", "2026-10-25T21:07:00-04:00")]
    public async Task PickupAtOrPastCheckoutCutoffIsRejectedNotShifted(string pickupAt, string? earliestAt)
    {
        // Checkout day is 2026-10-25; the cutoff is hotel-local midnight that ends it.
        var now = earliestAt is null
            ? new DateTimeOffset(2026, 10, 25, 23, 55, 0, TimeSpan.FromHours(-4))
            : new DateTimeOffset(2026, 10, 25, 21, 0, 0, TimeSpan.FromHours(-4));
        var providerCalls = new int[1];
        var tool = RideTool(now, out var http, out var rides, providerCalls);
        using var _ = http;
        using var __ = rides;

        var serialized = await tool.ProposeRideAsync(null, "South Beach", false, pickupAt, RideScope(), default);
        var result = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(serialized, JsonOptions)!;

        Assert.Equal("InvalidSchedule", result.Status);
        Assert.Null(result.Proposal);
        Assert.False(result.PickupAdjusted);
        Assert.Equal(new DateOnly(2026, 10, 25), result.CheckoutCutoffDate);
        Assert.Equal(earliestAt is null ? null : DateTimeOffset.Parse(earliestAt), result.EarliestPickupAt);
        Assert.Equal(0, providerCalls[0]);
        var text = ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.ProposeRide, serialized)
            .Interpret(new ConciergeTurnSubmission("late", "Book a ride.", "voice"),
                new Dictionary<string, object?> { ["pickupAt"] = pickupAt }, false)
            .GuestFacingText;
        Assert.Equal(
            earliestAt is null
                ? "Scheduled pickups must be before the end of your checkout day, Sunday, October 25, and there is no longer time to schedule one. No ride has been booked."
                : "Scheduled pickups must be before the end of your checkout day, Sunday, October 25. Please choose an earlier time. No ride has been booked.",
            text);
    }

    private static ConciergeActionToolService RideTool(
        DateTimeOffset now, out RecordingHttpClient http, out HttpClient rides, int[]? providerCalls = null)
    {
        http = CreateClient(request =>
        {
            if (providerCalls is not null)
            {
                providerCalls[0]++;
            }

            return request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal)
                ? Geocode("South Beach, Miami-Dade County, Florida", 25.7745, -80.1329)
                : RouteResponse();
        });
        rides = RideClient(_ => RidesResponse());
        return RideTool(http.Client, rides, now);
    }

    [Theory]
    [InlineData("Wayan Miami, 50 NW 24th St", 25.7745, -80.1329, "Booked")]
    [InlineData("Some Other Label", 25.7748, -80.1331, "DriverAssigned")]
    [InlineData("South Beach", 25.7800, -80.1350, "PickedUp")]
    public async Task RideProposalWarnsButAllowsDestinationWithActiveRide(
        string dropoffName, double latitude, double longitude, string status)
    {
        var now = new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);
        using var http = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal)
                ? Geocode("South Beach, Miami-Dade County, Florida", 25.7745, -80.1329)
                : RouteResponse());
        HttpRequestMessage? rideRequest = null;
        using var rides = RideClient(request =>
        {
            rideRequest = request;
            return RidesResponse(ActiveRide(dropoffName, latitude, longitude, now.AddMinutes(10), status));
        });
        var tool = RideTool(http.Client, rides, now);

        var serialized = await tool.ProposeRideAsync(null, "South Beach", true, null, RideScope(), default);
        var result = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(serialized, JsonOptions)!;

        Assert.Equal("Available", result.Status);
        Assert.NotNull(result.Proposal);
        using var json = JsonDocument.Parse(serialized);
        Assert.Equal(status, Assert.Single(json.RootElement.GetProperty("existingRides").EnumerateArray())
            .GetProperty("status").GetString());
        Assert.Equal($"/guests/{GuestId:D}/rides", rideRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("?when=upcoming", rideRequest.RequestUri.Query);
        Assert.Equal(GuestId.ToString("D"), rideRequest.Headers.GetValues("X-Verified-Guest-Id").Single());
        var rendered = ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.ProposeRide, serialized)
            .Interpret(new ConciergeTurnSubmission("again", "Book it now, please.", "voice"),
                new Dictionary<string, object?>(), false);
        Assert.Null(rendered.ProposedStateChange);
        Assert.Contains("You already have an active ride", rendered.GuestFacingText, StringComparison.Ordinal);
        Assert.Contains($"status: {status}", rendered.GuestFacingText, StringComparison.Ordinal);
        Assert.Contains("ADDITIONAL ride, not a change", rendered.GuestFacingText, StringComparison.Ordinal);
        Assert.Contains("confirmation on a later turn", rendered.GuestFacingText, StringComparison.Ordinal);
        Assert.Contains("No ride has been booked.", rendered.GuestFacingText, StringComparison.Ordinal);
        Assert.EndsWith("Shall I book it now?", rendered.GuestFacingText, StringComparison.Ordinal);
        Assert.Equal(rendered.GuestFacingText, rendered.CompactModelContext);
    }

    [Fact]
    public async Task RideProposalAllowsDifferentDestinationAndFailsClosedWithoutRideCheck()
    {
        var now = new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);
        using var http = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal)
                ? Geocode("South Beach, Miami-Dade County, Florida", 25.7745, -80.1329)
                : RouteResponse());
        var status = HttpStatusCode.OK;
        using var rides = RideClient(_ => status == HttpStatusCode.OK
            ? RidesResponse(
                ActiveRide("Wayan Miami, 50 NW 24th St", 25.8003, -80.1957, now),
                ActiveRide("South Beach", 25.8500, -80.1200, now),
                ActiveRide("South Beach", 25.7745, -80.1329, now, "Completed"),
                ActiveRide("South Beach", 25.7745, -80.1329, now, "Cancelled"))
            : new HttpResponseMessage(status));
        var tool = RideTool(http.Client, rides, now);

        var available = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(
            await tool.ProposeRideAsync(null, "South Beach", true, null, RideScope(), default), JsonOptions)!;
        Assert.Equal("Available", available.Status);
        Assert.NotNull(available.Proposal);
        var noWarning = ConciergeToolResultInterpreter.Decode(
                ConciergeToolDefinitions.ProposeRide, JsonSerializer.Serialize(available, JsonOptions))
            .Interpret(new ConciergeTurnSubmission("different", "A new ride.", "voice"),
                new Dictionary<string, object?>(), false);
        Assert.DoesNotContain("ADDITIONAL", noWarning.GuestFacingText, StringComparison.Ordinal);

        status = HttpStatusCode.ServiceUnavailable;
        var serialized = await tool.ProposeRideAsync(null, "South Beach", true, null, RideScope(), default);
        var unavailable = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(serialized, JsonOptions)!;
        Assert.Equal("ActiveRidesUnavailable", unavailable.Status);
        Assert.Null(unavailable.Proposal);
        var rendered = ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.ProposeRide, serialized)
            .Interpret(new ConciergeTurnSubmission("again", "Book a ride.", "voice"),
                new Dictionary<string, object?>(), false);
        Assert.Null(rendered.ProposedStateChange);
        Assert.Equal(
            "I couldn't check your existing rides, so I haven't offered a new one. Please try again shortly. No ride has been booked.",
            rendered.GuestFacingText);

        await Assert.ThrowsAsync<ArgumentException>(() => tool.ProposeRideAsync(
            null, "South Beach", true, null, new AIFunctionArguments(), default));
    }

    [Fact]
    public async Task ExactOverlappingProposalRemainsAvailableWithBoundedExistingRideContext()
    {
        var now = new DateTimeOffset(2026, 10, 19, 18, 0, 0, TimeSpan.Zero);
        var pickupAt = now.AddHours(1);
        using var http = CreateClient(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/geocode/search", StringComparison.Ordinal)
                ? Geocode("South Beach, Miami-Dade County, Florida", 25.7745, -80.1329)
                : RouteResponse());
        using var rides = RideClient(_ => RidesResponse(Enumerable.Range(0, 8)
            .Select(_ => ActiveRide("South Beach", 25.7745, -80.1329, pickupAt)).ToArray()));
        var tool = RideTool(http.Client, rides, now);

        var serialized = await tool.ProposeRideAsync(
            "Hotel ZGY", "South Beach", false, pickupAt.ToString("O"), RideScope(), default);
        var result = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(serialized, JsonOptions)!;

        Assert.Equal("Available", result.Status);
        Assert.Equal(pickupAt, result.Proposal!.PickupAtUtc);
        Assert.False(result.PickupAdjusted);
        Assert.Equal(3, result.ExistingRides!.Count);
        Assert.All(result.ExistingRides, ride => Assert.Equal(pickupAt, ride.PickupAt));
        var interpreted = ConciergeToolResultInterpreter.WithProposalSequence(
                ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.ProposeRide, serialized), "overlap", 4)
            .Interpret(new ConciergeTurnSubmission("overlap", "Another ride at the same time.", "voice"),
                new Dictionary<string, object?> { ["pickupAt"] = pickupAt.ToString("O") }, false);
        Assert.NotNull(interpreted.ProposedStateChange);
        Assert.Equal(interpreted.CompactModelContext, interpreted.GuestFacingText);
        Assert.True(Encoding.UTF8.GetByteCount(interpreted.CompactModelContext) <= 2_048);
        Assert.Contains("ADDITIONAL ride, not a change", interpreted.GuestFacingText, StringComparison.Ordinal);
        Assert.Contains("confirmation on a later turn", interpreted.GuestFacingText, StringComparison.Ordinal);
        Assert.Contains("No ride has been booked.", interpreted.GuestFacingText, StringComparison.Ordinal);
        Assert.EndsWith("Shall I book it on Monday, October 19 at 7:00 PM hotel time?", interpreted.GuestFacingText, StringComparison.Ordinal);
    }

    private static readonly Guid GuestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StayId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static AIFunctionArguments RideScope() => new(new Dictionary<string, object?>
    {
        [ConciergeToolDefinitions.TrustedGuestIdKey] = GuestId.ToString("D"),
        [ConciergeToolDefinitions.TrustedStayIdKey] = StayId.ToString("D"),
        [ConciergeToolDefinitions.TrustedCheckoutDateKey] = "2026-10-25",
    });

    private static ConciergeActionToolService RideTool(HttpClient locationClient, HttpClient rideClient, DateTimeOffset now) =>
        new(null!, CreateService(locationClient), null!, null!, new FixedHttpClientFactory(rideClient),
            new FakeTimeProvider(now), Options.Create(Hotel), Options.Create(new RideSchedulingOptions()));

    private static HttpClient RideClient(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(new DelegateHttpMessageHandler(responder)) { BaseAddress = new Uri("http://ride-service.test") };

    private static HttpResponseMessage RidesResponse(params object[] rides) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(rides, JsonOptions), Encoding.UTF8, "application/json"),
        };

    private static object ActiveRide(
        string dropoffName, double latitude, double longitude, DateTimeOffset pickupAt, string status = "Booked") =>
        new
        {
            id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            guestId = GuestId,
            stayId = StayId,
            pickupName = "Hotel ZGY",
            pickupLatitude = 25.7984634,
            pickupLongitude = -80.1956016,
            dropoffName,
            dropoffLatitude = latitude,
            dropoffLongitude = longitude,
            immediate = false,
            pickupAt,
            bookedAt = pickupAt.AddMinutes(-10),
            status,
            routeDurationSeconds = 720,
        };

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
    public async Task AmbiguousProposalSerializesCanonicalDestinationBranchesButNeverPickupAsDestination()
    {
        using var http = CreateClient(request =>
        {
            Assert.EndsWith("/geocode/search", request.RequestUri!.AbsolutePath);
            return JsonResponse(new
            {
                results = new[]
                {
                    GeoResult("Panther Coffee, 2390 Northwest 2nd Avenue, Miami", 25.801, -80.199, "Panther Coffee"),
                    GeoResult("Panther Coffee, 1875 Purdy Avenue, Miami Beach", 25.795, -80.144, "Panther Coffee"),
                },
            });
        });
        using var rides = RideClient(_ => throw new InvalidOperationException("An ambiguous route cannot check or book rides."));
        var tool = RideTool(http.Client, rides, new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero));
        var destination = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(
            await tool.ProposeRideAsync(null, "Panther Coffee", true, null, RideScope(), default), JsonOptions)!;
        Assert.Equal("NeedsClarification", destination.Status);
        Assert.False(destination.ClarifyingPickup);
        Assert.Equal(2, destination.CanonicalCandidates!.Count);
        Assert.Equal("Panther Coffee", destination.CanonicalCandidates[0].Name);
        Assert.Equal("2390 Northwest 2nd Avenue, Miami", destination.CanonicalCandidates[0].Address);
        Assert.Equal(25.801, destination.CanonicalCandidates[0].Latitude);
        Assert.Equal(-80.144, destination.CanonicalCandidates[1].Longitude);
        Assert.All(destination.CanonicalCandidates, branch => Assert.NotNull(branch.ProviderId));

        var pickup = JsonSerializer.Deserialize<ConciergeRideProposalToolResult>(
            await tool.ProposeRideAsync("Panther Coffee", "South Beach", true, null, RideScope(), default), JsonOptions)!;
        Assert.Equal("NeedsClarification", pickup.Status);
        Assert.True(pickup.ClarifyingPickup);
        Assert.Empty(pickup.CanonicalCandidates!);
        var text = ConciergeToolResultInterpreter.Decode(ConciergeToolDefinitions.ProposeRide,
                JsonSerializer.Serialize(pickup, JsonOptions))
            .Interpret(new("pickup", "Book from Panther Coffee to South Beach.", "voice"),
                new Dictionary<string, object?>(), false).GuestFacingText;
        Assert.StartsWith("Which pickup do you mean:", text);
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

    private static object GeoResult(string formatted, double latitude, double longitude, string? name = null) =>
        new
        {
            name,
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
