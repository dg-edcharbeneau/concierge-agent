namespace Concierge.Contracts;

/// <summary>Destination arrival, using the booked route duration rather than a driver arrival estimate.</summary>
public static class RideArrivalEstimate
{
    public static DateTimeOffset? Calculate(
        DateTimeOffset pickupAt,
        double routeDurationSeconds,
        DateTimeOffset? pickedUpAtUtc = null,
        DateTimeOffset? completedAtUtc = null)
    {
        if (completedAtUtc is { } completed)
        {
            return completed;
        }

        if (!double.IsFinite(routeDurationSeconds) || routeDurationSeconds is <= 0 or > 86_400)
        {
            return null;
        }

        return (pickedUpAtUtc ?? pickupAt).AddSeconds(routeDurationSeconds);
    }
}
