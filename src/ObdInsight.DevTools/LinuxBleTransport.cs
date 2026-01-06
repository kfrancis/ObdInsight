#if !WINDOWS
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using ObdInsight.Core.Transports.Ble;
using System.Buffers;
using System.Text;

namespace ObdInsight.DevTools;

/// <summary>
/// Linux BLE transport using Linux.Bluetooth library (BlueZ over D-Bus).
/// Works on Linux with BlueZ v5.50+.
/// </summary>
public sealed class LinuxBleTransport : BleTransportBase, IAsyncDisposable
{
    private readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Device? _device;
    private IGattService1? _service;
    private IGattCharacteristic1? _writeCharacteristic;
    private Linux.Bluetooth.GattCharacteristic? _notifyCharacteristic;
    private volatile bool _isConnected;
    private volatile bool _userDisconnecting;

    // Diagnostic counters
    private int _notificationsReceived;
    private int _bytesReceived;
    private int _writeAttempts;
    private int _writeSuccesses;

    public LinuxBleTransport(BleDeviceProfile profile) : base(profile)
    {
    }

    /// <summary>
    /// Delay between consecutive writes in milliseconds.
    /// </summary>
    public int InterWriteDelayMs { get; set; } = 20;

    public override bool IsConnected => _isConnected && _device is not null && _writeCharacteristic is not null;

