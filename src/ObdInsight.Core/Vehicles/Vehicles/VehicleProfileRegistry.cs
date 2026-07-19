using ObdInsight.Core.Vehicles.Implementations;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf;

namespace ObdInsight.Core.Vehicles;

/// <summary>
/// Registry of available vehicle profiles.
/// Explicit registration (roadmap B12) — the previous reflection scan
/// (<c>Assembly.GetTypes()</c> + <c>Activator.CreateInstance</c>) was iOS trim/AOT
/// hostile and silently swallowed instantiation failures. New profiles are added to
/// <see cref="BuildDefaultProfiles"/> (one line), or injected at runtime via
/// <see cref="RegisterProfile"/> for out-of-assembly vehicles.
/// </summary>
public static class VehicleProfileRegistry
{
    private static readonly object s_gate = new();
    private static readonly List<IVehicleProfile> s_registered = [];
    private static readonly Lazy<IReadOnlyList<IVehicleProfile>> s_defaults =
        new(BuildDefaultProfiles);

    public static IReadOnlyList<IVehicleProfile> AllProfiles
    {
        get
        {
            lock (s_gate)
            {
                return s_registered.Count == 0
                    ? s_defaults.Value
                    : [.. s_defaults.Value, .. s_registered];
            }
        }
    }

    /// <summary>Adds a profile beyond the built-in set (e.g. from a plugin assembly).</summary>
    public static void RegisterProfile(IVehicleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (s_gate)
        {
            s_registered.Add(profile);
        }
    }

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

    private static IReadOnlyList<IVehicleProfile> BuildDefaultProfiles() =>
    [
        new NissanLeaf(),
        new HondaCrv(),
    ];
}
