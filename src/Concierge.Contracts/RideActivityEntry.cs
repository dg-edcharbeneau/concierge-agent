namespace Concierge.Contracts;

public static class RideActivityFeed
{
    public const int MaxRetainedEntries = 100;
    public const string Topic = "rides";
}

/// <summary>A ride status notification published to the owning stay's activity stream.</summary>
public sealed record RideActivityEntry(
    Guid RideId,
    int Sequence,
    string Status,
    DateTimeOffset ChangedAtUtc,
    string PickupName,
    string DropoffName,
    DateTimeOffset PickupAtUtc,
    string? DriverName = null,
    string? Car = null,
    string? Plate = null,
    DateTimeOffset? ArrivalAtUtc = null);

/// <summary>Signal payload used to retain only the newest ride activity stream entries.</summary>
public sealed record TruncateRideActivityStreamRequest(long BeforeOffset);

public static class ConciergeStayWorkflowIds
{
    public const string Prefix = "concierge-stay-";
    public const string TruncateRideActivityStreamSignal = "truncate-ride-activity-stream";

    public static string ForStay(Guid stayId) => $"{Prefix}{stayId:N}";
}
