namespace Concierge.WorkflowServer.Location;

/// <summary>
/// Shared provider call policy. Exceptions are rebuilt without the original message or
/// request URI because Geoapify requires its API key in the query string.
/// </summary>
internal static class LocationProviderHttp
{
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
}
