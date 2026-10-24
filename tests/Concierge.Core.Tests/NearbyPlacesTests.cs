using System.Net;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Location;
using Microsoft.Extensions.Options;

namespace Concierge.Core.Tests;

public sealed class NearbyPlacesTests
{
    private static readonly HotelCoordinates Hotel = new(25.7983, -80.1997);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SearchIsACircleAroundTheHotelBiasedToItsCoordinates()
    {
        HttpRequestMessage? sent = null;
        var service = CreateService(request =>
        {
            sent = request;
            return Json(new { type = "FeatureCollection", features = Array.Empty<object>() });
        });

        await service.FindAsync(Category("bar"));

        Assert.NotNull(sent);
        Assert.Equal(HttpMethod.Get, sent.Method);
        Assert.Equal("https://api.geoapify.com/v2/places", sent.RequestUri!.GetLeftPart(UriPartial.Path));
        var query = Query(sent.RequestUri);
        Assert.Equal("catering.bar,catering.pub", query["categories"]);
        Assert.Equal("circle:-80.1997,25.7983,8000", query["filter"]);
        Assert.Equal("proximity:-80.1997,25.7983", query["bias"]);
        Assert.Equal("10", query["limit"]);
        Assert.Equal("en", query["lang"]);
        Assert.False(query.ContainsKey("name"));
        Assert.Equal("test-places-key", query["apiKey"]);
    }

