using ObdInsight.Core.Adapters;
using ObdInsight.Core.Adapters.Elm327;
using ObdInsight.Core.Transports.Ble;

namespace ObdInsight.Drivers.Adapters;

/// <summary>
/// Registry of all OBD adapters available in the Drivers package.
/// Provides factory methods for creating adapter instances and matching devices.
/// </summary>
/// <remarks>
/// The adapter registry serves two purposes:
/// 1. Discovery: Find a suitable adapter for a given device name
/// 2. Factory: Create adapter instances for communication
///
/// This separation allows adding new adapter support (e.g., STN1110, OBDLink)
/// without modifying existing code.
/// </remarks>
public static class AdapterRegistry
{
    /// <summary>
    /// Gets all registered adapter types.
    /// </summary>
    public static IEnumerable<AdapterInfo> GetAllAdapters()
    {
        yield return new AdapterInfo(
            Name: "ELM327",
            Description: "ELM327 and compatible OBD interpreters (most common)",
            SupportedDeviceNames: ["OBDII", "Veepeak", "ELM327", "OBDLink", "V-LINK", "OBD"],
            Factory: () => new Elm327Adapter()
        );

        // Future adapters can be added here:
        // yield return new AdapterInfo(
        //     Name: "STN1110",
        //     Description: "STN1110 high-performance adapter",
        //     SupportedDeviceNames: ["STN1110", "OBDLink"],
        //     Factory: () => new Stn1110Adapter()
        // );
    }

    /// <summary>
    /// Creates an adapter instance by name.
    /// </summary>
    /// <param name="name">The adapter name (e.g., "ELM327")</param>
    /// <returns>A new adapter instance, or null if not found</returns>
    public static IObdAdapter? CreateAdapter(string name)
    {
        var info = GetAllAdapters()
            .FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return info?.Factory();
    }

    /// <summary>
    /// Finds an adapter that supports the given BLE device.
    /// </summary>
    /// <param name="device">The discovered BLE device</param>
    /// <returns>Adapter info if a match is found</returns>
    public static AdapterInfo? FindAdapterForDevice(BleDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        return GetAllAdapters()
            .FirstOrDefault(a => a.SupportedDeviceNames
                .Any(name => device.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Finds an adapter that supports the given device name.
    /// </summary>
    /// <param name="deviceName">The device name to match</param>
    /// <returns>Adapter info if a match is found</returns>
    public static AdapterInfo? FindAdapterForDeviceName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return null;

        return GetAllAdapters()
            .FirstOrDefault(a => a.SupportedDeviceNames
                .Any(name => deviceName.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Creates an adapter for the given BLE device, auto-detecting the type.
    /// </summary>
    /// <param name="device">The BLE device to connect to</param>
    /// <returns>A new adapter instance, or a default ELM327 if no match</returns>
    public static IObdAdapter CreateAdapterForDevice(BleDeviceInfo device)
    {
        var info = FindAdapterForDevice(device);
        return info?.Factory() ?? new Elm327Adapter();
    }

    /// <summary>
    /// Gets the default adapter (ELM327).
    /// </summary>
    public static IObdAdapter CreateDefaultAdapter() => new Elm327Adapter();
}

/// <summary>
/// Information about a registered OBD adapter.
/// </summary>
/// <param name="Name">Display name</param>
/// <param name="Description">Human-readable description</param>
/// <param name="SupportedDeviceNames">Device names this adapter handles</param>
/// <param name="Factory">Factory function to create instances</param>
public record AdapterInfo(
    string Name,
    string Description,
    IReadOnlyList<string> SupportedDeviceNames,
    Func<IObdAdapter> Factory
);