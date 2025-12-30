using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using ObdInsight.Core;

namespace ObdInsight.DevTools;

/// <summary>
/// Windows-specific BLE transport using WinRT APIs.
/// Works on Windows 10/11 desktop with Bluetooth LE support.
/// </summary>
public sealed class WindowsBleTransport : BleTransportBase
{
    private BluetoothLEDevice? _device;
    private bool _isConnected;
    private GattCharacteristic? _notifyCharacteristic;
    private GattDeviceService? _service;
    private GattCharacteristic? _writeCharacteristic;

    public WindowsBleTransport(BleDeviceProfile profile) : base(profile)
    {
    }

    public override bool IsConnected => _isConnected && _device != null;

    public override async Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default)
    {
        try
        {
            SetConnectionState(BleConnectionState.Connecting);
            DeviceAddress = deviceAddress;

            // Parse MAC address to ulong
            var macValue = ParseMacAddress(deviceAddress);

            // Connect to device
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(macValue).AsTask(cancellationToken);
            if (_device == null)
            {
                SetConnectionState(BleConnectionState.Disconnected);
                return false;
            }

            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            // Get GATT services
            var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                await DisconnectAsync();
                return false;
            }

            // Find our target service
            _service = servicesResult.Services.FirstOrDefault(s => s.Uuid == Profile.ServiceUuid);
            if (_service == null)
            {
                // Log available services for debugging
                var availableServices = string.Join(", ", servicesResult.Services.Select(s => s.Uuid.ToString()));
                System.Diagnostics.Debug.WriteLine($"Service {Profile.ServiceUuid} not found. Available: {availableServices}");
                await DisconnectAsync();
                return false;
            }

            // Get characteristics
            var charsResult = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                await DisconnectAsync();
                return false;
            }

            _writeCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == Profile.WriteCharacteristicUuid);
            _notifyCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == Profile.NotifyCharacteristicUuid);

            if (_writeCharacteristic == null)
            {
                System.Diagnostics.Debug.WriteLine($"Write characteristic {Profile.WriteCharacteristicUuid} not found");
                await DisconnectAsync();
                return false;
            }

            // Subscribe to notifications if characteristic supports it
            if (_notifyCharacteristic != null)
            {
                var notifyProps = _notifyCharacteristic.CharacteristicProperties;
                if (notifyProps.HasFlag(GattCharacteristicProperties.Notify) ||
                    notifyProps.HasFlag(GattCharacteristicProperties.Indicate))
                {
                    _notifyCharacteristic.ValueChanged += OnCharacteristicValueChanged;

                    var cccdValue = notifyProps.HasFlag(GattCharacteristicProperties.Indicate)
                        ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                        : GattClientCharacteristicConfigurationDescriptorValue.Notify;

                    var status = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue)
                        .AsTask(cancellationToken);

                    if (status != GattCommunicationStatus.Success)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to enable notifications: {status}");
                    }
                }
            }

            ClearBuffer();
            _isConnected = true;
            SetConnectionState(BleConnectionState.Connected);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Connection failed: {ex.Message}");
            await DisconnectAsync();
            return false;
        }
    }

    public override async Task DisconnectAsync()
    {
        SetConnectionState(BleConnectionState.Disconnecting);

        try
        {
            if (_notifyCharacteristic != null)
            {
                _notifyCharacteristic.ValueChanged -= OnCharacteristicValueChanged;
                try
                {
                    await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch { /* Ignore errors during disconnect */ }
            }

            _service?.Dispose();

            if (_device != null)
            {
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _device.Dispose();
            }
        }
        finally
        {
            _device = null;
            _service = null;
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
        }
    }

    public override void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        base.Dispose();
    }

    protected override async Task WriteCharacteristicAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (_writeCharacteristic == null)
        {
            throw new InvalidOperationException("Write characteristic not available");
        }

        var writeType = Profile.WriteWithResponse
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;

        var buffer = data.AsBuffer();
        var result = await _writeCharacteristic.WriteValueAsync(buffer, writeType).AsTask(cancellationToken);

        if (result != GattCommunicationStatus.Success)
        {
            throw new IOException($"Write failed: {result}");
        }
    }

    private static ulong ParseMacAddress(string mac)
    {
        // Handle formats: "66:1e:87:02:c2:db" or "661e8702c2db"
        var cleanMac = mac.Replace(":", "").Replace("-", "");
        return Convert.ToUInt64(cleanMac, 16);
    }

    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var data = args.CharacteristicValue.ToArray();
        OnDataReceived(data);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
        }
    }
}