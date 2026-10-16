using System.Net;
using System.Text;
using System.Text.Json;
using Concierge.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Concierge.Core.Tests;

public sealed class ThinkPortIsolationTests
{
    private const int ThinkPort = 5443;

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    [InlineData("Production", true)]
    public async Task AllowedPostReachesThinkHandlerWithoutDispatchingSelectedEndpoint(
        string environment, bool selectEndpoint)
    {
        using var factory = CreateFactory(environment, ThinkPort, selectEndpoint);
        using var http = CreateClient(factory);
        foreach (var path in new[] { "/v1/chat/completions", "/V1/CHAT/COMPLETIONS?probe=1" })
        {
            using var response = await http.PostAsync(path, new StringContent(
                """{"model":"concierge","messages":[{"role":"user","content":"Hello"}]}""",
                Encoding.UTF8, "application/json"));

            // No voice bearer: the real handler answers with its OpenAI-shaped 401.
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = json.RootElement.GetProperty("error");
            Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
            Assert.Equal("invalid_api_key", error.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, error.GetProperty("param").ValueKind);
            Assert.False(response.Headers.Contains("Strict-Transport-Security"));
            Assert.False(response.Headers.Contains("Location"));
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task OtherThinkRequestsStayEmptyEvenForSelectedUiAndHealthEndpoints(string environment)
    {
        using var factory = CreateFactory(environment, ThinkPort);
        using var http = CreateClient(factory);
        foreach (var (method, path) in new[]
        {
            ("GET", "/v1/chat/completions"), ("HEAD", "/v1/chat/completions"),
            ("OPTIONS", "/v1/chat/completions"), ("PUT", "/v1/chat/completions"),
            ("DELETE", "/v1/chat/completions"), ("PATCH", "/v1/chat/completions"),
            ("POST", "/v1/chat/completions/"), ("POST", "/v1/chat/completions/extra"),
            ("GET", "/"), ("POST", "/"), ("GET", "/Error"), ("GET", "/not-found"),
            ("GET", "/health"), ("GET", "/health/ready"), ("GET", "/api/concierge/history"),
        })
        {
            using var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            await AssertEmptyNotFound(response);
        }
    }

    [Theory]
    [InlineData("Development", "5443")]
    [InlineData("Production", "5443")]
    [InlineData("Production", null)]
    [InlineData("Production", "invalid")]
    [InlineData("Production", "0")]
    [InlineData("Production", "65536")]
    public async Task NormalListenerBlocksThinkButPreservesUiApiAndHealth(string environment, string? configuredPort)
    {
        using var factory = CreateFactory(environment, 7443, configuredPort: configuredPort);
        using var http = CreateClient(factory);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Get })
        {
            using var response = await http.SendAsync(
                new HttpRequestMessage(method, "/V1/CHAT/COMPLETIONS"));
            await AssertEmptyNotFound(response);
        }

        using var ui = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal("text/html", ui.Content.Headers.ContentType?.MediaType);
        using var health = await http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var api = await http.GetAsync("/api/concierge/history");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal("application/json", api.Content.Headers.ContentType?.MediaType);
        using var notFound = await http.GetAsync("/synthetic-not-found");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("text/html", notFound.Content.Headers.ContentType?.MediaType);
    }

    private static async Task AssertEmptyNotFound(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Null(response.Content.Headers.ContentType);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        Assert.False(response.Headers.Contains("Location"));
    }

    // A non-localhost HTTPS host makes Production HSTS observable if the gate moves too late.
    private static HttpClient CreateClient(WebApplicationFactory<ConciergeService> factory) =>
        factory.CreateClient(new() { BaseAddress = new Uri("https://hotel.example"), AllowAutoRedirect = false });

    private static WebApplicationFactory<ConciergeService> CreateFactory(
        string environment, int localPort, bool selectEndpoint = false, string? configuredPort = "5443") =>
        new WebApplicationFactory<ConciergeService>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("THINK_PORT", configuredPort ?? "");
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new ConnectionFilter(localPort, selectEndpoint)));
        });

    // TestServer has no real listener. Set its server-observed connection feature, not Host,
    // and optionally emulate an endpoint selected before the user's WebApplication pipeline.
    private sealed class ConnectionFilter(int localPort, bool selectEndpoint) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.LocalPort = localPort;
                if (selectEndpoint)
                {
                    context.SetEndpoint(new Endpoint(
                        _ => throw new InvalidOperationException("The selected UI endpoint must not execute."),
                        new EndpointMetadataCollection(), "Selected UI endpoint"));
                }
                return continuation(context);
            });
            next(app);
        };
    }
}
