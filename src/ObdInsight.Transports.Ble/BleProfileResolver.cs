namespace ObdInsight.Transports.Ble;

/// <summary>One discovered GATT characteristic, reduced to what profile matching needs.</summary>
public sealed record GattCharacteristicInfo(Guid Uuid, bool CanWrite, bool CanNotify);

/// <summary>One discovered GATT service and its characteristics.</summary>
public sealed record GattServiceInfo(Guid Uuid, IReadOnlyList<GattCharacteristicInfo> Characteristics);

/// <summary>
/// A concrete, connectable resolution: the actual UUIDs to use on this device
/// (may differ from the source profile when a fallback rule fired).
/// </summary>
public sealed record ResolvedBleProfile(
    string Name,
    Guid ServiceUuid,
    Guid WriteCharacteristicUuid,
    Guid NotifyCharacteristicUuid,
    bool WriteWithResponse,
    int MaxWriteSize);

/// <summary>
/// Pure profile auto-probe (docs/BLE_TRANSPORT_DESIGN.md §3): discovered GATT topology
/// in, best usable profile out. No BLE dependencies — unit-tested exhaustively;
/// the thin Plugin.BLE wrapper feeds it real discovery data.
/// </summary>
public static class BleProfileResolver
{
    public static ResolvedBleProfile? Resolve(IReadOnlyList<GattServiceInfo> services) =>
        Resolve(services, BleAdapterProfile.KnownProfiles);

    public static ResolvedBleProfile? Resolve(
        IReadOnlyList<GattServiceInfo> services,
        IReadOnlyList<BleAdapterProfile> knownProfiles)
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
        }

        // Rule 3: generic fallback — any service carrying a usable (write, notify) pair
        // or a dual-role characteristic. Last resort for unknown clones.
        foreach (var service in services)
        {
            var write = service.Characteristics.FirstOrDefault(c => c.CanWrite);
            var notify = service.Characteristics.FirstOrDefault(c => c.CanNotify);
            if (write is not null && notify is not null)
            {
                return new ResolvedBleProfile(
                    "Generic (write/notify pair fallback)",
                    service.Uuid, write.Uuid, notify.Uuid,
                    WriteWithResponse: false, MaxWriteSize: 20);
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

    private static GattServiceInfo? FindService(IReadOnlyList<GattServiceInfo> services, Guid uuid) =>
        services.FirstOrDefault(s => BleUuid.Matches(s.Uuid, uuid));

    private static GattCharacteristicInfo? FindCharacteristic(
        GattServiceInfo service, Guid uuid, Func<GattCharacteristicInfo, bool> capability) =>
        service.Characteristics.FirstOrDefault(c => BleUuid.Matches(c.Uuid, uuid) && capability(c));
}
