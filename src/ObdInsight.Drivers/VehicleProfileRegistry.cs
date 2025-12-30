using ObdInsight.Core.Vehicles;

namespace ObdInsight.Drivers;

/// <summary>
/// Registry of all vehicle profiles available in the Drivers package.
/// Use this to register additional profiles with a VehicleDetectorService.
/// </summary>
public static class VehicleProfileRegistry
{
    /// <summary>
    /// Gets all vehicle profiles from this driver package.
    /// </summary>
    public static IEnumerable<IVehicleProfile> GetAllProfiles()
    {
        // Chevrolet
        yield return new Vehicles.ChevroletBoltProfile();

        // Future profiles can be added here:
        // yield return new Vehicles.TeslaModel3Profile();
        // yield return new Vehicles.ToyotaPriusProfile();
        // yield return new Vehicles.FordMustangMachEProfile();
    }

    /// <summary>
    /// Registers all profiles from this package with the given detector.
    /// </summary>
    public static void RegisterAllProfiles(IVehicleDetector detector)
    {
        ArgumentNullException.ThrowIfNull(detector);

        foreach (var profile in GetAllProfiles())
        {
            detector.RegisterProfile(profile);
        }
    }

    /// <summary>
    /// Gets profiles by manufacturer.
    /// </summary>
    public static IEnumerable<IVehicleProfile> GetProfilesByManufacturer(string manufacturer)
    {
        return GetAllProfiles()
            .Where(p => p.Manufacturer.Equals(manufacturer, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets profiles that support electric vehicles.
    /// </summary>
    public static IEnumerable<IVehicleProfile> GetEvProfiles()
    {
        return GetAllProfiles().Where(p => p.IsElectric);
    }
}
