namespace ObdInsight.Core.Communication.Bluetooth;

/// <summary>
/// Information about a discovered BLE device.
/// </summary>
/// <param name="Name">Device display name</param>
/// <param name="Address">Device MAC address or identifier</param>
/// <param name="Rssi">Signal strength in dBm</param>
/// <param name="AdvertisedServices">Service UUIDs advertised by the device</param>
/// <param name="ManufacturerData">Optional manufacturer-specific data</param>
public record BleDeviceInfo(
    string Name,
    string Address,
    int Rssi,
    IReadOnlyList<Guid> AdvertisedServices,
    IReadOnlyDictionary<string, byte[]>? ManufacturerData = null
);

/// <summary>
/// Filter criteria for BLE device scanning.
/// </summary>
/// <param name="ServiceUuids">Only discover devices advertising these services</param>
/// <param name="DeviceNames">Only discover devices with names containing these strings</param>
/// <param name="DeviceAddresses">Only discover devices with these addresses</param>
/// <param name="MinRssi">Minimum signal strength to report</param>
public record BleScanFilter(
    IReadOnlyList<Guid>? ServiceUuids = null,
    IReadOnlyList<string>? DeviceNames = null,
    IReadOnlyList<string>? DeviceAddresses = null,
    int? MinRssi = null
);

/// <summary>
/// Event args for device discovery events.
/// </summary>
public class BleDeviceDiscoveredEventArgs : EventArgs
{
    /// <summary>
    /// Creates a new device discovery event
    /// </summary>
    public BleDeviceDiscoveredEventArgs(BleDeviceInfo device) => Device = device;

    /// <summary>
    /// The discovered device
    /// </summary>
    public BleDeviceInfo Device { get; }
}

/// <summary>
/// Event args for scan state change events.
/// </summary>
public class BleScanStateChangedEventArgs : EventArgs
{
    /// <summary>
    /// Creates a new scan state change event
    /// </summary>
    public BleScanStateChangedEventArgs(bool isScanning) => IsScanning = isScanning;

    /// <summary>
    /// Whether scanning is currently active
    /// </summary>
    public bool IsScanning { get; }
}
