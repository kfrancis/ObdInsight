namespace ObdInsight.DevTools;

/// <summary>
/// Configuration for connecting to a specific BLE OBD adapter.
/// Contains GATT service/characteristic UUIDs and connection parameters.
/// </summary>
public sealed class BleDeviceProfile
{
    public required Guid ServiceUuid { get; init; }
    public required Guid WriteCharacteristicUuid { get; init; }
    public required Guid NotifyCharacteristicUuid { get; init; }
    public bool WriteWithResponse { get; init; } = true;
    public int MaxWriteSize { get; init; } = 20;
    public string Name { get; init; } = "Unknown";
    public bool NotificationsRequired { get; init; } = true;

    /// <summary>
    /// Veepeak BLE OBD adapter (ASCII mode) - Nordic UART Service
    /// </summary>
    public static readonly BleDeviceProfile VeepeakBle = new()
    {
        Name = "Veepeak BLE (ASCII)",
        ServiceUuid = Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb"),
        WriteCharacteristicUuid = Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb"),
        NotifyCharacteristicUuid = Guid.Parse("0000fff2-0000-1000-8000-00805f9b34fb"),
        WriteWithResponse = false,
        MaxWriteSize = 20
    };

    /// <summary>
    /// Veepeak BLE OBD adapter (Binary protocol mode)
    /// </summary>
    public static readonly BleDeviceProfile VeepeakBinary = new()
    {
        Name = "Veepeak BLE (Binary)",
        ServiceUuid = Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb"),
        WriteCharacteristicUuid = Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb"),
        NotifyCharacteristicUuid = Guid.Parse("0000fff2-0000-1000-8000-00805f9b34fb"),
        WriteWithResponse = false,
        MaxWriteSize = 20
    };

    /// <summary>
    /// Generic Nordic UART Service profile (used by many BLE OBD adapters)
    /// </summary>
    public static readonly BleDeviceProfile NordicUart = new()
    {
        Name = "Nordic UART",
        ServiceUuid = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e"),
        WriteCharacteristicUuid = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e"),
        NotifyCharacteristicUuid = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e"),
        WriteWithResponse = false,
        MaxWriteSize = 20
    };

    /// <summary>
    /// Veepeak BLE Alt (alternate service UUID)
    /// </summary>
    public static readonly BleDeviceProfile VeepeakBleAlt = new()
    {
        Name = "Veepeak BLE Alt",
        ServiceUuid = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e"),
        WriteCharacteristicUuid = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e"),
        NotifyCharacteristicUuid = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e"),
        WriteWithResponse = false,
        MaxWriteSize = 20
    };

    /// <summary>
    /// OBDLink MX+ profile
    /// </summary>
    public static readonly BleDeviceProfile OBDLink = new()
    {
        Name = "OBDLink",
        // Full 128-bit form required — Guid.Parse("fff0") throws FormatException,
        // which as a static-field initializer would take down the whole profile
        // table with a TypeInitializationException on first touch.
        ServiceUuid = Guid.Parse("0000fff0-0000-1000-8000-00805f9b34fb"),
        WriteCharacteristicUuid = Guid.Parse("0000fff1-0000-1000-8000-00805f9b34fb"),
        NotifyCharacteristicUuid = Guid.Parse("0000fff2-0000-1000-8000-00805f9b34fb"),
        WriteWithResponse = true,
        MaxWriteSize = 20
    };

    /// <summary>
    /// OBDLink MX alias
    /// </summary>
    public static BleDeviceProfile ObdLinkMx => OBDLink;

    /// <summary>
    /// All known device profiles
    /// </summary>
    public static IReadOnlyList<BleDeviceProfile> AllProfiles { get; } = new[]
    {
        VeepeakBle,
        VeepeakBinary,
        VeepeakBleAlt,
        NordicUart,
        OBDLink
    };
}
