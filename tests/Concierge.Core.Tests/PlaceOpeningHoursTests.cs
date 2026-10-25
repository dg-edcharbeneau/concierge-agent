using System.Net;
using System.Text;
using System.Text.Json;
using Concierge.Contracts;
using Concierge.WorkflowServer.AI;
using Concierge.WorkflowServer.Location;
using Microsoft.Extensions.Options;

namespace Concierge.Core.Tests;

public sealed class PlaceOpeningHoursTests
{
    private static readonly HotelCoordinates Hotel = new(25.7983, -80.1997);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task NamedSearchUsesProviderNameFilterAndHotelArea()
    {
        HttpRequestMessage? sent = null;
        var service = CreateService(request =>
        {
            sent = request;
            return Json(new { features = Array.Empty<object>() });
        });

        var result = await service.FindAsync("Wayan Miami");

        Assert.Equal(PlaceOpeningHoursStatus.NoResults, result.Status);
        Assert.NotNull(sent);
        Assert.Equal("https://api.geoapify.com/v2/places", sent.RequestUri!.GetLeftPart(UriPartial.Path));
        var query = Query(sent.RequestUri);
        Assert.Equal("Wayan Miami", query["name"]);
        Assert.Equal("circle:-80.1997,25.7983,60000", query["filter"]);
        Assert.Equal("proximity:-80.1997,25.7983", query["bias"]);
        Assert.Equal("en", query["lang"]);
        Assert.Contains("catering.restaurant", query["categories"], StringComparison.Ordinal);
        Assert.Equal("test-places-key", query["apiKey"]);
    }

    [Fact]
    public void ProviderHoursAreSelectedFromDuplicateRecordsAtOneSite()
    {
        // Geoapify can list one venue as an OSM node and its building way; both carry the
        // venue's name, house number, and street.
        using var document = JsonDocument.Parse(
            """
            {
              "features": [
                { "properties": { "name": "Wayan Miami", "place_id": "building",
                  "housenumber": "50", "street": "NW 24th Street",
                  "lat": 25.8000, "lon": -80.2000, "address_line1": "Wayan Miami" } },
                { "properties": { "name": "Wayan Miami", "place_id": "venue",
                  "housenumber": "50", "street": "Northwest 24th Street",
                  "lat": 25.8001, "lon": -80.2000, "opening_hours": "17:30-21:30; Th 17:30-22:00; Fr,Sa 17:30-22:30" } }
              ]
            }
            """);

        var result = GeoapifyPlaceOpeningHoursService.ReadPlaces(document.RootElement, "Wayan Miami", Hotel);

        Assert.Equal(PlaceOpeningHoursStatus.Available, result.Status);
        Assert.Equal("Wayan Miami", result.PlaceName);
        Assert.Equal("17:30-21:30; Th 17:30-22:00; Fr,Sa 17:30-22:30", result.OpeningHours);
        Assert.Empty(result.Candidates);
    }

    [Theory]
    // Two branches of one chain on neighbouring lots, ~35 m apart; only one lists hours.
    [InlineData("Starbucks", "1776", "Biscayne Boulevard")]
    // Same name, no address on one record: identity cannot be shown.
    [InlineData("Starbucks", null, null)]
    public void NeighbouringBranchesWithoutSharedAddressNeedClarification(
        string secondName, string? secondNumber, string? secondStreet)
    {
        var second = JsonSerializer.Serialize(new
        {
            properties = new
            {
                name = secondName,
                place_id = "branch-b",
                housenumber = secondNumber,
                street = secondStreet,
                lat = 25.79310,
                lon = -80.18910,
            },
        });
        using var document = JsonDocument.Parse(
            $$"""
            { "features": [
              { "properties": { "name": "Starbucks", "place_id": "branch-a",
                "housenumber": "1750", "street": "Biscayne Boulevard",
                "lat": 25.79280, "lon": -80.18920, "opening_hours": "Mo-Su 06:30-20:00" } },
              {{second}}
            ] }
            """);

        var result = GeoapifyPlaceOpeningHoursService.ReadPlaces(document.RootElement, "Starbucks", Hotel);

        Assert.Equal(PlaceOpeningHoursStatus.NeedsClarification, result.Status);
        Assert.Null(result.OpeningHours);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains("Starbucks, 1750 Biscayne Boulevard", result.Candidates);
    }