    public override async Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default)
    {
        try
        {
            _userDisconnecting = false;
            _isConnected = false;
            _notificationsReceived = 0;
            _bytesReceived = 0;
            _writeAttempts = 0;
            _writeSuccesses = 0;

            SetConnectionState(BleConnectionState.Connecting);
            DeviceAddress = deviceAddress;

            Log($"Connecting to {deviceAddress}...");

            // Get adapter
            var adapters = await BlueZManager.GetAdaptersAsync();
            var adapter = adapters.FirstOrDefault();

            if (adapter is null)
            {
                Log("No Bluetooth adapter found");
                SetConnectionState(BleConnectionState.Disconnected);
                return false;
            }

            // Find device by address
            var devices = await adapter.GetDevicesAsync();
            _device = devices.FirstOrDefault(d => 
                d.GetAddressAsync().GetAwaiter().GetResult()
                    .Equals(deviceAddress, StringComparison.OrdinalIgnoreCase));

            if (_device is null)
            {
                Log($"Device {deviceAddress} not found. Scanning...");
                
                // Start discovery to find the device
                await adapter.StartDiscoveryAsync();
                
                // Wait up to 10 seconds for the device to be discovered
                var timeout = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < timeout && !cancellationToken.IsCancellationRequested)
                {
                    devices = await adapter.GetDevicesAsync();
                    _device = devices.FirstOrDefault(d =>
                        d.GetAddressAsync().GetAwaiter().GetResult()
                            .Equals(deviceAddress, StringComparison.OrdinalIgnoreCase));

                    if (_device is not null)
                        break;

                    await Task.Delay(500, cancellationToken);
                }

                await adapter.StopDiscoveryAsync();

                if (_device is null)
                {
                    Log("Device not found after scan");
                    SetConnectionState(BleConnectionState.Disconnected);
                    return false;
                }
            }

            Log($"Found device: {await _device.GetNameAsync()}");

            // Connect to device
            await _device.ConnectAsync();

            // Wait for connection to be established
            await _device.WaitForPropertyValueAsync("Connected", value: true, TimeSpan.FromSeconds(10));
            //if (!connected)
            //{
            //    Log("Failed to establish connection");
            //    SetConnectionState(BleConnectionState.Disconnected);
            //    return false;
            //}

            // Wait for services to be resolved
            await _device.WaitForPropertyValueAsync("ServicesResolved", value: true, TimeSpan.FromSeconds(10));
            //if (!servicesResolved)
            //{
            //    Log("Services not resolved");
            //    await DisconnectAsync();
            //    return false;
            //}

            Log("Connected and services resolved");

            // Get the OBD service
            _service = await _device.GetServiceAsync(Profile.ServiceUuid.ToString());
            if (_service is null)
            {
                Log($"Service {Profile.ServiceUuid} not found");
                await DisconnectAsync();
                return false;
            }

            Log($"Found service: {Profile.ServiceUuid}");

            // Get characteristics
            _writeCharacteristic = await _service.GetCharacteristicAsync(Profile.WriteCharacteristicUuid.ToString());
            _notifyCharacteristic = await _service.GetCharacteristicAsync(Profile.NotifyCharacteristicUuid.ToString());

            if (_writeCharacteristic is null)
            {
                Log($"Write characteristic {Profile.WriteCharacteristicUuid} not found");
                await DisconnectAsync();
                return false;
            }

            if (_notifyCharacteristic is null)
            {
                Log($"Notify characteristic {Profile.NotifyCharacteristicUuid} not found");
                await DisconnectAsync();
                return false;
            }

            Log("Characteristics found");

            // Subscribe to notifications
            if (_notifyCharacteristic is not null)
            {
                _notifyCharacteristic.Value += OnCharacteristicValue;
                await _notifyCharacteristic.StartNotifyAsync();
                Log("Notifications enabled");
            }

            ClearBuffer();
            _isConnected = true;
            SetConnectionState(BleConnectionState.Connected);

            Log("Connection complete");
            return true;
        }
        catch (Exception ex)
        {
            Log($"Connection failed: {ex.GetType().Name}: {ex.Message}");
            await DisconnectAsync();
            return false;
        }
    }

    public override async Task DisconnectAsync()
    {
        _userDisconnecting = true;
        SetConnectionState(BleConnectionState.Disconnecting);
        Log($"DisconnectAsync called. Stats: notifications={_notificationsReceived}, bytes={_bytesReceived}, writes={_writeSuccesses}/{_writeAttempts}");

        try
        {
            if (_notifyCharacteristic is not null)
            {
                _notifyCharacteristic.Value -= OnCharacteristicValue;
                try
                {
                    await _notifyCharacteristic.StopNotifyAsync();
                }
                catch { }
            }

            if (_device is not null)
            {
                try
                {
                    await _device.DisconnectAsync();
                }
                catch { }
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
            Log("Disconnect complete");
        }
    }

    public override void Dispose()
    {
        _userDisconnecting = true;

        try
        {
            if (_notifyCharacteristic is not null)
                _notifyCharacteristic.Value -= OnCharacteristicValue;

            DisconnectAsync().GetAwaiter().GetResult();
        }
        catch { }
        finally
        {
            _device = null;
            _service = null;
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            _isConnected = false;
            _writeGate.Dispose();
        }

        base.Dispose();
    }

    public override ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return base.DisposeAsync();
    }

    /// <summary>
    /// Gets diagnostic statistics about the connection.
    /// </summary>
    public string GetDiagnostics()
    {
        return $"Notifications: {_notificationsReceived}, Bytes: {_bytesReceived}, Writes: {_writeSuccesses}/{_writeAttempts}";
    }

    protected override async Task WriteCharacteristicAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (_writeCharacteristic is null)
            throw new InvalidOperationException("Write characteristic not available");

        if (!_isConnected)
        {
            Log("Device not connected when trying to write");
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
            throw new IOException("Device not connected");
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            _writeAttempts++;

            var dataStr = Encoding.ASCII.GetString(data).Replace("\r", "\\r").Replace("\n", "\\n");
            Log($"Writing {data.Length} bytes: '{dataStr}'");

            // Write the data
            await _writeCharacteristic.WriteValueAsync(data, new Dictionary<string, object>());

            _writeSuccesses++;
            Log($"Write success");

            // Optional write pacing
            if (InterWriteDelayMs > 0)
                await Task.Delay(InterWriteDelayMs, cancellationToken);
        }
        catch (Exception ex)
        {
            Log($"Write failed: {ex.Message}");
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
            throw new IOException($"Write failed: {ex.Message}", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private Task OnCharacteristicValue(GattCharacteristic characteristic, GattCharacteristicValueEventArgs e)
    {
        _notificationsReceived++;
        var length = e.Value.Length;
        _bytesReceived += length;

        var rentedArray = _arrayPool.Rent(length);
        try
        {
            Array.Copy(e.Value, rentedArray, length);

            var text = Encoding.ASCII.GetString(rentedArray, 0, length);
            var escaped = text.Replace("\r", "\\r").Replace("\n", "\\n");
            Log($"RX notification #{_notificationsReceived}: {length} bytes: '{escaped}'");

            var data = new byte[length];
            Array.Copy(e.Value, data, length);
            OnDataReceived(data);
        }
        finally
        {
            _arrayPool.Return(rentedArray);
        }

        return Task.CompletedTask;
    }

    private static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[LinuxBLE] {message}");
    }
}

public sealed class LinuxBinaryBleTransport : IBinaryBleTransport, IAsyncDisposable
{
    private BleDeviceProfile binaryProfile;

    public LinuxBinaryBleTransport(BleDeviceProfile binaryProfile)
    {
        this.binaryProfile = binaryProfile;
    }

    public BleConnectionState ConnectionState => throw new NotImplementedException();

    public string DeviceAddress => throw new NotImplementedException();

    public bool IsConnected => throw new NotImplementedException();

    public event EventHandler<BleConnectionState>? ConnectionStateChanged;
    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    public void ClearReceiveBuffer()
    {
        throw new NotImplementedException();
    }

    public Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task DisconnectAsync()
    {
        throw new NotImplementedException();
    }

    public void Dispose()
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        throw new NotImplementedException();
    }

    public Task<byte[]> ReadAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<byte[]> SendCommandAsync(ReadOnlyMemory<byte> command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task WriteRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
#endif
