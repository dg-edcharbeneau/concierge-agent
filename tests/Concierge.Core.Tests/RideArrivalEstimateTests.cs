using System.Text.Json;
using Concierge.Contracts;

namespace Concierge.Core.Tests;

public sealed class RideArrivalEstimateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86_401)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void UnusableRouteDurationHasNoArrivalEstimate(double seconds)
    {
        Assert.Null(RideArrivalEstimate.Calculate(DateTimeOffset.UtcNow, seconds));
    }

    [Fact]
    public void DestinationArrivalUsesActualPickupAndCompletionWhenAvailable()
    {
        var pickup = DateTimeOffset.Parse("2026-10-25T17:57:00Z");
        var actualPickup = pickup.AddMinutes(1);
        var completed = actualPickup.AddSeconds(128.992);
        Assert.Equal(pickup.AddSeconds(128.992), RideArrivalEstimate.Calculate(pickup, 128.992));
        Assert.Equal(completed, RideArrivalEstimate.Calculate(pickup, 128.992, actualPickup));
        Assert.Equal(completed, RideArrivalEstimate.Calculate(pickup, double.NaN, actualPickup, completed));
    }

    [Fact]
    public void StreamEntryWithoutArrivalDoesNotInventOneFromPickupTime()
    {
        var entry = JsonSerializer.Deserialize<RideActivityEntry>(
            """{"RideId":"0af16850-18ec-7ae2-a15e-f60cbf6b3b30","Sequence":1,"Status":"Booked","ChangedAtUtc":"2026-10-25T17:47:10Z","PickupName":"Hotel ZGY","DropoffName":"Museum","PickupAtUtc":"2026-10-25T17:57:00Z"}""")!;
        Assert.Null(entry.ArrivalAtUtc);
    }
}
