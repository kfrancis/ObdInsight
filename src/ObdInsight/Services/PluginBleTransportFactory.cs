using ObdInsight.Core.Transports.Ble;
using Plugin.BLE;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ObdInsight.Services;

/// <summary>
///
/// </summary>
public sealed class PluginBleTransportFactory : IBleTransportFactory
{
    private readonly IBluetoothLE _bluetoothLe;
    private readonly IAdapter _adapter;

    public PluginBleTransportFactory()
    {
        _bluetoothLe = CrossBluetoothLE.Current;
        _adapter = CrossBluetoothLE.Current.Adapter;
    }

    /// <summary>
    /// Gets whether Bluetooth is available on this device.
    /// </summary>
    public bool IsAvailable => _bluetoothLe.IsAvailable;

    /// <summary>
    /// Gets whether Bluetooth is currently enabled/on.
    /// </summary>
    public bool IsOn => _bluetoothLe.IsOn;

    /// <inheritdoc/>
    public IBleTransport CreateTransport(BleDeviceProfile profile)
    {
        return new PluginBleTransport(_adapter, profile);
    }

    /// <inheritdoc/>
    public IBleScanner CreateScanner()
    {
        return new PluginBleScanner(_adapter);
    }
}

/// <summary>
/// Plugin.BLE-based BLE scanner implementation.
/// </summary>
public sealed class PluginBleScanner : IBleScanner
{
    private readonly IAdapter _adapter;
    private bool _disposed;

    public PluginBleScanner(IAdapter adapter)
    {
        _adapter = adapter;
        _adapter.DeviceDiscovered += OnDeviceDiscovered;
        _adapter.ScanTimeoutElapsed += OnScanTimeoutElapsed;
    }

    /// <inheritdoc/>
    public bool IsScanning => _adapter.IsScanning;

    /// <inheritdoc/>
    public event EventHandler<BleDeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <inheritdoc/>
    public event EventHandler<BleScanStateChangedEventArgs>? ScanStateChanged;

    /// <inheritdoc/>
    public async Task StartScanAsync(BleScanFilter? filter = null, CancellationToken cancellationToken = default)
    {
        // Configure scan parameters
        _adapter.ScanTimeout = 30000; // 30 seconds default

        // Build filter function
        Func<IDevice, bool>? deviceFilter = null;
        if (filter is not null)
        {
            deviceFilter = device =>
            {
                // Filter by RSSI
                if (filter.MinRssi.HasValue && device.Rssi < filter.MinRssi.Value)
                    return false;

                // Filter by name
                if (filter.DeviceNames?.Count > 0)
                {
                    var deviceName = device.Name ?? string.Empty;
                    if (!filter.DeviceNames.Any(n => deviceName.Contains(n, StringComparison.OrdinalIgnoreCase)))
                        return false;
                }

                // Filter by address
                if (filter.DeviceAddresses?.Count > 0)
                {
                    var deviceId = device.Id.ToString();
                    if (!filter.DeviceAddresses.Any(a => deviceId.Equals(a, StringComparison.OrdinalIgnoreCase)))
                        return false;
                }

                return true;
            };
        }

        ScanStateChanged?.Invoke(this, new BleScanStateChangedEventArgs(true));

        // Start scanning - Plugin.BLE handles service UUID filtering internally
        if (filter?.ServiceUuids?.Count > 0)
        {
            await _adapter.StartScanningForDevicesAsync(
                serviceUuids: filter.ServiceUuids.Select(u => u).ToArray(),
                deviceFilter: deviceFilter,
                allowDuplicatesKey: false,
                cancellationToken: cancellationToken);
        }
        else
        {
            await _adapter.StartScanningForDevicesAsync(
                deviceFilter: deviceFilter,
                allowDuplicatesKey: false,
                cancellationToken: cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task StopScanAsync()
    {
        if (_adapter.IsScanning)
        {
            await _adapter.StopScanningForDevicesAsync();
        }
        ScanStateChanged?.Invoke(this, new BleScanStateChangedEventArgs(false));
    }

    private void OnDeviceDiscovered(object? sender, DeviceEventArgs e)
    {
        var device = e.Device;

        // Convert to our BleDeviceInfo type
        var deviceInfo = new BleDeviceInfo(
            Name: device.Name ?? "Unknown",
            Address: device.Id.ToString(),
            Rssi: device.Rssi,
            AdvertisedServices: device.AdvertisementRecords?
                .Where(r => r.Type == Plugin.BLE.Abstractions.AdvertisementRecordType.UuidsComplete128Bit ||
                            r.Type == Plugin.BLE.Abstractions.AdvertisementRecordType.UuidsComplete16Bit)
                .SelectMany(r => ParseServiceUuids(r.Data))
                .ToList() ?? [],
            ManufacturerData: ExtractManufacturerData(device)
        );

        DeviceDiscovered?.Invoke(this, new BleDeviceDiscoveredEventArgs(deviceInfo));
    }

    private void OnScanTimeoutElapsed(object? sender, EventArgs e)
    {
        ScanStateChanged?.Invoke(this, new BleScanStateChangedEventArgs(false));
    }

    private static IEnumerable<Guid> ParseServiceUuids(byte[]? data)
    {
        if (data is null || data.Length == 0)
            yield break;

        // 16-bit UUIDs
        if (data.Length % 2 == 0 && data.Length <= 8)
        {
            for (int i = 0; i < data.Length; i += 2)
            {
                var uuid16 = BitConverter.ToUInt16(data, i);
                yield return new Guid($"0000{uuid16:X4}-0000-1000-8000-00805F9B34FB");
            }
        }
        // 128-bit UUIDs
        else if (data.Length % 16 == 0)
        {
            for (int i = 0; i < data.Length; i += 16)
            {
                var uuidBytes = new byte[16];
                Array.Copy(data, i, uuidBytes, 0, 16);
                yield return new Guid(uuidBytes);
            }
        }
    }

    private static IReadOnlyDictionary<string, byte[]>? ExtractManufacturerData(IDevice device)
    {
        var mfgRecord = device.AdvertisementRecords?
            .FirstOrDefault(r => r.Type == Plugin.BLE.Abstractions.AdvertisementRecordType.ManufacturerSpecificData);

        if (mfgRecord?.Data is null || mfgRecord.Data.Length < 2)
            return null;

        // First 2 bytes are company ID
        var companyId = BitConverter.ToUInt16(mfgRecord.Data, 0);
        var data = mfgRecord.Data.Skip(2).ToArray();

        return new Dictionary<string, byte[]>
        {
            [companyId.ToString("X4")] = data
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _adapter.DeviceDiscovered -= OnDeviceDiscovered;
        _adapter.ScanTimeoutElapsed -= OnScanTimeoutElapsed;

        if (_adapter.IsScanning)
        {
            _adapter.StopScanningForDevicesAsync().GetAwaiter().GetResult();
        }
    }
}