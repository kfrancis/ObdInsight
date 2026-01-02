using ObdInsight.Core.Vehicles;

namespace ObdInsight.Services;

/// <summary>
/// Holds vehicle identification and detected profile for the current app session.
/// </summary>
public sealed class VehicleSessionService
{
    private readonly object _lock = new();

    public string? Vin { get; private set; }

    public IVehicleProfile? Profile { get; private set; }

    public void SetVehicle(string? vin, IVehicleProfile? profile)
    {
        lock (_lock)
        {
            Vin = vin;
            Profile = profile;
        }
    }

    public void Clear()
    {
        SetVehicle(null, null);
    }
}
