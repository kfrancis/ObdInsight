using System.Reflection;
using ObdTestApp.Core.Vehicles.Implementations;

namespace ObdTestApp.Core.Vehicles;

/// <summary>
/// Registry for discovering and managing available vehicle profiles.
/// </summary>
public static class VehicleProfileRegistry
{
    private static readonly Lazy<IReadOnlyList<IVehicleProfile>> s_profiles = 
        new(DiscoverProfiles);

    public static IReadOnlyList<IVehicleProfile> AllProfiles => s_profiles.Value;

    /// <summary>
    /// Gets a list of unique vehicle makes and models.
    /// </summary>
    public static IReadOnlyList<(string Make, string Model)> GetAvailableVehicles()
    {
        return AllProfiles
            .Select(p => (p.Make, p.Model))
            .Distinct()
            .OrderBy(x => x.Make)
            .ThenBy(x => x.Model)
            .ToList();
    }

    /// <summary>
    /// Finds a vehicle profile by make and model.
    /// </summary>
    public static IVehicleProfile? FindProfile(string make, string model)
    {
        return AllProfiles.FirstOrDefault(p => 
            p.Make.Equals(make, StringComparison.OrdinalIgnoreCase) &&
            p.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets all variants for a specific vehicle.
    /// </summary>
    public static IReadOnlyList<VehicleVariant>? GetVariants(string make, string model)
    {
        return FindProfile(make, model)?.Variants;
    }

    private static IReadOnlyList<IVehicleProfile> DiscoverProfiles()
    {
        var profiles = new List<IVehicleProfile>();

        try
        {
            // Get the assembly containing vehicle implementations
            var assembly = typeof(HondaCrv).Assembly;

            // Find all types that implement IVehicleProfile (but not abstract base classes)
            var profileTypes = assembly.GetTypes()
                .Where(t => 
                    !t.IsAbstract && 
                    !t.IsInterface && 
                    typeof(IVehicleProfile).IsAssignableFrom(t) &&
                    t.GetConstructor(Type.EmptyTypes) != null) // Has parameterless constructor
                .ToList();

            // Instantiate each profile
            foreach (var type in profileTypes)
            {
                try
                {
                    if (Activator.CreateInstance(type) is IVehicleProfile profile)
                    {
                        profiles.Add(profile);
                    }
                }
                catch (Exception ex)
                {
                    // Log or handle instantiation errors
                    System.Diagnostics.Debug.WriteLine($"Failed to instantiate {type.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error discovering vehicle profiles: {ex.Message}");
        }

        return profiles.OrderBy(p => p.Make).ThenBy(p => p.Model).ToList();
    }
}
