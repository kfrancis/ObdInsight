namespace ObdInsight.Transports.Ble;

/// <summary>One discovered GATT characteristic, reduced to what profile matching needs.</summary>
public sealed record GattCharacteristicInfo(Guid Uuid, bool CanWrite, bool CanNotify);

/// <summary>One discovered GATT service and its characteristics.</summary>
public sealed record GattServiceInfo(Guid Uuid, IReadOnlyList<GattCharacteristicInfo> Characteristics);

/// <summary>
///     A concrete, connectable resolution: the actual UUIDs to use on this device
///     (may differ from the source profile when a fallback rule fired).
/// </summary>
public sealed record ResolvedBleProfile(
    string Name,
    Guid ServiceUuid,
    Guid WriteCharacteristicUuid,
    Guid NotifyCharacteristicUuid,
    bool WriteWithResponse,
    int MaxWriteSize);

/// <summary>
///     Pure profile auto-probe (docs/BLE_TRANSPORT_DESIGN.md §3): discovered GATT topology
///     in, best usable profile out. No BLE dependencies — unit-tested exhaustively;
///     the thin Plugin.BLE wrapper feeds it real discovery data.
/// </summary>
public static class BleProfileResolver
{
    public static ResolvedBleProfile? Resolve(IReadOnlyList<GattServiceInfo> services, string? deviceName = null) =>
        Resolve(services, BleAdapterProfile.KnownProfiles, deviceName);

    public static ResolvedBleProfile? Resolve(
        IReadOnlyList<GattServiceInfo> services,
        IReadOnlyList<BleAdapterProfile> knownProfiles,
        string? deviceName = null)
    {
        // Rule 1: exact known-profile match, in table priority order.
        foreach (var profile in knownProfiles)
        {
            var service = FindService(services, profile.ServiceUuid);
            if (service is null)
            {
                continue;
            }

            var write = FindCharacteristic(service, profile.WriteCharacteristicUuid, c => c.CanWrite);
            var notify = FindCharacteristic(service, profile.NotifyCharacteristicUuid, c => c.CanNotify);
            if (write is not null && notify is not null)
            {
                return new ResolvedBleProfile(
                    profile.Name, service.Uuid, write.Uuid, notify.Uuid,
                    profile.WriteWithResponse, profile.MaxWriteSize);
            }

            // Rule 2: known service, but the expected characteristic split isn't there —
            // fall back to a single write+notify characteristic within it (clone variance).
            var dual = service.Characteristics.FirstOrDefault(c => c.CanWrite && c.CanNotify);
            if (dual is not null)
            {
                return new ResolvedBleProfile(
                    $"{profile.Name} (single-characteristic fallback)",
                    service.Uuid, dual.Uuid, dual.Uuid,
                    profile.WriteWithResponse, profile.MaxWriteSize);
            }

            // Rule 2b: known service, but write/notify sit on characteristic UUIDs the
            // firmware batch changed (e.g. FFF3/FFF4 instead of FFF1/FFF2) — any usable
            // write+notify pair inside a *recognized* service still deserves a named
            // profile, not the generic fallback below.
            var anyWrite = service.Characteristics.FirstOrDefault(c => c.CanWrite);
            var anyNotify = service.Characteristics.FirstOrDefault(c => c.CanNotify);
            if (anyWrite is not null && anyNotify is not null)
            {
                return new ResolvedBleProfile(
                    $"{profile.Name} (characteristic fallback)",
                    service.Uuid, anyWrite.Uuid, anyNotify.Uuid,
                    profile.WriteWithResponse, profile.MaxWriteSize);
            }
        }

        // Rule 3: generic fallback — any service carrying a usable (write, notify) pair
        // or a dual-role characteristic. Last resort for unknown clones.
        foreach (var service in services)
        {
            var write = service.Characteristics.FirstOrDefault(c => c.CanWrite);
            var notify = service.Characteristics.FirstOrDefault(c => c.CanNotify);
            if (write is not null && notify is not null)
            {
                var label = NameHint(deviceName) is { } hint
                    ? $"{hint} (write/notify pair fallback)"
                    : "Generic (write/notify pair fallback)";
                return new ResolvedBleProfile(
                    label, service.Uuid, write.Uuid, notify.Uuid,
                    false, 20);
            }
        }

        return null;
    }

    /// <summary>Splits a payload into BLE-write-sized chunks.</summary>
    public static IEnumerable<ReadOnlyMemory<byte>> Chunk(ReadOnlyMemory<byte> data, int maxChunkSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunkSize, 1);
        for (var offset = 0; offset < data.Length; offset += maxChunkSize)
        {
            yield return data.Slice(offset, Math.Min(maxChunkSize, data.Length - offset));
        }
    }

    /// <summary>
    ///     Advertised device name is a more stable signal than GATT UUIDs, which vary by
    ///     firmware batch on the same physical clone — used only to label an otherwise
    ///     unrecognized service for diagnosability, not to pick UUIDs.
    /// </summary>
    private static string? NameHint(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        if (deviceName.Contains("veepeak", StringComparison.OrdinalIgnoreCase))
        {
            return "Veepeak-like";
        }

        if (deviceName.Contains("vgate", StringComparison.OrdinalIgnoreCase))
        {
            return "Vgate-like";
        }

        if (deviceName.Contains("obdii", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("obd2", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("elm327", StringComparison.OrdinalIgnoreCase))
        {
            return "ELM327-family";
        }

        return null;
    }

    private static GattServiceInfo? FindService(IReadOnlyList<GattServiceInfo> services, Guid uuid) =>
        services.FirstOrDefault(s => BleUuid.Matches(s.Uuid, uuid));

    private static GattCharacteristicInfo? FindCharacteristic(
        GattServiceInfo service, Guid uuid, Func<GattCharacteristicInfo, bool> capability) =>
        service.Characteristics.FirstOrDefault(c => BleUuid.Matches(c.Uuid, uuid) && capability(c));
}
