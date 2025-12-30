using ObdInsight.Core.Vehicles;

namespace ObdInsight.Drivers;

/// <summary>
/// Registry of all vehicle profiles available in the Drivers package.
/// Use this to register additional profiles with a VehicleDetectorService.
/// </summary>
/// <remarks>
/// Vehicle profiles define:
/// - VIN matching for auto-detection
/// - Custom PIDs beyond standard OBD-II
/// - Response decoding logic
/// - Initialization commands for specific ECU protocols
///
/// Add new vehicle profiles by implementing <see cref="IVehicleProfile"/>
/// and registering them here.
/// </remarks>
public static class VehicleProfileRegistry
{
    /// <summary>
    /// Gets all vehicle profiles from this driver package.
    /// </summary>
    public static IEnumerable<IVehicleProfile> GetAllProfiles()
    {
        // Nissan
        yield return new Vehicles.NissanLeafProfile();

        // Chevrolet
        yield return new Vehicles.ChevroletBoltProfile();

        // Future profiles can be added here:
        // yield return new Vehicles.TeslaModel3Profile();
        // yield return new Vehicles.ToyotaPriusProfile();
        // yield return new Vehicles.FordMustangMachEProfile();
        // yield return new Vehicles.HyundaiIoniq6Profile();
    }

    /// <summary>
    /// Registers all profiles from this package with the given detector.
    /// </summary>
    /// <param name="detector">The detector to register profiles with</param>
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
    /// <param name="manufacturer">Manufacturer name to filter by</param>
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

    /// <summary>
    /// Gets profiles that match a given VIN.
    /// </summary>
    /// <param name="vin">The Vehicle Identification Number</param>
    public static IEnumerable<IVehicleProfile> GetProfilesForVin(string vin)
    {
        return GetAllProfiles().Where(p => p.MatchesVin(vin));
    }

    /// <summary>
    /// Gets all supported manufacturers.
    /// </summary>
    public static IEnumerable<string> GetSupportedManufacturers()
    {
        return GetAllProfiles()
            .Select(p => p.Manufacturer)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m);
    }
}