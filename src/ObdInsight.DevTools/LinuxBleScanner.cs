#if !WINDOWS
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using ObdInsight.Core.Transports.Ble;
using System.Collections.Concurrent;

namespace ObdInsight.DevTools;

/// <summary>
/// Linux BLE scanner using Linux.Bluetooth library (BlueZ over D-Bus).
/// </summary>
public sealed class LinuxBleScanner : IBleScanner
{
    private readonly ConcurrentDictionary<string, BleDeviceInfo> _discoveredDevices = new();
    private Adapter? _adapter;
    private BleScanFilter? _currentFilter;
    private bool _isScanning;
    private CancellationTokenSource? _scanCts;

    public event EventHandler<BleDeviceDiscoveredEventArgs>? DeviceDiscovered;
    public event EventHandler<BleScanStateChangedEventArgs>? ScanStateChanged;

    public bool IsScanning => _isScanning;

    public async Task StartScanAsync(BleScanFilter? filter = null, CancellationToken cancellationToken = default)
    {
        _currentFilter = filter;
        _discoveredDevices.Clear();

        // Get the first available Bluetooth adapter
        var adapters = await BlueZManager.GetAdaptersAsync();
        
        _adapter = adapters.FirstOrDefault();

        if (_adapter is null)
        {
            throw new InvalidOperationException("No Bluetooth adapter found");
        }

        // Subscribe to device found events
        _adapter.DeviceFound += OnDeviceFoundAsync;

        _isScanning = true;
        _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        ScanStateChanged?.Invoke(this, new BleScanStateChangedEventArgs(true));

        // Set discovery filter if service UUIDs are specified
        if (filter?.ServiceUuids?.Count > 0)
        {
            var filterDict = new Dictionary<string, object>
            {
                ["UUIDs"] = filter.ServiceUuids.Select(u => u.ToString()).ToArray(),
                ["Transport"] = "le" // Bluetooth LE only
            };

            await _adapter.SetDiscoveryFilterAsync(filterDict);
        }

        // Start discovery
        await _adapter.StartDiscoveryAsync();
    }

    public async Task StopScanAsync()
    {
        if (!_isScanning || _adapter is null)
            return;

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;

        try
        {
            await _adapter.StopDiscoveryAsync();
        }
        catch
        {
            // Ignore errors during stop
        }

        if (_adapter is not null)
        {
            _adapter.DeviceFound -= OnDeviceFoundAsync;
        }

        _isScanning = false;
        ScanStateChanged?.Invoke(this, new BleScanStateChangedEventArgs(false));
    }

    public void Dispose()
    {
        StopScanAsync().GetAwaiter().GetResult();
        _adapter = null;
    }

    private async Task OnDeviceFoundAsync(Adapter sender, DeviceFoundEventArgs eventArgs)
    {
        try
        {
            // Get device properties
            var properties = await sender.GetAllAsync();
            var address = await sender.GetAddressAsync();
            var name = await sender.GetNameAsync() ?? await sender.GetAliasAsync() ?? "Unknown";
            var rssi = await eventArgs.Device.GetRSSIAsync();

            // Apply filters
            if (_currentFilter?.DeviceAddresses?.Count > 0 &&
                !_currentFilter.DeviceAddresses.Any(a => a.Equals(address, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (_currentFilter?.MinRssi.HasValue == true && rssi < _currentFilter.MinRssi.Value)
            {
                return;
            }

            if (_currentFilter?.DeviceNames?.Count > 0 &&
                !_currentFilter.DeviceNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // Get advertised service UUIDs
            var serviceUuids = new List<Guid>();
            try
            {
                var uuids = await eventArgs.Device.GetUUIDsAsync();
                if (uuids is not null)
                {
                    foreach (var uuid in uuids)
                    {
                        if (Guid.TryParse(uuid, out var parsedGuid))
                        {
                            serviceUuids.Add(parsedGuid);
                        }
                    }
                }
            }
            catch
            {
                // UUIDs might not be available yet
            }

            // Get manufacturer data
            var manufacturerData = new Dictionary<string, byte[]>();
            try
            {
                var mfgData = await eventArgs.Device.GetManufacturerDataAsync();
                if (mfgData is not null)
                {
                    foreach (var kvp in mfgData)
                    {
                        manufacturerData[$"0x{kvp.Key:X4}"] = (byte[])kvp.Value;
                    }
                }
            }
            catch
            {
                // Manufacturer data might not be available
            }

            var deviceInfo = new BleDeviceInfo(
                Name: name,
                Address: address,
                Rssi: rssi,
                AdvertisedServices: serviceUuids,
                ManufacturerData: manufacturerData
            );

            // Only raise event if this is a new device or info changed
            if (_discoveredDevices.TryAdd(address, deviceInfo) ||
                _discoveredDevices.TryGetValue(address, out var existing) && existing.Rssi != deviceInfo.Rssi)
            {
                _discoveredDevices[address] = deviceInfo;
                DeviceDiscovered?.Invoke(this, new BleDeviceDiscoveredEventArgs(deviceInfo));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error processing device: {ex.Message}");
        }
    }
}
#endif
