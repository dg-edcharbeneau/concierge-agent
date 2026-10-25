using System.Net;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Location;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Concierge.Core.Tests;

public sealed class ConciergeActionToolServiceRideClientTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid GuestId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid StayId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static readonly Guid ProposalId = Guid.Parse("12345678-1234-4234-9234-123456789abc");
    private static readonly Guid RideId = Guid.Parse("87654321-4321-4321-8321-cba987654321");
    private static readonly DateOnly CheckoutDate = new(2026, 10, 25);

    [Fact]
    public async Task ConfirmRidePostsProposalWithVerifiedScopeAndMapsRideResponse()
    {
        var pickupAt = new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, RideResponse(pickupAt)));
        using var client = CreateClient(handler);
        var service = CreateService(client);

        var result = await service.ConfirmRideAsync(
            ProposalId.ToString("D"),
            Arguments(includeProposal: true),
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal("/rides", handler.Request.PathAndQuery);
        AssertVerifiedScope(handler.Request);
        using var body = JsonDocument.Parse(handler.Request.Body!);
        var json = body.RootElement;
        Assert.Equal(GuestId, json.GetProperty("guestId").GetGuid());
        Assert.Equal(StayId, json.GetProperty("stayId").GetGuid());
        Assert.Equal(CheckoutDate.ToString("yyyy-MM-dd"), json.GetProperty("checkoutDate").GetString());
        Assert.Equal("Hotel ZGY", json.GetProperty("pickup").GetProperty("name").GetString());
        Assert.Equal(25.7984, json.GetProperty("pickup").GetProperty("latitude").GetDouble());
        Assert.Equal(-80.1956, json.GetProperty("pickup").GetProperty("longitude").GetDouble());
        Assert.Equal("South Beach", json.GetProperty("dropoff").GetProperty("name").GetString());
        Assert.Equal(25.7745, json.GetProperty("dropoff").GetProperty("latitude").GetDouble());
        Assert.Equal(-80.1329, json.GetProperty("dropoff").GetProperty("longitude").GetDouble());
        Assert.True(json.GetProperty("immediate").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("pickupAt").ValueKind);
        Assert.Equal($"concierge-{StayId:N}-{ProposalId:N}", json.GetProperty("idempotencyKey").GetString());
        Assert.Equal(720, json.GetProperty("routeDurationSeconds").GetDouble());

        var action = JsonSerializer.Deserialize<ConciergeRideActionResult>(result, JsonOptions)!;
        Assert.True(action.Simulated);
        Assert.Null(action.Message);
        Assert.Empty(action.Rides);
        var ride = Assert.IsType<RideSummary>(action.Ride);
        Assert.Equal(RideId, ride.Id);
        Assert.Equal("Booked", ride.Status);
        Assert.Equal("Hotel ZGY", ride.Pickup);
        Assert.Equal("South Beach", ride.Dropoff);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 10, 0, 0, TimeSpan.FromHours(-4)), ride.PickupAt);
        Assert.Equal("Alex", ride.DriverName);
        Assert.Equal("Blue sedan", ride.Car);
        Assert.Equal("ABC-123", ride.Plate);
    }

    [Theory]
    [InlineData("invalid_request", "The proposed pickup time is no longer eligible. No booking was confirmed.")]
    [InlineData("unavailable", "The ride service did not confirm the outcome. Check ride history before trying again.")]
    [InlineData(null, "The ride service did not confirm the outcome. Check ride history before trying again.")]
    public async Task ConfirmRideMapsServiceErrorsWithoutReturningRawErrorBody(string? errorCode, string expectedMessage)
    {
        var body = errorCode is null ? "{not-json" : JsonSerializer.Serialize(new { code = errorCode, message = "internal detail" });
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        using var client = CreateClient(handler);
        var service = CreateService(client);

        var result = await service.ConfirmRideAsync(
            ProposalId.ToString("D"),
            Arguments(includeProposal: true),
            CancellationToken.None);

        var action = JsonSerializer.Deserialize<ConciergeRideActionResult>(result, JsonOptions)!;
        Assert.True(action.Simulated);
        Assert.Equal(expectedMessage, action.Message);
        Assert.Null(action.Ride);
        Assert.Empty(action.Rides);
        Assert.DoesNotContain("internal detail", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRideStatusUsesScopedRouteAndMapsRideResponse()
    {
        var pickupAt = new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, RideResponse(pickupAt)));
        using var client = CreateClient(handler);
        var service = CreateService(client);

        var result = await service.GetRideStatusAsync(RideId.ToString("D"), Arguments(), CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Request.Method);
        Assert.Equal($"/rides/{RideId:D}", handler.Request.PathAndQuery);
        AssertVerifiedScope(handler.Request);
        var action = JsonSerializer.Deserialize<ConciergeRideActionResult>(result, JsonOptions)!;
        Assert.Null(action.Message);
        Assert.Equal(RideId, action.Ride!.Id);
        Assert.Equal("Booked", action.Ride.Status);
    }

    [Fact]
    public async Task GetRideStatusEstimatesArrivalFromPickupAndRouteDurationInHotelTime()
    {
        var pickupAt = new DateTimeOffset(2026, 10, 22, 1, 27, 8, TimeSpan.Zero);
        var pickedUpAt = pickupAt.AddSeconds(30);
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, new
        {
            id = RideId, guestId = GuestId, stayId = StayId,
            pickupName = "Hotel ZGY", pickupLatitude = 25.7984, pickupLongitude = -80.1956,
            dropoffName = "South Beach", dropoffLatitude = 25.7745, dropoffLongitude = -80.1329,
            immediate = true, pickupAt, bookedAt = pickupAt, status = "PickedUp",
            routeDurationSeconds = 562.8, pickedUpAtUtc = pickedUpAt,
        }));
        using var client = CreateClient(handler);
        var service = CreateService(client);

        var result = await service.GetRideStatusAsync(RideId.ToString("D"), Arguments(), CancellationToken.None);

        var ride = JsonSerializer.Deserialize<ConciergeRideActionResult>(result, JsonOptions)!.Ride!;
        Assert.Equal(pickedUpAt.AddSeconds(562.8), ride.ArrivalAt);
        Assert.Equal(TimeSpan.FromHours(-4), ride.ArrivalAt!.Value.Offset);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "That ride was not found for this stay.")]
    [InlineData(HttpStatusCode.Conflict, "Ride status is temporarily unavailable. Please try again later.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Ride status is temporarily unavailable. Please try again later.")]
    public async Task GetRideStatusMapsServiceErrorsWithoutLeakingErrorBody(
        HttpStatusCode statusCode,
        string expectedMessage)
    {
        var handler = new RecordingHandler(_ => ErrorResponse(statusCode));
        using var client = CreateClient(handler);
        var service = CreateService(client);

        var result = await service.GetRideStatusAsync(RideId.ToString("D"), Arguments(), CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Request.Method);
        Assert.Equal($"/rides/{RideId:D}", handler.Request.PathAndQuery);
        AssertVerifiedScope(handler.Request);
        var action = JsonSerializer.Deserialize<ConciergeRideActionResult>(result, JsonOptions)!;
        Assert.True(action.Simulated);
        Assert.Equal(expectedMessage, action.Message);
        Assert.Null(action.Ride);
        Assert.Empty(action.Rides);
        Assert.DoesNotContain("private-service-detail", result, StringComparison.Ordinal);
        Assert.DoesNotContain("private-host", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRideHistoryUsesVerifiedGuestRouteAndMapsResults()
    {
        var pickupAt = new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, new[] { RideResponse(pickupAt) }));
        using var client = CreateClient(handler);
        var service = CreateService(client);

        var result = await service.GetRideHistoryAsync("past", Arguments(), CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Request.Method);
        Assert.Equal($"/guests/{GuestId:D}/rides?when=past", handler.Request.PathAndQuery);
        AssertVerifiedScope(handler.Request);
        var action = JsonSerializer.Deserialize<ConciergeRideActionResult>(result, JsonOptions)!;
        Assert.Null(action.Message);
        Assert.Null(action.Ride);
        Assert.Equal(RideId, Assert.Single(action.Rides).Id);
    }

    [Theory]
    [InlineData("upcoming", "What rides do I have coming up?", false)]
    [InlineData("past", "What rides have I taken?", false)]
    [InlineData("upcoming", "Please cancel my ride.", true)]
    public async Task RideHistoryOutageReachesGuestAsUnavailableNotAsNoRides(
        string when,
        string userMessage,
        bool includeRideIdsForCancellation)
    {
        var handler = new RecordingHandler(_ => ErrorResponse(HttpStatusCode.ServiceUnavailable));
        using var client = CreateClient(handler);
        var service = CreateService(client);
        var serialized = await service.GetRideHistoryAsync(when, Arguments(), CancellationToken.None);
        var request = new ConciergeTurnSubmission("history-outage", userMessage, "web");

        var interpretation = ConciergeToolResultInterpreter.Decode(
            ConciergeToolDefinitions.GetRideHistory, serialized);
        var result = interpretation.Interpret(
            request,
            new Dictionary<string, object?> { ["when"] = when },
            includeRideIdsForCancellation);

        Assert.Equal("Ride history is temporarily unavailable. Please try again later.", result.GuestFacingText);
        Assert.Equal(result.GuestFacingText, result.CompactModelContext);
    }

    [Fact]
    public async Task EmptyRideHistoryStillReportsNoRides()
    {
        var handler = new RecordingHandler(_ => JsonResponse(HttpStatusCode.OK, Array.Empty<object>()));
        using var client = CreateClient(handler);
        var service = CreateService(client);
        var serialized = await service.GetRideHistoryAsync("upcoming", Arguments(), CancellationToken.None);
        var request = new ConciergeTurnSubmission("history-empty", "What rides do I have coming up?", "web");

        var result = ConciergeToolResultInterpreter.Decode(
                ConciergeToolDefinitions.GetRideHistory, serialized)
            .Interpret(
                request,
                new Dictionary<string, object?> { ["when"] = "upcoming" },
                includeRideIdsForCancellation: false);

        Assert.Equal("You don't have any upcoming rides.", result.GuestFacingText);
    }

    [Fact]
    public async Task CancelRideUsesScopedDeleteAndMapsConflict()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict));
        using var client = CreateClient(handler);
        var service = CreateService(client);

        var result = await service.CancelRideAsync(RideId.ToString("D"), Arguments(), CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Request.Method);
        Assert.Equal($"/rides/{RideId:D}", handler.Request.PathAndQuery);
        AssertVerifiedScope(handler.Request);
        var action = JsonSerializer.Deserialize<ConciergeRideActionResult>(result, JsonOptions)!;
        Assert.Equal("This ride can no longer be cancelled.", action.Message);
        Assert.Null(action.Ride);
        Assert.Empty(action.Rides);
    }

    [Fact]
    public async Task CancelRideEmitsTypedConfirmationOnlyAfterScopedDeleteCompletes()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var client = CreateClient(handler);
        var service = CreateService(client);

        var serialized = await service.CancelRideAsync(RideId.ToString("D"), Arguments(), CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Request.Method);
        Assert.Equal($"/rides/{RideId:D}", handler.Request.PathAndQuery);
        AssertVerifiedScope(handler.Request);
        using var result = JsonDocument.Parse(serialized);
        Assert.Equal(RideId, result.RootElement.GetProperty("confirmedCancellationRideId").GetGuid());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "That ride was not found for this stay.")]
    [InlineData(HttpStatusCode.Conflict, "This ride can no longer be cancelled.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "The ride service did not confirm cancellation. Check ride status before trying again.")]
    [InlineData(HttpStatusCode.Accepted, "The ride service did not confirm cancellation. Check ride status before trying again.")]
    public async Task CancelRideMapsServiceErrorsAndKeepsUnavailableOutcomeAmbiguous(
        HttpStatusCode statusCode,
        string expectedMessage)
    {
        var handler = new RecordingHandler(_ => ErrorResponse(statusCode));
        using var client = CreateClient(handler);
        var service = CreateService(client);

        var result = await service.CancelRideAsync(RideId.ToString("D"), Arguments(), CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, handler.Request.Method);
        Assert.Equal($"/rides/{RideId:D}", handler.Request.PathAndQuery);
        AssertVerifiedScope(handler.Request);
        var action = JsonSerializer.Deserialize<ConciergeRideActionResult>(result, JsonOptions)!;
        Assert.True(action.Simulated);
        Assert.Equal(expectedMessage, action.Message);
        Assert.Null(action.Ride);
        Assert.Empty(action.Rides);
        using var json = JsonDocument.Parse(result);
        Assert.True(!json.RootElement.TryGetProperty("confirmedCancellationRideId", out var confirmed)
            || confirmed.ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain("private-service-detail", result, StringComparison.Ordinal);
        Assert.DoesNotContain("private-host", result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST", 1)]
    [InlineData("DELETE", 1)]
    [InlineData("GET", 1 + ConciergeActionToolService.RideReadMaxRetries)]
    public async Task RideClientSendsSideEffectsOnceAndRetriesReads(string method, int expectedAttempts)
    {
        var attempts = 0;
        var services = new ServiceCollection();
        ConciergeActionToolService.AddRideServiceClient(services);
        services.AddHttpClient(ConciergeActionToolService.RideClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(_ =>
            {
                Interlocked.Increment(ref attempts);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }));
        services.Configure<HttpStandardResilienceOptions>(
            $"{ConciergeActionToolService.RideClientName}-standard",
            options => options.Retry.Delay = TimeSpan.Zero);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ConciergeActionToolService.RideClientName);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/rides"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(expectedAttempts, attempts);
    }

    private static ConciergeActionToolService CreateService(HttpClient client) =>
        new(
            null!,
            null!,
            null!,
            null!,
            new SingleClientFactory(client),
            TimeProvider.System,
            Options.Create(new HotelLocationOptions { TimeZoneId = "America/New_York" }),
            Options.Create(new RideSchedulingOptions()));

    private static HttpClient CreateClient(RecordingHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://ride-service.test") };

    private static AIFunctionArguments Arguments(bool includeProposal = false)
    {
        var values = new Dictionary<string, object?>
        {
            [ConciergeToolDefinitions.TrustedGuestIdKey] = GuestId.ToString("D"),
            [ConciergeToolDefinitions.TrustedStayIdKey] = StayId.ToString("D"),
            [ConciergeToolDefinitions.TrustedCheckoutDateKey] = CheckoutDate.ToString("yyyy-MM-dd"),
        };
        if (includeProposal)
        {
            var proposal = new ConciergeRideProposal(
                ProposalId,
                4,
                new ConciergeRidePlace("Hotel ZGY", 25.7984, -80.1956),
                new ConciergeRidePlace("South Beach", 25.7745, -80.1329),
                true,
                null,
                720);
            values[ConciergeToolDefinitions.TrustedProposalKey] = JsonSerializer.Serialize(proposal, JsonOptions);
        }

        return new AIFunctionArguments(values);
    }

    private static void AssertVerifiedScope(RecordedRequest request)
    {
        Assert.Equal(GuestId.ToString("D"), request.Headers["X-Verified-Guest-Id"]);
        Assert.Equal(StayId.ToString("D"), request.Headers["X-Verified-Stay-Id"]);
        Assert.Equal(CheckoutDate.ToString("yyyy-MM-dd"), request.Headers["X-Verified-Checkout-Date"]);
    }

    private static object RideResponse(DateTimeOffset pickupAt) =>
        new
        {
            id = RideId,
            guestId = GuestId,
            stayId = StayId,
            pickupName = "Hotel ZGY",
            pickupLatitude = 25.7984,
            pickupLongitude = -80.1956,
            dropoffName = "South Beach",
            dropoffLatitude = 25.7745,
            dropoffLongitude = -80.1329,
            immediate = true,
            pickupAt,
            bookedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
            status = "Booked",
            routeDurationSeconds = 720,
            driverName = "Alex",
            car = "Blue sedan",
            plate = "ABC-123",
        };

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode statusCode, T value) =>
        new(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode) =>
        new(statusCode)
        {
            Content = new StringContent(
                """{"code":"unavailable","message":"private-service-detail at private-host"}""",
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public RecordedRequest Request { get; private set; } = null!;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = new RecordedRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => string.Join(",", header.Value),
                    StringComparer.OrdinalIgnoreCase),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            return respond(request);
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string PathAndQuery,
        IReadOnlyDictionary<string, string> Headers,
        string? Body);
}
