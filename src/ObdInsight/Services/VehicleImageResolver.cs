using ObdInsight.Core.Vehicles;

namespace ObdInsight.Services;

/// <summary>
/// Resolves a display image for a vehicle profile.
/// Returns a placeholder when no specific image is available.
/// </summary>
public sealed class VehicleImageResolver
{
    public const string PlaceholderImage = "vehicle_placeholder.svg";

    public ImageSource Resolve(IVehicleProfile? profile)
    {
        if (profile is null)
            return PlaceholderImage;

        // First supported profile: Nissan Leaf
        if (profile.Manufacturer.Equals("Nissan", StringComparison.OrdinalIgnoreCase) &&
            profile.Model.Equals("Leaf", StringComparison.OrdinalIgnoreCase))
        {
            return "vehicle_nissan_leaf.svg";
        }

        return PlaceholderImage;
    }
}