    [Fact]
    public void MissingHoursAndMultipleSitesHaveDistinctStatuses()
    {
        using var missing = JsonDocument.Parse(
            """
            { "features": [
              { "properties": { "name": "Some Cafe", "place_id": "cafe", "lat": 25.8, "lon": -80.2 } }
            ] }
            """);
        using var ambiguous = JsonDocument.Parse(
            """
            { "features": [
              { "properties": { "name": "Some Cafe", "place_id": "cafe-1", "lat": 25.8, "lon": -80.2 } },
              { "properties": { "name": "Some Cafe", "place_id": "cafe-2", "lat": 25.81, "lon": -80.2 } }
            ] }
            """);

        var unlisted = GeoapifyPlaceOpeningHoursService.ReadPlaces(missing.RootElement, "Some Cafe", Hotel);
        var needsClarification = GeoapifyPlaceOpeningHoursService.ReadPlaces(ambiguous.RootElement, "Some Cafe", Hotel);

        Assert.Equal(PlaceOpeningHoursStatus.NotListed, unlisted.Status);
        Assert.Null(unlisted.OpeningHours);
        Assert.Equal(PlaceOpeningHoursStatus.NeedsClarification, needsClarification.Status);
        Assert.Equal(2, needsClarification.Candidates.Count);
    }

    [Fact]
    public void NamedPlaceOutsideTheHotelSearchAreaIsNotReturned()
    {
        using var document = JsonDocument.Parse(
            """
            { "features": [
              { "properties": { "name": "Wayan Miami", "place_id": "far",
                "lat": 26.5, "lon": -80.2, "opening_hours": "17:30-21:30" } }
            ] }
            """);

        var result = GeoapifyPlaceOpeningHoursService.ReadPlaces(document.RootElement, "Wayan Miami", Hotel);

        Assert.Equal(PlaceOpeningHoursStatus.NoResults, result.Status);
        Assert.Null(result.PlaceName);
    }

    private const string WayanHours = "17:30-21:30; Th 17:30-22:00; Fr,Sa 17:30-22:30";

    [Fact]
    public void OpeningHoursToolRendersWebHoursAsADailyListAndExplicitlyReportsUnlistedHours()
    {
        var hours = Render(new ConciergePlaceOpeningHoursToolResult(
            "Available", "Wayan Miami", WayanHours, [], "Eastern Time"));
        var unlisted = Render(new ConciergePlaceOpeningHoursToolResult(
            "NotListed", "Some Cafe", null, []));

        Assert.Equal(
            "Wayan Miami's hours (Eastern Time):\n\n"
            + "- **Mon:** 5:30 PM–9:30 PM\n"
            + "- **Tue:** 5:30 PM–9:30 PM\n"
            + "- **Wed:** 5:30 PM–9:30 PM\n"
            + "- **Thu:** 5:30 PM–10 PM\n"
            + "- **Fri:** 5:30 PM–10:30 PM\n"
            + "- **Sat:** 5:30 PM–10:30 PM\n"
            + "- **Sun:** 5:30 PM–9:30 PM",
            hours);
        Assert.Equal("Opening hours for Some Cafe are not listed.", unlisted);
        Assert.DoesNotContain("closest", unlisted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("options", unlisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpeningHoursToolRendersVoiceHoursAsPlainGroupedSentences()
    {
        var hours = Render(
            new ConciergePlaceOpeningHoursToolResult("Available", "Wayan Miami", WayanHours, [], "Eastern Time"),
            "voice");

        Assert.Equal(
            "Wayan Miami is open Monday through Wednesday and Sunday from 5:30 PM to 9:30 PM, "
            + "Thursday until 10 PM, and Friday and Saturday until 10:30 PM.",
            hours);
        Assert.DoesNotContain("*", hours, StringComparison.Ordinal);
        Assert.DoesNotContain("- ", hours, StringComparison.Ordinal);
    }

    [Fact]
    public void WebHoursEscapeThePlaceNameAndOmitAMissingTimeZoneLabel()
    {
        var hours = Render(new ConciergePlaceOpeningHoursToolResult(
            "Available", "Joe's *Stone* Crab", "Mo-Su 11:00-22:00", []));

        Assert.StartsWith("Joe's \\*Stone\\* Crab's hours:\n\n- **Mon:** 11 AM–10 PM", hours);
    }

    [Theory]
    [InlineData("web")]
    [InlineData("voice")]
    public void UnsupportedOpeningHoursSyntaxFallsBackToTheProviderValue(string channel)
    {
        var hours = Render(
            new ConciergePlaceOpeningHoursToolResult(
                "Available", "Wayan Miami", "Mo-Fr 17:00-22:00; PH off", [], "Eastern Time"),
            channel);

        Assert.Equal("Wayan Miami's listed opening hours are Mo-Fr 17:00-22:00; PH off.", hours);
    }

    private static string Render(ConciergePlaceOpeningHoursToolResult result, string channel = "web")
    {
        var interpretation = ConciergeToolResultInterpreter.Decode(
            ConciergeToolDefinitions.GetPlaceOpeningHours,
            JsonSerializer.Serialize(result, JsonOptions));
        Assert.True(interpretation.ShouldReturnRenderedToolAnswer);
        return interpretation.Interpret(
                new ConciergeTurnSubmission("hours", "What are the hours?", channel),
                new Dictionary<string, object?> { ["placeName"] = result.PlaceName ?? "Wayan Miami" },
                includeRideIdsForCancellation: false)
            .GuestFacingText;
    }

    private static GeoapifyPlaceOpeningHoursService CreateService(
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