    [Fact]
    public void ParsingKeepsTheThreeClosestNamedPlacesWithStreetAddresses()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "type": "FeatureCollection",
              "features": [
                { "properties": { "name": "Far Pharmacy", "street": "Biscayne Boulevard", "housenumber": "900",
                                  "lat": 25.80, "lon": -80.19, "distance": 2400, "place_id": "far" } },
                { "properties": { "street": "NW 2nd Avenue", "lat": 25.79, "lon": -80.20, "distance": 50, "place_id": "unnamed" } },
                { "properties": { "name": "CVS", "street": "NW 20th Street", "housenumber": "120",
                                  "lat": 25.799, "lon": -80.198, "distance": 310, "place_id": "cvs" } },
                { "properties": { "name": "CVS", "street": "NW 20th Street", "housenumber": "120",
                                  "lat": 25.799, "lon": -80.198, "distance": 310, "place_id": "cvs" } },
                { "properties": { "name": "Walgreens", "address_line1": "Walgreens", "lat": 25.801, "lon": -80.2,
                                  "distance": 800, "place_id": "wag" } },
                { "properties": { "name": "Corner Drugs", "address_line1": "55 NW 22nd Street",
                                  "lat": 25.79, "lon": -80.21, "place_id": "corner" } },
                { "properties": { "name": "Bad Coordinates", "lat": 125.0, "lon": -80.2, "distance": 1 } }
              ]
            }
            """);

        var places = GeoapifyNearbyPlacesService.ReadPlaces(document.RootElement, Hotel);

        Assert.Equal(["CVS", "Walgreens", "Corner Drugs"], places.Select(place => place.Name));
        Assert.Equal("120 NW 20th Street", places[0].Address);
        // An address line that only repeats the place name is not an address.
        Assert.Null(places[1].Address);
        Assert.Equal("55 NW 22nd Street", places[2].Address);
        // No reported distance: computed from the hotel (about 1.4 km away).
        Assert.InRange(places[2].DistanceMeters, 1_000, 1_800);
    }

    [Fact]
    public void ParsingRejectsAResponseWithoutFeatures()
    {
        using var document = JsonDocument.Parse("""{ "error": "Unauthorized" }""");

        Assert.Throws<InvalidDataException>(() => GeoapifyNearbyPlacesService.ReadPlaces(document.RootElement, Hotel));
    }

    [Fact]
    public async Task ProviderFailureDoesNotLeakTheApiKey()
    {
        var service = CreateService(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.FindAsync(Category("pharmacy")));

        Assert.DoesNotContain("test-places-key", failure.ToString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, failure.StatusCode);
    }

    [Fact]
    public async Task ToolResultReportsMilesAndNoResults()
    {
        var found = CreateTool(_ => Json(new
        {
            features = new[]
            {
                new { properties = new { name = "CVS", street = "NW 20th Street", housenumber = "120", lat = 25.799, lon = -80.198, distance = 805.0, place_id = "cvs" } },
            },
        }));
        var empty = CreateTool(_ => Json(new { features = Array.Empty<object>() }));

        var result = JsonSerializer.Deserialize<ConciergeNearbyPlacesToolResult>(
            await found.FindNearbyPlacesAsync("pharmacy", CancellationToken.None), JsonOptions)!;
        var none = JsonSerializer.Deserialize<ConciergeNearbyPlacesToolResult>(
            await empty.FindNearbyPlacesAsync("pharmacy", CancellationToken.None), JsonOptions)!;

        Assert.Equal("Available", result.Status);
        Assert.Equal("pharmacy", result.Category);
        Assert.Equal(new ConciergeNearbyPlace("CVS", "120 NW 20th Street", 0.5), Assert.Single(result.Places));
        Assert.Equal("NoResults", none.Status);
        Assert.Empty(none.Places);
    }

    [Theory]
    [InlineData("drugstore")]
    [InlineData("Pharmacy")]
    [InlineData("")]
    public async Task ToolServiceRejectsUnknownCategories(string category)
    {
        var tool = CreateTool(_ => throw new InvalidOperationException("must not call Geoapify"));

        await Assert.ThrowsAsync<ArgumentException>(
            () => tool.FindNearbyPlacesAsync(category, CancellationToken.None));
    }

    [Fact]
    public void WebRendersAMarkdownListOfTheClosestPlaces()
    {
        var text = Render(
            "web",
            new ConciergeNearbyPlacesToolResult("Available", "pharmacy",
            [
                new("Walgreens", null, 0.8),
                new("CVS *24h*", "120 NW 20th Street", 0.2),
            ]));

        Assert.Equal(
            "Here are the closest pharmacies to Hotel ZGY:\n\n"
            + "1. **CVS \\*24h\\***, 120 NW 20th Street (0.2 miles)\n"
            + "2. **Walgreens** (0.8 miles)",
            text);
    }

    [Fact]
    public void VoiceRendersPlainSentences()
    {
        var text = Render(
            "voice",
            new ConciergeNearbyPlacesToolResult("Available", "atm",
            [
                new("Bank ATM", "1 Main Street", 0.05),
                new("Corner ATM", null, 0.4),
                new("Mall ATM", null, 1.2),
            ]));

        Assert.Equal(
            "The closest ATM is Bank ATM at 1 Main Street, less than 0.1 miles from Hotel ZGY. "
            + "Other options are Corner ATM, 0.4 miles away and Mall ATM, 1.2 miles away.",
            text);
        Assert.DoesNotContain("**", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoResultsAndUnknownCategoriesGetCuratedReplies()
    {
        Assert.Equal(
            "I couldn't find an ATM within about five miles of Hotel ZGY. The front desk may be able to suggest one.",
            Render("voice", new ConciergeNearbyPlacesToolResult("NoResults", "atm", [])));
        Assert.Equal(
            "I couldn't find a pharmacy within about five miles of Hotel ZGY. The front desk may be able to suggest one.",
            Render("web", new ConciergeNearbyPlacesToolResult("NoResults", "pharmacy", [])));

        var unknown = ConciergeToolResultInterpreter.Decode(
            ConciergeToolDefinitions.FindNearbyPlaces,
            JsonSerializer.Serialize(new ConciergeNearbyPlacesToolResult("Available", "casino", []), JsonOptions));
        Assert.False(unknown.ShouldReturnRenderedToolAnswer);
        Assert.Equal(
            "I couldn't look up nearby places just now. Please try again in a moment.",
            ConciergeToolResultInterpreter.ToolFailureReply(ConciergeToolDefinitions.FindNearbyPlaces));
    }

    [Theory]
    [InlineData("pharmacy", true)]
    [InlineData("atm", true)]
    [InlineData("cinema", true)]
    [InlineData("drugstore", false)]
    [InlineData(null, false)]
    public void PolicyAcceptsOnlyKnownCategories(string? category, bool valid)
    {
        var arguments = new Dictionary<string, object?> { ["category"] = category };

        Assert.Equal(valid, ConciergeToolCallPolicy.TryValidateArguments(
            ConciergeToolDefinitions.FindNearbyPlaces, arguments, out var error));
        Assert.Equal(valid, error is null);
    }

    [Fact]
    public void PolicyRejectsUnexpectedArguments()
    {
        var arguments = new Dictionary<string, object?> { ["category"] = "pharmacy", ["near"] = "Miami Beach" };

        Assert.False(ConciergeToolCallPolicy.TryValidateArguments(
            ConciergeToolDefinitions.FindNearbyPlaces, arguments, out _));
    }

    [Fact]
    public async Task MovieTheatersUseGeoapifyCinemaCategory()
    {
        HttpRequestMessage? sent = null;
        var service = CreateService(request =>
        {
            sent = request;
            return Json(new { features = Array.Empty<object>() });
        });

        await service.FindAsync(Category("cinema"));

        Assert.Equal("entertainment.cinema", Query(sent!.RequestUri!)["categories"]);
        Assert.Equal(
            "I couldn't find a movie theater within about five miles of Hotel ZGY. The front desk may be able to suggest one.",
            Render("web", new ConciergeNearbyPlacesToolResult("NoResults", "cinema", [])));
    }

    [Fact]
    public void ModelSchemaConstrainsCategoryToTheKnownSet()
    {
        var schema = ConciergeToolDefinitions
            .FindModelTool(ConciergeToolDefinitions.FindNearbyPlaces, includeEndChat: false)
            .JsonSchema;
        var properties = schema.GetProperty("properties");

        Assert.Equal(
            NearbyPlaceCategories.Keys,
            properties.GetProperty("category").GetProperty("enum").EnumerateArray().Select(value => value.GetString()!));
        Assert.Equal(["category"], properties.EnumerateObject().Select(property => property.Name));
        Assert.Equal(["category"], schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()!));
        Assert.False(ConciergeToolCallPolicy.IsRideTool(ConciergeToolDefinitions.FindNearbyPlaces));
    }

    private static string Render(string channel, ConciergeNearbyPlacesToolResult result)
    {
        var interpretation = ConciergeToolResultInterpreter.Decode(
            ConciergeToolDefinitions.FindNearbyPlaces,
            JsonSerializer.Serialize(result, JsonOptions));
        Assert.True(interpretation.ShouldReturnRenderedToolAnswer);
        return interpretation.Interpret(
                new ConciergeTurnSubmission("nearby", "What is the nearest one?", channel),
                new Dictionary<string, object?> { ["category"] = result.Category },
                includeRideIdsForCancellation: false)
            .GuestFacingText;
    }

    private static NearbyPlaceCategory Category(string key) =>
        NearbyPlaceCategories.TryGet(key, out var category) ? category : throw new ArgumentException(key);

    private static GeoapifyNearbyPlacesService CreateService(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var hotelLocation = new HotelLocation();
        hotelLocation.Initialize(Hotel);
        return new GeoapifyNearbyPlacesService(
            new FixedHttpClientFactory(new HttpClient(new DelegateHandler(responder))),
            hotelLocation,
            Options.Create(new GeoapifyOptions { ApiKey = "test-places-key" }));
    }

    private static ConciergeActionToolService CreateTool(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        new(
            null!,
            null!,
            CreateService(responder),
            CreateOpeningHoursService(responder),
            null!,
            TimeProvider.System,
            Options.Create(new HotelLocationOptions { TimeZoneId = "America/New_York" }));

    private static GeoapifyPlaceOpeningHoursService CreateOpeningHoursService(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var hotelLocation = new HotelLocation();
        hotelLocation.Initialize(Hotel);
        return new GeoapifyPlaceOpeningHoursService(
            new FixedHttpClientFactory(new HttpClient(new DelegateHandler(responder))),
            hotelLocation,
            Options.Create(new GeoapifyOptions { ApiKey = "test-places-key" }));
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]), StringComparer.Ordinal);

    private static HttpResponseMessage Json<T>(T value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
