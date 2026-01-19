using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace ObdTestApp.Core.Communication.Bluetooth;

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

/// <summary>
/// Windows BLE scanner using WinRT advertisement watcher.
/// </summary>
public sealed class BleScanner : IDisposable
{
    private readonly ConcurrentDictionary<string, BleDeviceInfo> _discoveredDevices = new();
    private readonly BluetoothLEAdvertisementWatcher _watcher;
    private BleScanFilter? _currentFilter;

    public BleScanner()
    {
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.Received += OnAdvertisementReceived;
        _watcher.Stopped += OnWatcherStopped;
    }

    public event EventHandler<BleDeviceDiscoveredEventArgs>? DeviceDiscovered;

    public event EventHandler<BleScanStateChangedEventArgs>? ScanStateChanged;

    public bool IsScanning => _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started;

    public void Dispose()
    {
        StopScanAsync().GetAwaiter().GetResult();
        _watcher.Received -= OnAdvertisementReceived;
        _watcher.Stopped -= OnWatcherStopped;
    }

    public Task StartScanAsync(BleScanFilter? filter = null, CancellationToken cancellationToken = default)
    {
        _currentFilter = filter;
        _discoveredDevices.Clear();

        // Configure service filter if specified
        if (filter?.ServiceUuids?.Count > 0)
        {
            _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Clear();
            foreach (var uuid in filter.ServiceUuids)
            {
                _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(uuid);
            }
        }

        _watcher.Start();
        ScanStateChanged?.Invoke(this, new BleScanStateChangedEventArgs(true));
        return Task.CompletedTask;
    }

    public Task StopScanAsync()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
        {
            _watcher.Stop();
        }
        return Task.CompletedTask;
    }

    private static string FormatMacAddress(ulong address)
    {
        var bytes = BitConverter.GetBytes(address);
        return $"{bytes[5]:X2}:{bytes[4]:X2}:{bytes[3]:X2}:{bytes[2]:X2}:{bytes[1]:X2}:{bytes[0]:X2}";
    }

    private async void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            var address = FormatMacAddress(args.BluetoothAddress);

            // Apply address filter
            if (_currentFilter?.DeviceAddresses?.Count > 0 &&
                !_currentFilter.DeviceAddresses.Any(a => a.Equals(address, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // Apply RSSI filter
            if (_currentFilter?.MinRssi.HasValue == true && args.RawSignalStrengthInDBm < _currentFilter.MinRssi.Value)
            {
                return;
            }

            // Try to get device name (may need to connect briefly)
            var name = args.Advertisement.LocalName;
            if (string.IsNullOrEmpty(name))
            {
                // Try to get name from device
                try
                {
                    using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(args.BluetoothAddress);
                    name = device?.Name ?? "Unknown";
                }
                catch
                {
                    name = "Unknown";
                }
            }

            // Apply name filter
            if (_currentFilter?.DeviceNames?.Count > 0 &&
                !_currentFilter.DeviceNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // Extract advertised service UUIDs
            var serviceUuids = args.Advertisement.ServiceUuids.ToList();

            // Extract manufacturer data
            var manufacturerData = new Dictionary<string, byte[]>();
            foreach (var data in args.Advertisement.ManufacturerData)
            {
                var key = $"0x{data.CompanyId:X4}";
                var bytes = new byte[data.Data.Length];
                using var reader = Windows.Storage.Streams.DataReader.FromBuffer(data.Data);
                reader.ReadBytes(bytes);
                manufacturerData[key] = bytes;
            }

            var deviceInfo = new BleDeviceInfo(
                Name: name,
                Address: address,
                Rssi: args.RawSignalStrengthInDBm,
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
            System.Diagnostics.Debug.WriteLine($"Error processing advertisement: {ex.Message}");
        }
    }

    private void OnWatcherStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        ScanStateChanged?.Invoke(this, new BleScanStateChangedEventArgs(false));
    }
}
