using Concierge.WorkflowServer.AI;

namespace Concierge.Core.Tests;

public sealed class OpeningHoursFormatterTests
{
    [Fact]
    public void LaterRulesOverrideTheDaysTheyName()
    {
        var web = Web("17:30-21:30; Th 17:30-22:00; Fr,Sa 17:30-22:30");

        Assert.Equal(
            [
                "Mon: 5:30 PM–9:30 PM", "Tue: 5:30 PM–9:30 PM", "Wed: 5:30 PM–9:30 PM",
                "Thu: 5:30 PM–10 PM", "Fri: 5:30 PM–10:30 PM", "Sat: 5:30 PM–10:30 PM",
                "Sun: 5:30 PM–9:30 PM",
            ],
            web);
    }

    [Fact]
    public void DayRangesListsAndUnnamedDaysAreClosed()
    {
        var web = Web("Mo-We,Fr 09:00-17:00; Sa 10:00-14:00");

        Assert.Equal(
            [
                "Mon: 9 AM–5 PM", "Tue: 9 AM–5 PM", "Wed: 9 AM–5 PM", "Thu: Closed",
                "Fri: 9 AM–5 PM", "Sat: 10 AM–2 PM", "Sun: Closed",
            ],
            web);
        Assert.Equal(
            "Cafe is open Monday through Wednesday and Friday from 9 AM to 5 PM, and Saturday from 10 AM to 2 PM. "
            + "It's closed Thursday and Sunday.",
            Voice("Mo-We,Fr 09:00-17:00; Sa 10:00-14:00"));
    }

    [Fact]
    public void WrappingDayRangeRunsThroughSunday()
    {
        var web = Web("Sa-Mo 08:00-12:00");

        Assert.Equal(
            ["Mon: 8 AM–12 PM", "Tue: Closed", "Wed: Closed", "Thu: Closed", "Fri: Closed", "Sat: 8 AM–12 PM", "Sun: 8 AM–12 PM"],
            web);
        Assert.Equal(
            "Cafe is open Monday, Saturday, and Sunday from 8 AM to 12 PM. It's closed Tuesday through Friday.",
            Voice("Sa-Mo 08:00-12:00"));
    }

    [Fact]
    public void MultipleIntervalsPerDayAreKeptInOrder()
    {
        Assert.Equal("Mon: 11 AM–2 PM, 5 PM–10 PM", Web("Mo-Fr 17:00-22:00,11:00-14:00")[0]);
        Assert.Equal(
            "Cafe is open Monday through Friday from 11 AM to 2 PM and from 5 PM to 10 PM. It's closed Saturday and Sunday.",
            Voice("Mo-Fr 11:00-14:00, 17:00-22:00"));
    }

    [Fact]
    public void OffAndClosedOverrideEarlierHours()
    {
        Assert.Equal(
            ["Mon: Closed", "Tue: Closed", "Wed: 7 AM–3 PM", "Thu: 7 AM–3 PM", "Fri: 7 AM–3 PM", "Sat: 7 AM–3 PM", "Sun: Closed"],
            Web("07:00-15:00; Mo,Tu off; Su closed"));
    }

    [Fact]
    public void AlwaysOpenIsRenderedAsTwentyFourHours()
    {
        Assert.All(Web("24/7"), line => Assert.EndsWith(": Open 24 hours", line, StringComparison.Ordinal));
        Assert.Equal("Cafe is open 24 hours a day, every day.", Voice("24/7"));
        Assert.Equal("Mon: Open 24 hours", Web("Mo-Su 00:00-24:00")[0]);
        Assert.Equal(
            "Cafe is open Monday through Saturday 24 hours. It's closed Sunday.",
            Voice("24/7; Su off"));
    }

    [Fact]
    public void OvernightHoursEndAfterMidnight()
    {
        Assert.Equal("Fri: 6 PM–2 AM", Web("Fr,Sa 18:00-02:00")[4]);
        Assert.Equal("Fri: 6 PM–12 AM", Web("Fr 18:00-24:00")[4]);
        Assert.Equal(
            "Cafe is open every day from 6 PM to 2 AM.",
            Voice("18:00-02:00"));
    }

    [Fact]
    public void WebFormatListsEveryDayWithTheZoneLabel()
    {
        Assert.True(OpeningHoursFormatter.TryParse("Mo-Fr 09:00-17:00", out var week));

        Assert.Equal(
            "Cafe's hours (Eastern Time):\n\n- **Mon:** 9 AM–5 PM\n- **Tue:** 9 AM–5 PM\n- **Wed:** 9 AM–5 PM\n"
            + "- **Thu:** 9 AM–5 PM\n- **Fri:** 9 AM–5 PM\n- **Sat:** Closed\n- **Sun:** Closed",
            OpeningHoursFormatter.FormatWeb("Cafe", week, "Eastern Time"));
    }

    [Theory]
    [InlineData("Mo-Fr 09:00-17:00; PH off")]
    [InlineData("Jan-Mar Mo-Fr 09:00-17:00")]
    [InlineData("Mo-Fr 09:00-17:00 || \"by appointment\"")]
    [InlineData("sunrise-sunset")]
    [InlineData("Mo-Fr 09:00-17:00 \"call ahead\"")]
    [InlineData("Mo-Fr 09:00+")]
    [InlineData("Mo-Fr 9:00-17:00")]
    [InlineData("Mo-Fr 09:00-17:00, Sa 10:00-14:00")]
    [InlineData("Mo-Fr 09:60-17:00")]
    [InlineData("Mo-Fr 09:00-25:00")]
    [InlineData("Mo-Fr 09:00-09:00")]
    [InlineData("Mo[1] 09:00-17:00")]
    [InlineData("week 1-26 Mo 09:00-17:00")]
    [InlineData("Mo-Fr")]
    [InlineData(" ; ")]
    [InlineData("")]
    public void UnsupportedSyntaxDoesNotParse(string value)
    {
        Assert.False(OpeningHoursFormatter.TryParse(value, out _));
    }

    [Fact]
    public void TimeZoneLabelUsesTheGenericNameOrOmitsIt()
    {
        var eastern = OpeningHoursFormatter.TimeZoneLabel(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));

        Assert.True(eastern is null or "Eastern Time", eastern);
        // A daylight-saving zone without a "Standard Time" name has no safe generic label.
        Assert.Null(OpeningHoursFormatter.TimeZoneLabel(TimeZoneInfo.FindSystemTimeZoneById("Europe/London")));
    }

    private static string[] Web(string value)
    {
        Assert.True(OpeningHoursFormatter.TryParse(value, out var week), value);
        return OpeningHoursFormatter.FormatWeb("Cafe", week, null)
            .Split('\n')
            .Skip(2)
            .Select(line => line.Replace("- **", string.Empty, StringComparison.Ordinal)
                .Replace(":**", ":", StringComparison.Ordinal))
            .ToArray();
    }

    private static string Voice(string value)
    {
        Assert.True(OpeningHoursFormatter.TryParse(value, out var week), value);
        return OpeningHoursFormatter.FormatVoice("Cafe", week);
    }
}
