using Concierge.Contracts;

namespace Concierge.RideService;

public sealed record RidePlaceRequest(string Name, double Latitude, double Longitude);

public sealed record RideBookingRequest(
    Guid GuestId,
    Guid StayId,
    DateOnly CheckoutDate,
    RidePlaceRequest Pickup,
    RidePlaceRequest Dropoff,
    bool Immediate,
    DateTimeOffset? PickupAt,
    string IdempotencyKey,
    double RouteDurationSeconds = 1800);

public sealed record RideBookingCommand(
    Guid Id,
    string IdempotencyKey,
    Guid GuestId,
    Guid StayId,
    DateOnly CheckoutDate,
    string PickupName,
    double PickupLatitude,
    double PickupLongitude,
    string DropoffName,
    double DropoffLatitude,
    double DropoffLongitude,
    bool Immediate,
    DateTimeOffset PickupAtUtc,
    DateTimeOffset BookedAtUtc,
    double RouteDurationSeconds = 1800,
    string StayWorkflowId = "");

public sealed record RideResponse(
    Guid Id,
    Guid GuestId,
    Guid StayId,
    string PickupName,
    double PickupLatitude,
    double PickupLongitude,
    string DropoffName,
    double DropoffLatitude,
    double DropoffLongitude,
    bool Immediate,
    DateTimeOffset PickupAt,
    DateTimeOffset BookedAt,
    string Status,
    double RouteDurationSeconds = 1800,
    string StayWorkflowId = "",
    string? DriverName = null,
    string? Car = null,
    string? Plate = null,
    DateTimeOffset? PickedUpAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null,
    DateTimeOffset? CancelledAtUtc = null);

public sealed record RideApiError(string Code, string Message);

public sealed record RideBookingIntent(
    Guid GuestId,
    Guid StayId,
    DateOnly CheckoutDate,
    string PickupName,
    double PickupLatitude,
    double PickupLongitude,
    string DropoffName,
    double DropoffLatitude,
    double DropoffLongitude,
    bool Immediate,
    DateTimeOffset? PickupAtUtc,
    double RouteDurationSeconds = 1800);

public sealed record RideTransitionResult(
    bool Applied,
    string CurrentStatus,
    RideActivityEntry? Entry);

public sealed record RideTransitionCommand(
    Guid RideId,
    string ExpectedStatus,
    string TargetStatus,
    int Sequence,
    DateTimeOffset ChangedAtUtc,
    string? DriverName = null,
    string? Car = null,
    string? Plate = null);

public sealed record RideCancellationResult(
    bool Applied,
    string CurrentStatus,
    RideActivityEntry? Entry);

public static class RideWorkflowNames
{
    public const string TaskQueue = "concierge-rides";
    public const string BookingWorkflow = "ride-booking";
    public const string AdmitBookingUpdate = "admit-booking";
    public const string CancelRideUpdate = "cancel-ride";
    public const string StoreBookingActivity = "store-booking";
    public const string ReconcileBookingActivity = "reconcile-booking";
    public const string TransitionRideActivity = "transition-ride";
    public const string CancelRideActivity = "cancel-ride";
    public const string PublishRideEntryActivity = "publish-ride-entry";
}

public sealed class RideApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}
