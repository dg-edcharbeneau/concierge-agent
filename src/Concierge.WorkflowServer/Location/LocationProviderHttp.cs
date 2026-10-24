using System.Text.Json;

namespace Concierge.WorkflowServer.Location;

/// <summary>
/// Shared provider call policy for live tool lookups (Geoapify and Open-Meteo). Exceptions are
/// rebuilt without the original message or request URI because Geoapify requires its API key in
/// the query string.
/// </summary>
/// <remarks>
/// Retry owner: Temporal. Weather retries at the activity level; nearby places, opening hours,
/// routes, and ride proposals run once. This client therefore has no HTTP retry handler.
/// </remarks>
internal static class LocationProviderHttp
{
    public const string ClientName = "location-providers";

    /// <summary>
    /// Provisional whole-request bound: connection, response headers, and response body. The
    /// tool activity timeouts in <c>ConciergeStayWorkflow</c> are derived from this value.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public static IHttpClientBuilder AddLocationProviderClient(this IServiceCollection services) =>
        services.AddHttpClient(ClientName, client => client.Timeout = RequestTimeout)
            // Factory request logging would record the credential-bearing Geoapify URI.
            .RemoveAllLoggers()
            .AddHttpMessageHandler(() => new SanitizingTransportHandler("Location provider"));

    /// <summary>
    /// Sends one GET and parses the JSON body within <see cref="HttpClient.Timeout"/> of the named
    /// client. <see cref="HttpCompletionOption.ResponseHeadersRead"/> means the client timeout alone
    /// stops at the headers, so a linked token also bounds the body read. A timeout surfaces as
    /// <see cref="TimeoutException"/>; caller cancellation stays an <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<JsonDocument> GetJsonAsync(
        IHttpClientFactory httpClientFactory,
        string uri,
        string provider,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        using var requestBound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (client.Timeout != Timeout.InfiniteTimeSpan)
        {
            requestBound.CancelAfter(client.Timeout);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await SendAsync(client, request, provider, requestBound.Token);
            await using var body = await response.Content.ReadAsStreamAsync(requestBound.Token);
            return await JsonDocument.ParseAsync(body, cancellationToken: requestBound.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{provider} request did not complete within its time limit.");
        }
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        string provider,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException(
                $"{provider} request failed due to a provider or network error.",
                null,
                exception.StatusCode);
        }

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException(
                $"{provider} request failed with HTTP status code {(int)statusCode}.",
                null,
                statusCode);
        }

        return response;
    }

    /// <summary>
    /// Rebuilds transport exceptions before any outer handler (for example a resilience handler's
    /// telemetry) can log them, so a credential-bearing request URI never reaches logs.
    /// </summary>
    internal sealed class SanitizingTransportHandler(string provider) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException exception)
            {
                throw new HttpRequestException(
                    $"{provider} request failed due to a provider or network error.", null, exception.StatusCode);
            }
            catch (OperationCanceledException)
            {
                throw new OperationCanceledException($"{provider} request was canceled.", cancellationToken);
            }
        }
    }
}
