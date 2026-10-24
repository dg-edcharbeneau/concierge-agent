namespace Concierge.Core.Tests;

/// <summary>Hands the same test client to services that create clients per operation.</summary>
internal sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}
