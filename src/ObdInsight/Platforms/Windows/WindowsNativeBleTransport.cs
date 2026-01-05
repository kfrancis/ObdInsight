using ObdInsight.Core.Transports.Ble;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ObdInsight.Services;

/// <summary>
/// Windows-specific BLE transport using native WinRT APIs.
/// This implementation is more reliable than Plugin.BLE on Windows.
/// </summary>
public sealed class WindowsNativeBleTransport : BleTransportBase, IAsyncDisposable
{
    private readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private BluetoothLEDevice? _device;
    private GattSession? _gattSession;
    private GattDeviceService? _service;
    private GattCharacteristic? _writeCharacteristic;
    private GattCharacteristic? _notifyCharacteristic;

    private volatile bool _isConnected;
    private volatile bool _userDisconnecting;
    private volatile bool _connectionStable;

    public WindowsNativeBleTransport(BleDeviceProfile profile) : base(profile)
    {
    }

    /// <inheritdoc/>
    public override bool IsConnected => _isConnected && _device != null && _writeCharacteristic != null;

    /// <inheritdoc/>
    public override async Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _userDisconnecting = false;
            _isConnected = false;
            _connectionStable = false;

            SetConnectionState(BleConnectionState.Connecting);
            DeviceAddress = deviceAddress;

            var macValue = ParseMacAddress(deviceAddress);
            Log($"Connecting to {deviceAddress} (0x{macValue:X})...");

            // Connect to device using native WinRT
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(macValue).AsTask(cancellationToken);
            if (_device == null)
            {
                Log("Failed to get BluetoothLEDevice");
                SetConnectionState(BleConnectionState.Disconnected);
                return false;
            }

            Log($"Got device: {_device.Name}, ConnectionStatus: {_device.ConnectionStatus}");

            // DON'T subscribe to ConnectionStatusChanged yet - it causes issues during service discovery
            // We'll add it after successful connection

            // DON'T create GattSession with MaintainConnection yet - causes connect/disconnect churn
            // on non-bonded devices. We'll create it after successful service discovery.

            // Get service - this is what actually establishes the GATT connection
            // Use multiple attempts with increasing delays
            _service = await GetServiceWithRetryAsync(Profile.ServiceUuid, cancellationToken);
            if (_service == null)
            {
                Log($"Service {Profile.ServiceUuid} not found after all retries");
                await DisconnectAsync();
                return false;
            }

            Log($"Found target service: {_service.Uuid}");

            // Get characteristics
            _writeCharacteristic = await GetCharacteristicForUuidAsync(_service, Profile.WriteCharacteristicUuid, cancellationToken);
            if (_writeCharacteristic == null)
            {
                Log($"Write characteristic {Profile.WriteCharacteristicUuid} not found");
                await DisconnectAsync();
                return false;
            }

            Log($"Write characteristic found: {_writeCharacteristic.Uuid}");

            _notifyCharacteristic = await GetCharacteristicForUuidAsync(_service, Profile.NotifyCharacteristicUuid, cancellationToken);
            if (_notifyCharacteristic != null)
            {
                Log($"Notify characteristic found: {_notifyCharacteristic.Uuid}");

                var notifyOk = await EnableNotificationsAsync(_notifyCharacteristic, cancellationToken);
                if (!notifyOk)
                {
                    Log("Warning: Failed to enable notifications");
                }
            }

            // NOW create GattSession to maintain the connection (after successful discovery)
            try
            {
                _gattSession = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(cancellationToken);
                if (_gattSession != null)
                {
                    _gattSession.MaintainConnection = true;
                    _gattSession.SessionStatusChanged += OnSessionStatusChanged;
                    Log($"GATT session created with MaintainConnection=true");
                }
            }
            catch (Exception ex)
            {
                Log($"Warning: Could not create GATT session: {ex.Message}");
            }

            // NOW subscribe to connection changes (after successful discovery)
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            ClearBuffer();
            _isConnected = true;
            _connectionStable = true;
            SetConnectionState(BleConnectionState.Connected);

