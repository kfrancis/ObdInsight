namespace ObdInsight.Transports.Ble;

/// <summary>
/// A known BLE OBD adapter GATT profile. UUIDs are stored in full 128-bit form;
/// matching handles 16-bit short-form equivalence (see <see cref="BleUuid"/>).
/// </summary>
public sealed record BleAdapterProfile
{
    public required string Name { get; init; }
    public required Guid ServiceUuid { get; init; }
    public required Guid WriteCharacteristicUuid { get; init; }
    public required Guid NotifyCharacteristicUuid { get; init; }

    /// <summary>Write-with-response vs write-without-response (most clones want without).</summary>
    public bool WriteWithResponse { get; init; }

    /// <summary>Max bytes per BLE write (default 20 — the classic ATT MTU payload).</summary>
    public int MaxWriteSize { get; init; } = 20;

    /// <summary>
    /// Vgate iCar Pro (EvTestDrive reference adapter): FFE0 service, single FFE1
    /// characteristic carrying both write and notify.
    /// </summary>
    public static readonly BleAdapterProfile VgateICarPro = new()
    {
        Name = "Vgate iCar Pro (FFE0/FFE1)",
        ServiceUuid = BleUuid.FromShortId(0xFFE0),
        WriteCharacteristicUuid = BleUuid.FromShortId(0xFFE1),
        NotifyCharacteristicUuid = BleUuid.FromShortId(0xFFE1),
        WriteWithResponse = false,
    };

    /// <summary>Veepeak-style FFF0 service: FFF1 write, FFF2 notify (hardware-proven Windows path).</summary>
    public static readonly BleAdapterProfile VeepeakFff0 = new()
    {
        Name = "Veepeak (FFF0/FFF1/FFF2)",
        ServiceUuid = BleUuid.FromShortId(0xFFF0),
        WriteCharacteristicUuid = BleUuid.FromShortId(0xFFF1),
        NotifyCharacteristicUuid = BleUuid.FromShortId(0xFFF2),
        WriteWithResponse = false,
    };

    /// <summary>Nordic UART Service (many generic adapters): RX = write, TX = notify.</summary>
    public static readonly BleAdapterProfile NordicUart = new()
    {
        Name = "Nordic UART",
        ServiceUuid = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e"),
        WriteCharacteristicUuid = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e"),
        NotifyCharacteristicUuid = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e"),
        WriteWithResponse = false,
    };

    /// <summary>Auto-probe priority order (reference adapter first).</summary>
    public static IReadOnlyList<BleAdapterProfile> KnownProfiles { get; } =
        [VgateICarPro, VeepeakFff0, NordicUart];
}

/// <summary>Bluetooth base-UUID helpers for 16-bit short-form equivalence.</summary>
public static class BleUuid
{
    public static Guid FromShortId(ushort shortId) =>
        Guid.Parse($"0000{shortId:x4}-0000-1000-8000-00805f9b34fb");

    /// <summary>True when the GUID is a Bluetooth-base short-form expansion.</summary>
    public static bool TryGetShortId(Guid uuid, out ushort shortId)
    {
        var bytes = uuid.ToString("N");
        // 0000xxxx0000100080005f9b34fb? Compare via canonical string of the base form.
        if (bytes.Length == 32 &&
            bytes.StartsWith("0000", StringComparison.OrdinalIgnoreCase) &&
            bytes.EndsWith("00001000800000805f9b34fb", StringComparison.OrdinalIgnoreCase))
        {
            shortId = Convert.ToUInt16(bytes.Substring(4, 4), 16);
            return true;
        }

        shortId = 0;
        return false;
    }

    /// <summary>Equality that treats short-form and 128-bit expansions as the same UUID.</summary>
    public static bool Matches(Guid a, Guid b)
    {
        if (a == b)
        {
            return true;
        }

        return TryGetShortId(a, out var sa) && TryGetShortId(b, out var sb) && sa == sb;
    }
}
