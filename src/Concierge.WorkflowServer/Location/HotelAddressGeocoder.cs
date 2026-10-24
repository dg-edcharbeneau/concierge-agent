using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Location;

/// <summary>Resolves the configured address without depending on the hotel-biased place resolver.</summary>
public sealed class HotelAddressGeocoder(
    IHttpClientFactory httpClientFactory,
    IOptions<HotelLocationOptions> hotelOptions,
    IOptions<GeoapifyOptions> geoapifyOptions)
{
    public const string ClientName = "hotel-address-geocoding";

    // Provisional startup bounds: 5-second attempts, 20 seconds for all attempts and backoff, and
    // a 25-second deadline that also covers the response body read outside the resilience handler.
    internal static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan TotalRequestTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan Deadline = TotalRequestTimeout + TimeSpan.FromSeconds(5);

    public async Task<HotelCoordinates> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var address = hotelOptions.Value.Address;
        var key = geoapifyOptions.Value.ApiKey;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Includes response-body reading, which is outside the resilience handler's total timeout.
        deadline.CancelAfter(Deadline);
        try
        {
            using var client = httpClientFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://api.geoapify.com/v1/geocode/search"
                + $"?text={Uri.EscapeDataString(address)}&filter=countrycode%3Aus&limit=2&format=json"
                + $"&apiKey={Uri.EscapeDataString(key)}");
            using var response = await LocationProviderHttp.SendAsync(client, request, "Geoapify", deadline.Token);
            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(deadline.Token),
                cancellationToken: deadline.Token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array
                || results.GetArrayLength() != 1)
            {
                throw Failure("Expected exactly one address match; make the street address unambiguous.");
            }

            var result = results[0];
            if (result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("result_type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() is not ("building" or "amenity")
                || !result.TryGetProperty("rank", out var rank)
                || !TryNumber(rank, "confidence", out var confidence)
                || confidence is < 0.9 or > 1
                || !TryNumber(result, "lat", out var latitude) || latitude is < -90 or > 90
                || !TryNumber(result, "lon", out var longitude) || longitude is < -180 or > 180)
            {
                throw Failure("Expected a building or amenity match with confidence at least 0.9 and valid coordinates. Check the full street address.");
            }

            // Provider names (for example, the real hotel at this address) never replace Hotel:Address's label.
            return new HotelCoordinates(latitude, longitude);
        }
        catch (HttpRequestException exception)
        {
            throw Failure(exception.StatusCode is { } status
                ? $"Geoapify returned HTTP {(int)status}. Check Geoapify:ApiKey, quota, and provider availability."
                : "Geoapify could not be reached. Check network connectivity and provider availability.");
        }
        catch (JsonException)
        {
            throw Failure("Geoapify returned invalid address data. Check provider availability and the full street address.");
        }
        catch (Exception exception) when (exception is OperationCanceledException or Polly.Timeout.TimeoutRejectedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw Failure("Geoapify address lookup timed out. Check network connectivity and provider availability.");
        }
        catch (Polly.CircuitBreaker.BrokenCircuitException)
        {
            throw Failure("Geoapify is temporarily unavailable. Retry startup after checking provider availability.");
        }
    }

    private static InvalidOperationException Failure(string reason) =>
        new($"Unable to resolve Hotel:Address at startup. {reason}");

    private static bool TryNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value)
            && double.IsFinite(value);
    }
}
