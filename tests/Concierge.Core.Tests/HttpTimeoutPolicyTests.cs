using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.RideService.Workflows;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Location;
using Concierge.WorkflowServer.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Concierge.Core.Tests;

/// <summary>
/// Retry ownership and time bounds, exercised through the production client registrations
/// (ServiceDefaults plus the named-client methods) with only the primary handler stubbed.
/// </summary>
public sealed class HttpTimeoutPolicyTests
{
    private const string Secret = "FAKE_SENTINEL_LOCATION_KEY";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan Margin = ConciergeToolDefinitions.ActivityCleanupMargin;

    // Shrunk ride budget so hanging-call tests stay fast; the relationships match production.
    private static readonly TimeSpan TestAttempt = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan TestTotal = TimeSpan.FromMilliseconds(800);

    [Theory]
    [InlineData("GET", HttpStatusCode.ServiceUnavailable, 1 + ConciergeActionToolService.RideReadMaxRetries)]
    [InlineData("GET", HttpStatusCode.TooManyRequests, 1 + ConciergeActionToolService.RideReadMaxRetries)]
    [InlineData("GET", HttpStatusCode.BadRequest, 1)]
    [InlineData("GET", HttpStatusCode.Unauthorized, 1)]
    [InlineData("POST", HttpStatusCode.ServiceUnavailable, 1)]
    [InlineData("POST", HttpStatusCode.RequestTimeout, 1)]
    [InlineData("DELETE", HttpStatusCode.ServiceUnavailable, 1)]
    public async Task RideClientRetriesOnlyTransientReadsWithoutAServiceDefaultsHandler(
        string method, HttpStatusCode status, int expectedAttempts)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var host = RideHost(handler, shrink: true);
        var client = host.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ConciergeActionToolService.RideClientName);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/rides"));

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(expectedAttempts, handler.Attempts);
    }

    [Fact]
    public async Task HangingRideReadRetriesWithinTotalBudgetAndReleasesEveryAttempt()
    {
        var handler = new StubHandler(Hang);
        using var host = RideHost(handler, shrink: true);
        var client = host.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ConciergeActionToolService.RideClientName);
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => client.GetAsync("/rides/1"));

        Assert.True(error is TimeoutRejectedException or OperationCanceledException, error.GetType().Name);
        Assert.Equal(1 + ConciergeActionToolService.RideReadMaxRetries, handler.Attempts);
        Assert.Equal(handler.Attempts, handler.Released);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TestTotal + TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task HangingRideMutationIsSentOnceAndBoundedByTotalNotReadAttemptTimeout(string method)
    {
        var handler = new StubHandler(Hang);
        using var host = RideHost(handler, shrink: true);
        var client = host.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ConciergeActionToolService.RideClientName);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/rides")));

        Assert.Equal(1, handler.Attempts);
        Assert.Equal(1, handler.Released);
        // The single mutation attempt gets the whole budget, not the 200 ms read attempt timeout.
        Assert.InRange(stopwatch.Elapsed, TestTotal - TimeSpan.FromMilliseconds(150), TestTotal + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SlowRideMutationLongerThanReadAttemptTimeoutStillSucceeds()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TestAttempt * 2, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var host = RideHost(handler, shrink: true);
        var client = host.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ConciergeActionToolService.RideClientName);

        using var response = await client.PostAsync("/rides", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task RideResponseBodyHangIsBoundedByClientTimeout()
    {
        var body = new HangingStream();
        var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        using var host = RideHost(handler, shrink: true, clientTimeout: TimeSpan.FromMilliseconds(500));
        var client = host.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(ConciergeActionToolService.RideClientName);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("/rides/1"));

        Assert.Equal(1, handler.Attempts);
        Assert.True(await body.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ConfirmRideTimeoutSurfacesAsUnknownWithoutResend()
    {
        var bodies = new List<string>();
        var handler = new StubHandler(async (request, cancellationToken) =>
        {
            bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        });
        using var host = RideHost(handler, shrink: true);
        var service = RideToolService(host.Services.GetRequiredService<IHttpClientFactory>());

        var first = await Assert.ThrowsAnyAsync<Exception>(
            () => service.ConfirmRideAsync(ProposalId.ToString("D"), RideArguments(), CancellationToken.None));
        // A repeated activity for the same admitted turn carries the same idempotency key.
        await Assert.ThrowsAnyAsync<Exception>(
            () => service.ConfirmRideAsync(ProposalId.ToString("D"), RideArguments(), CancellationToken.None));

        // The activity wrapper treats timeouts as activity failures; ConfirmRide has one attempt and
        // the workflow renders its failure as an explicitly unknown outcome.
        Assert.True(first is TimeoutRejectedException or OperationCanceledException, first.GetType().Name);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal(1, ConciergeToolDefinitions.RetryPolicyFor(ConciergeToolDefinitions.ConfirmRide).MaximumAttempts);
        Assert.Equal(1, ConciergeToolDefinitions.RetryPolicyFor(ConciergeToolDefinitions.ConfirmRideCancellation).MaximumAttempts);
        Assert.Contains("did not confirm the outcome",
            ConciergeToolResultInterpreter.ToolFailureReply(ConciergeToolDefinitions.ConfirmRide));
        var keys = bodies.Select(body => JsonDocument.Parse(body).RootElement.GetProperty("idempotencyKey").GetString()).ToArray();
        Assert.Equal(2, keys.Length);
        Assert.Equal(keys[0], keys[1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task LocationClientNeverRetriesAtHttpLevel(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var host = LocationHost(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => FindNearby(host, CancellationToken.None));

        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, handler.Attempts);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task LocationHeaderHangTimesOutWithinRequestBound()
    {
        var handler = new StubHandler(Hang);
        using var host = LocationHost(handler, TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => FindNearby(host, CancellationToken.None));

        Assert.Equal(1, handler.Attempts);
        Assert.Equal(1, handler.Released);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LocationBodyHangTimesOutWithinRequestBound()
    {
        var body = new HangingStream();
        var handler = new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        using var host = LocationHost(handler, TimeSpan.FromMilliseconds(300));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<TimeoutException>(() => FindNearby(host, CancellationToken.None));

        Assert.True(await body.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CallerCancellationReleasesLocationRequestAndIsNotReportedAsTimeout()
    {
        var handler = new StubHandler(Hang);
        using var host = LocationHost(handler);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FindNearby(host, caller.Token));

        Assert.IsNotType<TimeoutException>(error);
        Assert.Equal(1, handler.Released);
    }

    [Fact]
    public async Task LocationTransportFailureNeverLogsTheCredential()
    {
        var logs = new CaptureLogs();
        var handler = new StubHandler((request, _) =>
            throw new HttpRequestException("Failed " + request.RequestUri));
        using var host = LocationHost(handler, logs: logs);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => FindNearby(host, CancellationToken.None));

        Assert.Equal(1, handler.Attempts);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain(logs.Messages, message => message.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void ActivityTimeoutsCoverInnerWorstCase()
    {
        var location = LocationProviderHttp.RequestTimeout;
        var ride = ConciergeActionToolService.RideClientTimeout;
        TimeSpan Tool(string name) => ConciergeStayWorkflow.ToolActivityTimeout(name);

        // Location tools: sequential single-attempt requests per activity attempt.
        Assert.True(Tool(ConciergeToolDefinitions.GetWeather) >= 2 * location + Margin);
        Assert.True(Tool(ConciergeToolDefinitions.FindNearbyPlaces) >= location + Margin);
        Assert.True(Tool(ConciergeToolDefinitions.GetPlaceOpeningHours) >= location + Margin);
        Assert.True(Tool(ConciergeToolDefinitions.GetRoute) >= 3 * location + Margin);
        Assert.True(Tool(ConciergeToolDefinitions.ProposeRide) >= 3 * location + Margin);

        // Ride tools: one ride-service call bounded by the client timeout.
        foreach (var name in new[]
                 {
                     ConciergeToolDefinitions.GetRideStatus, ConciergeToolDefinitions.GetRideHistory,
                     ConciergeToolDefinitions.ProposeRideCancellation, ConciergeToolDefinitions.ConfirmRideCancellation,
                     ConciergeToolDefinitions.ConfirmRide,
                 })
        {
            Assert.True(Tool(name) >= ride + Margin, name);
        }

        // Read retries fit inside the total; the total plus body buffering fits the client timeout.
        var reads = (1 + ConciergeActionToolService.RideReadMaxRetries) * ConciergeActionToolService.RideReadAttemptTimeout
            + ConciergeActionToolService.RideReadMaxRetries * ConciergeActionToolService.RideReadRetryDelay;
        Assert.True(reads <= ConciergeActionToolService.RideRequestTotalTimeout);
        Assert.True(ConciergeActionToolService.RideRequestTotalTimeout < ride);

        // RideService finishes its admission/cancellation update inside the single HTTP attempt.
        Assert.True(RideBookingWorkflow.StoreActivityOptions.ScheduleToCloseTimeout
            < ConciergeActionToolService.RideRequestTotalTimeout);
        Assert.True(RideBookingWorkflow.CancelActivityOptions.ScheduleToCloseTimeout
            < ConciergeActionToolService.RideRequestTotalTimeout);

        // Startup geocoding: attempts fit the total; the deadline also covers the body read.
        Assert.True(HotelAddressGeocoder.AttemptTimeout <= HotelAddressGeocoder.TotalRequestTimeout);
        Assert.True(HotelAddressGeocoder.Deadline > HotelAddressGeocoder.TotalRequestTimeout);
    }

    [Fact]
    public void ActivityAllowanceHonorsShortenedStartToCloseNearTurnDeadline()
    {
        var started = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        // The workflow capped StartToClose at 2 s of remaining turn time; 1 s has elapsed.
        Assert.Equal(
            TimeSpan.FromSeconds(1) - Margin,
            ConciergeToolDefinitions.RemainingActivityAllowance(started, TimeSpan.FromSeconds(2), started.AddSeconds(1)));
        // Already inside the cleanup margin: cancel immediately.
        Assert.Equal(
            TimeSpan.Zero,
            ConciergeToolDefinitions.RemainingActivityAllowance(started, TimeSpan.FromMilliseconds(300), started));
        Assert.Null(ConciergeToolDefinitions.RemainingActivityAllowance(started, null, started));
    }

    private static readonly Guid GuestId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid StayId = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private static readonly Guid ProposalId = Guid.Parse("12345678-1234-4234-9234-123456789abc");

    private static async Task<HttpResponseMessage> Hang(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new UnreachableException();
    }

    private static IHost RideHost(StubHandler handler, bool shrink, TimeSpan? clientTimeout = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.AddServiceDefaults();
        ConciergeActionToolService.AddRideServiceClient(builder.Services);
        var http = builder.Services.AddHttpClient(ConciergeActionToolService.RideClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        if (clientTimeout is { } timeout)
        {
            http.ConfigureHttpClient(client => client.Timeout = timeout);
        }

        if (shrink)
        {
            builder.Services.Configure<HttpStandardResilienceOptions>(
                $"{ConciergeActionToolService.RideClientName}-standard",
                options =>
                {
                    options.AttemptTimeout.Timeout = TestAttempt;
                    options.TotalRequestTimeout.Timeout = TestTotal;
                    options.Retry.Delay = TimeSpan.Zero;
                });
        }

        return builder.Build();
    }

    private static IHost LocationHost(StubHandler handler, TimeSpan? requestTimeout = null, CaptureLogs? logs = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(logs);
        }

        builder.AddServiceDefaults();
        builder.Services.AddSingleton<IOptions<HotelLocationOptions>>(Options.Create(new HotelLocationOptions
        {
            Address = "2217 NW Miami Court, Miami, FL 33127",
            TimeZoneId = "America/New_York",
        }));
        builder.Services.AddSingleton<IOptions<GeoapifyOptions>>(Options.Create(new GeoapifyOptions { ApiKey = Secret }));
        builder.Services.AddHotelLocation();
        builder.Services.AddSingleton<GeoapifyNearbyPlacesService>();
        var http = builder.Services.AddHttpClient(LocationProviderHttp.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        if (requestTimeout is { } timeout)
        {
            http.ConfigureHttpClient(client => client.Timeout = timeout);
        }

        var host = builder.Build();
        host.Services.GetRequiredService<HotelLocation>().Initialize(new HotelCoordinates(25.7984634, -80.1956016));
        return host;
    }

    private static Task<IReadOnlyList<NearbyPlace>> FindNearby(IHost host, CancellationToken cancellationToken) =>
        host.Services.GetRequiredService<GeoapifyNearbyPlacesService>()
            .FindAsync(NearbyPlaceCategories.All[0], cancellationToken);

    private static ConciergeActionToolService RideToolService(IHttpClientFactory factory) =>
        new(
            null!,
            null!,
            null!,
            null!,
            factory,
            TimeProvider.System,
            Options.Create(new HotelLocationOptions { TimeZoneId = "America/New_York" }));

    private static AIFunctionArguments RideArguments()
    {
        var proposal = new ConciergeRideProposal(
            ProposalId,
            4,
            new ConciergeRidePlace("Hotel ZGY", 25.7984, -80.1956),
            new ConciergeRidePlace("South Beach", 25.7745, -80.1329),
            true,
            null,
            720);
        return new AIFunctionArguments(new Dictionary<string, object?>
        {
            [ConciergeToolDefinitions.TrustedGuestIdKey] = GuestId.ToString("D"),
            [ConciergeToolDefinitions.TrustedStayIdKey] = StayId.ToString("D"),
            [ConciergeToolDefinitions.TrustedCheckoutDateKey] = "2026-10-25",
            [ConciergeToolDefinitions.TrustedProposalKey] = JsonSerializer.Serialize(proposal, JsonOptions),
        });
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int attempts;
        private int released;

        public int Attempts => Volatile.Read(ref attempts);

        /// <summary>Attempts whose cancellation token was observed and honored.</summary>
        public int Released => Volatile.Read(ref released);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref attempts);
            try
            {
                return await respond(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref released);
                throw;
            }
        }
    }

    /// <summary>Response body that never produces data until its read is canceled.</summary>
    private sealed class HangingStream : Stream
    {
        public TaskCompletionSource<bool> Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                Canceled.TrySetResult(true);
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CaptureLogs : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(Messages);
        public void Dispose() { }

        private sealed class Capture(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add($"{formatter(state, exception)} {exception} {state}");
                }
            }
        }
    }
}
