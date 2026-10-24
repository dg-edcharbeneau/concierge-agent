namespace Concierge.WorkflowServer.Location;

public static class HotelLocationExtensions
{
    public static IServiceCollection AddHotelLocation(this IServiceCollection services)
    {
        services.AddSingleton<HotelLocation>();
        services.AddSingleton<HotelAddressGeocoder>();
        var geocoding = services.AddHttpClient(HotelAddressGeocoder.ClientName).RemoveAllLoggers();
        // Retry owner: this startup HTTP policy (no Temporal activity wraps the startup geocode).
        // Provisional bounds; HotelAddressGeocoder.Deadline also covers the response-body read.
        geocoding.AddStandardResilienceHandler(options =>
        {
            options.AttemptTimeout.Timeout = HotelAddressGeocoder.AttemptTimeout;
            options.TotalRequestTimeout.Timeout = HotelAddressGeocoder.TotalRequestTimeout;
            options.Retry.MaxRetryAttempts = 3;
            options.Retry.Delay = TimeSpan.FromSeconds(1);
        });
        // Must be inside resilience so Polly never sees credential-bearing transport exceptions.
        geocoding.AddHttpMessageHandler(() => new LocationProviderHttp.SanitizingTransportHandler("Geoapify"));
        // Live tool lookups: no HTTP retries (Temporal owns them), whole-request bound per call.
        services.AddLocationProviderClient();
        return services;
    }

    public static async Task ResolveHotelLocationAsync(this IHost host)
    {
        var coordinates = await host.Services.GetRequiredService<HotelAddressGeocoder>()
            .ResolveAsync(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        host.Services.GetRequiredService<HotelLocation>().Initialize(coordinates);
        host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(HotelLocation))
            .LogInformation(
                "Resolved Hotel:Address to {Latitude}, {Longitude}.",
                coordinates.Latitude,
                coordinates.Longitude);
    }
}