            sw.Stop();
            Log($"Connection complete in {sw.ElapsedMilliseconds}ms");

            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Connection failed after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
            await DisconnectAsync();
            return false;
        }
    }

    /// <summary>
    /// Gets service with aggressive retry logic to handle Windows BLE flakiness.
    /// </summary>
    private async Task<GattDeviceService?> GetServiceWithRetryAsync(Guid serviceUuid, CancellationToken ct)
    {
        if (_device == null) return null;

        // Try Cached first (fastest when it works)
        Log($"Getting service {serviceUuid} (Cached)...");
        try
        {
            var result = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Cached).AsTask(ct);
            if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
            {
                Log("Found service via Cached mode");
                return result.Services[0];
            }
            Log($"Cached mode: Status={result.Status}, Count={result.Services.Count}");
        }
        catch (Exception ex)
        {
            Log($"Cached mode failed: {ex.Message}");
        }

        // Uncached with retries and increasing delays
        var delays = new[] { 500, 1000, 2000, 3000 };

        for (var attempt = 0; attempt < delays.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Wait before retry (connection needs time to stabilize)
            Log($"Waiting {delays[attempt]}ms before attempt {attempt + 1}...");
            await Task.Delay(delays[attempt], ct);

            // Check if device is still connected
            if (_device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                Log($"Device disconnected, waiting for reconnection...");
                
                // Wait up to 3 seconds for reconnection
                var reconnectDeadline = DateTime.UtcNow.AddSeconds(3);
                while (_device.ConnectionStatus != BluetoothConnectionStatus.Connected && 
                       DateTime.UtcNow < reconnectDeadline)
                {
                    await Task.Delay(100, ct);
                }

                if (_device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    Log("Device still disconnected after waiting");
                    continue;
                }
                Log("Device reconnected");
            }

            Log($"Getting service (Uncached, attempt {attempt + 1})...");
            try
            {
                var result = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached).AsTask(ct);

                if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
                {
                    Log($"Found service via Uncached mode (attempt {attempt + 1})");
                    return result.Services[0];
                }

                Log($"Attempt {attempt + 1}: Status={result.Status}, Count={result.Services.Count}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"Attempt {attempt + 1} exception: {ex.Message}");
            }
        }

        // Log available services for debugging
        try
        {
            Log("Listing all available services...");
            var allServices = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            if (allServices.Status == GattCommunicationStatus.Success && allServices.Services.Count > 0)
            {
                foreach (var svc in allServices.Services)
                {
                    Log($"  Available: {svc.Uuid}")
 ;
                }
            }
            else
            {
                Log($"  GetGattServicesAsync: Status={allServices.Status}, Count={allServices.Services.Count}");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to list services: {ex.Message}");
        }

        return null;
    }

    /// <inheritdoc/>
    public override async Task DisconnectAsync()
    {
        _userDisconnecting = true;
        SetConnectionState(BleConnectionState.Disconnecting);
        Log("DisconnectAsync called");

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
                catch { }
            }

            _service?.Dispose();

            if (_gattSession != null)
            {
                _gattSession.SessionStatusChanged -= OnSessionStatusChanged;
                _gattSession.MaintainConnection = false;
                _gattSession.Dispose();
                _gattSession = null;
            }

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
            _connectionStable = false;
            SetConnectionState(BleConnectionState.Disconnected);
            Log("Disconnect complete");
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _userDisconnecting = true;

        try
        {
            if (_notifyCharacteristic != null)
                _notifyCharacteristic.ValueChanged -= OnCharacteristicValueChanged;

            _service?.Dispose();

            if (_gattSession != null)
            {
                _gattSession.SessionStatusChanged -= OnSessionStatusChanged;
                _gattSession.MaintainConnection = false;
                _gattSession.Dispose();
            }

            if (_device != null)
            {
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _device.Dispose();
            }
        }
        catch { }
        finally
        {
            _device = null;
            _service = null;
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            _gattSession = null;
            _isConnected = false;
            _connectionStable = false;
            _writeGate.Dispose();
        }

        base.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeGate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    protected override async Task WriteCharacteristicAsync(byte[] data, CancellationToken cancellationToken)
    {
        if (_writeCharacteristic == null)
            throw new InvalidOperationException("Write characteristic not available");

        if (_device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
            throw new IOException("Device not connected");
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var writeType = Profile.WriteWithResponse
                ? GattWriteOption.WriteWithResponse
                : GattWriteOption.WriteWithoutResponse;

            var buffer = data.AsBuffer();

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var result = await _writeCharacteristic.WriteValueWithResultAsync(buffer, writeType)
                        .AsTask(cancellationToken);

                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        return;
                    }

                    Log($"Write attempt {attempt + 1} failed: {result.Status}");

                    if (writeType == GattWriteOption.WriteWithoutResponse && attempt == 0)
                    {
                        writeType = GattWriteOption.WriteWithResponse;
                    }

                    await Task.Delay(100, cancellationToken);
                }
                catch (Exception ex)
                {
                    Log($"Write exception: {ex.Message}");
                    await Task.Delay(100, cancellationToken);
                }
            }

            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
            throw new IOException("Write failed after retries");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    #region Characteristic Discovery

    private async Task<GattCharacteristic?> GetCharacteristicForUuidAsync(GattDeviceService service, Guid charUuid, CancellationToken ct)
    {
        // Try Cached first
        try
        {
            var result = await service.GetCharacteristicsForUuidAsync(charUuid, BluetoothCacheMode.Cached).AsTask(ct);
            if (result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0)
            {
                return result.Characteristics[0];
            }
        }
        catch { }

        // Fallback to Uncached with retries
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var result = await service.GetCharacteristicsForUuidAsync(charUuid, BluetoothCacheMode.Uncached).AsTask(ct);
                if (result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0)
                {
                    return result.Characteristics[0];
                }
            }
            catch { }

            await Task.Delay(300, ct);
        }

        return null;
    }

    #endregion

    #region Notification Handling

    private async Task<bool> EnableNotificationsAsync(GattCharacteristic characteristic, CancellationToken ct)
    {
        var props = characteristic.CharacteristicProperties;

        if (!props.HasFlag(GattCharacteristicProperties.Notify) &&
            !props.HasFlag(GattCharacteristicProperties.Indicate))
        {
            return false;
        }

        characteristic.ValueChanged += OnCharacteristicValueChanged;

        var cccdValue = props.HasFlag(GattCharacteristicProperties.Indicate)
            ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
            : GattClientCharacteristicConfigurationDescriptorValue.Notify;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(cccdValue)
                    .AsTask(ct);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    Log("Notifications enabled successfully");
                    return true;
                }

                Log($"CCCD write attempt {attempt + 1} failed: {result.Status}");
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                Log($"CCCD write exception: {ex.Message}");
                await Task.Delay(500, ct);
            }
        }

        characteristic.ValueChanged -= OnCharacteristicValueChanged;
        return false;
    }

    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var length = (int)args.CharacteristicValue.Length;
        var rentedArray = _arrayPool.Rent(length);

        try
        {
            args.CharacteristicValue.CopyTo(0, rentedArray, 0, length);

            var data = new byte[length];
            Array.Copy(rentedArray, data, length);
            OnDataReceived(data);
        }
        finally
        {
            _arrayPool.Return(rentedArray);
        }
    }

    #endregion

    #region Event Handlers

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        // Only log and react to connection changes AFTER we've established a stable connection
        if (!_connectionStable) return;

        Log($"ConnectionStatusChanged: {sender.ConnectionStatus}");

        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && !_userDisconnecting)
        {
            Log("External disconnection detected");
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
        }
    }

    private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
    {
        // Only log and react to session changes AFTER we've established a stable connection
        if (!_connectionStable) return;

        Log($"SessionStatusChanged: {args.Status}");

        if (args.Status == GattSessionStatus.Closed && !_userDisconnecting)
        {
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
        }
    }

    #endregion

    #region Helpers

    private static void Log(string message)
    {
        Debug.WriteLine($"[WinBLE] {message}");
    }

    private static ulong ParseMacAddress(string mac)
    {
        var cleanMac = mac.Replace(":", "").Replace("-", "");
        return Convert.ToUInt64(cleanMac, 16);
    }

    #endregion
}
