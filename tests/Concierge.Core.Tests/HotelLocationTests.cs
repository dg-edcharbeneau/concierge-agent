using System.Net;
using System.Text;
using Concierge.WorkflowServer.Location;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Concierge.Core.Tests;

public sealed class HotelLocationTests
{
    private const string Secret = "FAKE_SENTINEL_STARTUP_KEY";
    private static readonly HotelLocationOptions Hotel = new()
    {
        Address = "2217 NW Miami Court, Miami, FL 33127",
        TimeZoneId = "America/New_York",
    };

    [Fact]
    public void RuntimeCoordinatesRequireInitializationAndCannotBeReplaced()
    {
        var location = new HotelLocation();
        Assert.Throws<InvalidOperationException>(() => location.Coordinates);
        var coordinates = new HotelCoordinates(25.7984634, -80.1956016);
        location.Initialize(coordinates);
        Assert.Equal(coordinates, location.Coordinates);
        Assert.Throws<InvalidOperationException>(() => location.Initialize(coordinates));
    }

    [Theory]
    [InlineData("amenity")]
    [InlineData("building")]
    public async Task StartupResolvesUnbiasedAddressAndPreservesConfiguredLabel(string type)
    {
        Uri? uri = null;
        using var host = CreateHost(request =>
        {
            uri = request.RequestUri;
            return Json(Result(type));
        });

        await host.ResolveHotelLocationAsync();

        var coordinates = host.Services.GetRequiredService<HotelLocation>().Coordinates;
        Assert.Equal(new HotelCoordinates(25.7984634, -80.1956016), coordinates);
        Assert.Contains(Uri.EscapeDataString(Hotel.Address), uri!.Query);
        Assert.Contains("filter=countrycode%3Aus", uri.Query);
        Assert.Contains("limit=2", uri.Query);
        Assert.DoesNotContain("bias", uri.Query);
        Assert.Equal("Hotel ZGY, " + Hotel.Address, Hotel.Label);
    }

    [Theory]
    [InlineData("""{"results":[]}""")]
    [InlineData("""{"results":[{"result_type":"street","lat":25.8,"lon":-80.2,"rank":{"confidence":1}}]}""")]
    [InlineData("""{"results":[{"result_type":"building","lat":25.8,"lon":-80.2,"rank":{"confidence":0.89}}]}""")]
    [InlineData("""{"results":[{"result_type":"building","lon":-80.2,"rank":{"confidence":1}}]}""")]
    [InlineData("""{"results":[{"result_type":"building","lat":91,"lon":-80.2,"rank":{"confidence":1}}]}""")]
    [InlineData("""{"results":[{"result_type":"building","lat":25.8,"lon":-181,"rank":{"confidence":1}}]}""")]
    [InlineData("""{"results":[{"result_type":"building","lat":25.8,"lon":-80.2,"rank":{"confidence":1.1}}]}""")]
    [InlineData("""{"results":[{},{}]}""")]
    [InlineData("""{"results":[{"result_type":"building","lat":"wrong","lon":-80.2,"rank":{"confidence":1}}]}""")]
    [InlineData("not json")]
    public async Task InvalidOrAmbiguousAddressFailsBeforeLocationIsAvailable(string json)
    {
        using var host = CreateHost(_ => Json(json));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ResolveHotelLocationAsync());
        Assert.Contains("Hotel:Address", error.Message);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.Throws<InvalidOperationException>(() =>
            host.Services.GetRequiredService<HotelLocation>().Coordinates);
    }

    [Theory]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(503, 4)]
    public async Task StartupUsesStandardResilienceAndSafeErrors(int status, int expectedAttempts)
    {
        var attempts = 0;
        using var host = CreateHost(_ =>
        {
            attempts++;
            return new HttpResponseMessage((HttpStatusCode)status);
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ResolveHotelLocationAsync());
        Assert.Equal(expectedAttempts, attempts);
        Assert.Contains(status.ToString(), error.Message);
        Assert.DoesNotContain(Secret, error.ToString());
    }

    [Fact]
    public async Task TransientResponseRetriesAndThenPublishesLocation()
    {
        var attempts = 0;
        using var host = CreateHost(_ => ++attempts == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(Result("amenity")));
        await host.ResolveHotelLocationAsync();
        Assert.Equal(2, attempts);
        Assert.Equal(25.7984634, host.Services.GetRequiredService<HotelLocation>().Coordinates.Latitude);
    }

    [Fact]
    public async Task TransportExceptionIsSanitizedBeforeResilienceLogging()
    {
        var logs = new CaptureLogs();
        using var host = CreateHost(request =>
            throw new HttpRequestException("Failed " + request.RequestUri), logs);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ResolveHotelLocationAsync());

        Assert.DoesNotContain(Secret, error.ToString());
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, message => message.Contains(Secret, StringComparison.Ordinal));
        Assert.Contains(logs.Messages, message => message.Contains("Retry", StringComparison.OrdinalIgnoreCase));
    }

    private static IHost CreateHost(Func<HttpRequestMessage, HttpResponseMessage> responder, CaptureLogs? logs = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(logs);
        }
        builder.AddServiceDefaults();
        builder.Services.AddSingleton<IOptions<HotelLocationOptions>>(Options.Create(Hotel));
        builder.Services.AddSingleton<IOptions<GeoapifyOptions>>(Options.Create(new GeoapifyOptions { ApiKey = Secret }));
        builder.Services.AddHotelLocation();
        builder.Services.AddHttpClient(HotelAddressGeocoder.ClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new Responder(responder));
        return builder.Build();
    }

    private static string Result(string type) =>
        $$$"""{"results":[{"result_type":"{{{type}}}","lat":25.7984634,"lon":-80.1956016,"formatted":"Arlo Wynwood","rank":{"confidence":1}}]}""";

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
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
                Func<TState, Exception?, string> formatter) =>
                messages.Add($"{formatter(state, exception)} {exception} {state}");
        }
    }
}
