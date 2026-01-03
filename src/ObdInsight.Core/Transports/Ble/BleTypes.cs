namespace ObdInsight.Core.Transports.Ble;

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
/// Profile for a BLE OBD adapter, defining the service and characteristic UUIDs.
/// </summary>
/// <remarks>
/// Different BLE OBD adapters use different GATT services and characteristics.
/// This profile tells the transport how to communicate with a specific adapter type.
/// </remarks>
/// <param name="Name">Profile display name</param>
/// <param name="ServiceUuid">GATT service UUID for OBD communication</param>
/// <param name="WriteCharacteristicUuid">UUID of the characteristic to write commands to</param>
/// <param name="NotifyCharacteristicUuid">UUID of the characteristic to receive responses from</param>
/// <param name="WriteWithResponse">Whether to use write-with-response (slower but more reliable)</param>
/// <param name="MaxWriteSize">Maximum bytes per write (BLE MTU consideration)</param>
/// <param name="NotificationsRequired">If true, connection fails when CCCD notification subscription fails</param>
public record BleDeviceProfile(
    string Name,
    Guid ServiceUuid,
    Guid WriteCharacteristicUuid,
    Guid NotifyCharacteristicUuid,
    bool WriteWithResponse = false,
    int MaxWriteSize = 20,
    bool NotificationsRequired = true
)
{
    /// <summary>
    /// Veepeak OBDCheck BLE+ adapter.
    /// Service: 0000FFF0, Write: 0000FFF2, Notify: 0000FFF1
    /// </summary>
    public static BleDeviceProfile VeepeakBle => new(
        Name: "Veepeak BLE+",
        ServiceUuid: Guid.Parse("0000FFF0-0000-1000-8000-00805F9B34FB"),
        WriteCharacteristicUuid: Guid.Parse("0000FFF2-0000-1000-8000-00805F9B34FB"),
        NotifyCharacteristicUuid: Guid.Parse("0000FFF1-0000-1000-8000-00805F9B34FB"),
        WriteWithResponse: true,  // More reliable for ELM327 clones
        MaxWriteSize: 20,
        NotificationsRequired: true  // OBD adapters need notifications for RX
    );

    /// <summary>
    /// Alternative Veepeak profile using the secondary service (0000FFE0).
    /// Some Veepeak variants may use this instead.
    /// </summary>
    public static BleDeviceProfile VeepeakBleAlt => new(
        Name: "Veepeak BLE+ (Alt)",
        ServiceUuid: Guid.Parse("0000FFE0-0000-1000-8000-00805F9B34FB"),
        WriteCharacteristicUuid: Guid.Parse("0000FFE1-0000-1000-8000-00805F9B34FB"),
        NotifyCharacteristicUuid: Guid.Parse("0000FFE1-0000-1000-8000-00805F9B34FB"),
        WriteWithResponse: false,
        MaxWriteSize: 20,
        NotificationsRequired: true
    );

    /// <summary>
    /// Veepeak binary framing profile for higher performance.
    /// Uses proprietary binary protocol instead of ASCII ELM327 commands.
    /// Service: 6287, WriteNoResp: 6387, Notify: 6487
    /// </summary>
    /// <remarks>
    /// Binary framing bypasses ELM327 ASCII parsing overhead and can provide
    /// faster response times. However, it requires a different command format
    /// than standard AT/OBD-II ASCII commands.
    /// </remarks>
    public static BleDeviceProfile VeepeakBinary => new(
        Name: "Veepeak Binary",
        ServiceUuid: Guid.Parse("00006287-3c17-d293-8e48-14fe2e4da212"),
        WriteCharacteristicUuid: Guid.Parse("00006387-3c17-d293-8e48-14fe2e4da212"),
        NotifyCharacteristicUuid: Guid.Parse("00006487-3c17-d293-8e48-14fe2e4da212"),
        WriteWithResponse: false,  // WriteNoResp for speed
        MaxWriteSize: 20,
        NotificationsRequired: true
    );

    /// <summary>
    /// Nordic UART Service profile (used by some OBD adapters).
    /// </summary>
    public static BleDeviceProfile NordicUart => new(
        Name: "Nordic UART",
        ServiceUuid: Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E"),
        WriteCharacteristicUuid: Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E"),
        NotifyCharacteristicUuid: Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E"),
        WriteWithResponse: false,
        MaxWriteSize: 20,
        NotificationsRequired: true
    );

    /// <summary>
    /// OBDLink MX+ adapter profile.
    /// </summary>
    public static BleDeviceProfile ObdLinkMx => new(
        Name: "OBDLink MX+",
        ServiceUuid: Guid.Parse("0000FFF0-0000-1000-8000-00805F9B34FB"),
        WriteCharacteristicUuid: Guid.Parse("0000FFF2-0000-1000-8000-00805F9B34FB"),
        NotifyCharacteristicUuid: Guid.Parse("0000FFF1-0000-1000-8000-00805F9B34FB"),
        WriteWithResponse: true,  // OBDLink prefers write-with-response
        MaxWriteSize: 20,
        NotificationsRequired: true
    );

    /// <summary>
    /// All known BLE device profiles for auto-detection.
    /// </summary>
    public static IReadOnlyList<BleDeviceProfile> AllProfiles =>
    [
        VeepeakBinary,  // Try binary first for better performance
        VeepeakBle,
        VeepeakBleAlt,
        ObdLinkMx,
        NordicUart
    ];

    /// <summary>
    /// Try to find a matching profile by profile name.
    /// </summary>
    public static BleDeviceProfile? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return AllProfiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Try to find a matching profile by service UUID.
    /// </summary>
    public static BleDeviceProfile? FindByServiceUuid(Guid serviceUuid)
    {
        return AllProfiles.FirstOrDefault(p => p.ServiceUuid == serviceUuid);
    }
}

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