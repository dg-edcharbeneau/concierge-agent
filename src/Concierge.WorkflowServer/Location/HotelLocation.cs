namespace Concierge.WorkflowServer.Location;

public sealed record HotelCoordinates(double Latitude, double Longitude);

/// <summary>The address-derived location, published once before workers start.</summary>
public sealed class HotelLocation
{
    private HotelCoordinates? coordinates;

    public HotelCoordinates Coordinates => Volatile.Read(ref coordinates)
        ?? throw new InvalidOperationException(
            "Hotel location is not initialized. Resolve Hotel:Address before starting workers.");

    public void Initialize(HotelCoordinates value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!double.IsFinite(value.Latitude) || value.Latitude is < -90 or > 90
            || !double.IsFinite(value.Longitude) || value.Longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Hotel coordinates must be finite and within geographic bounds.");
        }

        if (Interlocked.CompareExchange(ref coordinates, value, null) is not null)
        {
            throw new InvalidOperationException("Hotel location has already been initialized.");
        }
    }
}
