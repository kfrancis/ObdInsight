using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ObdInsight.DevTools;

/// <summary>
/// Windows-specific BLE transport using WinRT APIs.
/// Works on Windows 10/11 desktop with Bluetooth LE support.
///
/// This implementation follows Windows BLE best practices:
/// - Event-driven readiness instead of fixed delays
/// - Targeted UUID enumeration (Cached then Uncached)
/// - WriteValueWithResultAsync for detailed error reporting
/// - Serialized writes with configurable pacing
/// - Proper CCCD notification handling
/// - ArrayPool for reduced allocations in notification path
/// </summary>
public sealed class WindowsBleTransport : BleTransportBase, IAsyncDisposable
{
    private readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private BluetoothLEDevice? _device;
    private TaskCompletionSource<bool>? _gattReadyTcs;
    private GattSession? _gattSession;
    private volatile bool _isConnected;
    private int _maxPduSize;
    private GattCharacteristic? _notifyCharacteristic;
    private GattDeviceService? _service;
    private volatile bool _userDisconnecting;
    private GattCharacteristic? _writeCharacteristic;
    
    // Diagnostic counters
    private int _notificationsReceived;
    private int _bytesReceived;
    private int _writeAttempts;
    private int _writeSuccesses;
    
    /// <summary>
    /// Enable verbose debug logging to console (useful for troubleshooting connectivity issues).
    /// </summary>
    public bool EnableDebugLogging { get; set; }

    /// <summary>
    /// Event raised when data is sent to the device.
    /// </summary>
    public event EventHandler<string>? DataSent;

    /// <summary>
    /// Event raised when data is received from the device.
    /// </summary>
    public event EventHandler<string>? DataReceived;

    public WindowsBleTransport(BleDeviceProfile profile) : base(profile)
    {
    }

    /// <summary>
    /// Delay between consecutive writes in milliseconds. Some ELM327 clones need pacing.
    /// </summary>
    public int InterWriteDelayMs { get; set; } = 20;

    public override bool IsConnected => _isConnected && _device != null && _writeCharacteristic != null;

    public override async Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 3;
        Exception? lastException = null;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
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

                var macValue = ParseMacAddress(deviceAddress);
                Log($"Connection attempt {attempt}/{maxRetries}: Connecting to {deviceAddress} (0x{macValue:X})...");

                // Connect to device
                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(macValue).AsTask(cancellationToken);
                if (_device == null)
                {
                    Log("Failed to get BluetoothLEDevice");
                    lastException = new IOException("Failed to get BluetoothLEDevice from address");
                    
                    if (attempt < maxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)); // Exponential backoff
                        Log($"Retrying in {delay.TotalSeconds}s...");
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }
                    
                    SetConnectionState(BleConnectionState.Disconnected);
                    return false;
                }

                Log($"Got device: {_device.Name}, ConnectionStatus: {_device.ConnectionStatus}");
                _device.ConnectionStatusChanged += OnConnectionStatusChanged;

                // Create GATT session with event-driven readiness
                _gattReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                try
                {
                    _gattSession = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(cancellationToken);
                    if (_gattSession != null)
                    {
                        _gattSession.MaintainConnection = true;
                        _gattSession.SessionStatusChanged += OnSessionStatusChanged;
                        _gattSession.MaxPduSizeChanged += OnMaxPduSizeChanged;
                        _maxPduSize = _gattSession.MaxPduSize;
                        Log($"GATT session created, MaintainConnection=true, MaxPduSize={_maxPduSize}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Warning: Could not create GATT session: {ex.Message}");
                }

                // Get service using targeted UUID enumeration (Cached first, then Uncached)
                _service = await GetServiceForUuidAsync(Profile.ServiceUuid, cancellationToken);
                if (_service == null)
                {
                    Log($"Service {Profile.ServiceUuid} not found");
                    lastException = new IOException($"Service {Profile.ServiceUuid} not found");
                    await DisconnectAsync();
                    
                    if (attempt < maxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                        Log($"Retrying in {delay.TotalSeconds}s...");
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }
                    
                    return false;
                }

                Log($"Found target service: {_service.Uuid}");

                // Get characteristics using targeted enumeration
                _writeCharacteristic = await GetCharacteristicForUuidAsync(_service, Profile.WriteCharacteristicUuid, cancellationToken);
                if (_writeCharacteristic == null)
                {
                    Log($"Write characteristic {Profile.WriteCharacteristicUuid} not found");
                    lastException = new IOException($"Write characteristic {Profile.WriteCharacteristicUuid} not found");
                    await DisconnectAsync();
                    
                    if (attempt < maxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                        Log($"Retrying in {delay.TotalSeconds}s...");
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }
                    
                    return false;
                }

                Log($"Write characteristic found: {_writeCharacteristic.Uuid}, Props: {_writeCharacteristic.CharacteristicProperties}");

                _notifyCharacteristic = await GetCharacteristicForUuidAsync(_service, Profile.NotifyCharacteristicUuid, cancellationToken);
                if (_notifyCharacteristic != null)
                {
                    Log($"Notify characteristic found: {_notifyCharacteristic.Uuid}, Props: {_notifyCharacteristic.CharacteristicProperties}");

                    // Adapters sharing a service UUID do not always agree on which characteristic
                    // plays which role, and a profile table cannot know. Trust the properties the
                    // device actually reports: if the two are transposed relative to the profile,
                    // swap them rather than failing. Hardware-confirmed on a Veepeak BLE where
                    // FFF1 advertises Notify and FFF2 advertises Write/WriteWithoutResponse -
                    // the reverse of what the profile declares.
                    if (!Supports(_notifyCharacteristic, GattCharacteristicProperties.Notify | GattCharacteristicProperties.Indicate)
                        && Supports(_writeCharacteristic, GattCharacteristicProperties.Notify | GattCharacteristicProperties.Indicate)
                        && Supports(_notifyCharacteristic, GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse))
                    {
                        Log("Profile roles are transposed for this device - swapping write/notify characteristics");
                        (_writeCharacteristic, _notifyCharacteristic) = (_notifyCharacteristic, _writeCharacteristic);
                        Log($"Write is now {_writeCharacteristic.Uuid} ({_writeCharacteristic.CharacteristicProperties})");
                        Log($"Notify is now {_notifyCharacteristic.Uuid} ({_notifyCharacteristic.CharacteristicProperties})");
                    }

                    var notifyOk = await EnableNotificationsAsync(_notifyCharacteristic, cancellationToken);
                    if (!notifyOk && Profile.NotificationsRequired)
                    {
                        Log("Failed to enable required notifications - aborting connect");
                        lastException = new IOException("Failed to enable required notifications");
                        await DisconnectAsync();
                        
                        if (attempt < maxRetries)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                            Log($"Retrying in {delay.TotalSeconds}s...");
                            await Task.Delay(delay, cancellationToken);
                            continue;
                        }
                        
                        return false;
                    }
                }
                else if (Profile.NotificationsRequired)
                {
                    Log($"Notify characteristic {Profile.NotifyCharacteristicUuid} not found but required");
                    lastException = new IOException($"Notify characteristic {Profile.NotifyCharacteristicUuid} not found but required");
                    await DisconnectAsync();
                    
                    if (attempt < maxRetries)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                        Log($"Retrying in {delay.TotalSeconds}s...");
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }
                    
                    return false;
                }

                // Signal readiness and wait for GATT to be truly ready
                TrySignalGattReady();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                try
                {
                    await _gattReadyTcs.Task.WaitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Log("GATT readiness timeout - proceeding anyway");
                }

                ClearBuffer();
                _isConnected = true;
                SetConnectionState(BleConnectionState.Connected);

                Log("Connection complete, IsConnected=true");
                
                // Do a test write to wake up the adapter
                Log("Sending wake-up sequence...");
                await TestCommunicationAsync(cancellationToken);
                
                Log($"Connection successful after {attempt} attempt(s)!");
                return true;
            }
            catch (Exception ex)
            {
                lastException = ex;
                Log($"Connection attempt {attempt}/{maxRetries} failed: {ex.GetType().Name}: {ex.Message}");
                
                await DisconnectAsync();
                
                if (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                    Log($"Retrying in {delay.TotalSeconds}s...");
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        
        Log($"Connection failed after {maxRetries} attempts. Last error: {lastException?.Message}");
        SetConnectionState(BleConnectionState.Disconnected);
        return false;
    }

    /// <summary>
    /// Test basic communication by sending a carriage return and waiting for any response.
    /// </summary>
    private async Task TestCommunicationAsync(CancellationToken ct)
    {
        try
        {
            // Send a few carriage returns to clear any pending state
            for (int i = 0; i < 3; i++)
            {
                await WriteCharacteristicDirectAsync(new byte[] { 0x0D }, ct); // CR
                await Task.Delay(100, ct);
            }
            
            // Wait a bit and check if we received anything
            await Task.Delay(500, ct);
            
            Log($"After wake-up: notifications={_notificationsReceived}, bytes={_bytesReceived}");
        }
        catch (Exception ex)
        {
            Log($"Wake-up test failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Direct write without the semaphore (for internal use during connect).
    /// </summary>
    private async Task WriteCharacteristicDirectAsync(byte[] data, CancellationToken ct)
    {
        if (_writeCharacteristic == null) return;
        
        _writeAttempts++;
        var buffer = data.AsBuffer();
        var writeType = Profile.WriteWithResponse
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;
            
        var result = await _writeCharacteristic.WriteValueWithResultAsync(buffer, writeType).AsTask(ct);
        
        if (result.Status == GattCommunicationStatus.Success)
        {
            _writeSuccesses++;
            Log($"Direct write OK: {BitConverter.ToString(data)}");
        }
        else
        {
            Log($"Direct write FAILED: {result.Status}, ProtocolError={result.ProtocolError}");
        }
    }

    public override async Task DisconnectAsync()
    {
        _userDisconnecting = true;
        SetConnectionState(BleConnectionState.Disconnecting);
        Log($"DisconnectAsync called. Stats: notifications={_notificationsReceived}, bytes={_bytesReceived}, writes={_writeSuccesses}/{_writeAttempts}");

        try
        {
            // Cancel any pending readiness wait
            _gattReadyTcs?.TrySetCanceled();

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

            if (_gattSession != null)
            {
                _gattSession.SessionStatusChanged -= OnSessionStatusChanged;
                _gattSession.MaxPduSizeChanged -= OnMaxPduSizeChanged;
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
            SetConnectionState(BleConnectionState.Disconnected);
            Log("Disconnect complete");
        }
    }

    public override void Dispose()
    {
        // Best-effort non-blocking cleanup to avoid deadlocks
        _userDisconnecting = true;
        _gattReadyTcs?.TrySetCanceled();

        try
        {
            if (_notifyCharacteristic != null)
                _notifyCharacteristic.ValueChanged -= OnCharacteristicValueChanged;

            _service?.Dispose();

            if (_gattSession != null)
            {
                _gattSession.SessionStatusChanged -= OnSessionStatusChanged;
                _gattSession.MaxPduSizeChanged -= OnMaxPduSizeChanged;
                _gattSession.MaintainConnection = false;
                _gattSession.Dispose();
            }

            if (_device != null)
            {
                _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                _device.Dispose();
            }
        }
        catch { /* Best effort */ }
        finally
        {
            _device = null;
            _service = null;
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            _gattSession = null;
            _isConnected = false;
            _writeGate.Dispose();
        }

        base.Dispose();
    }

    public new async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeGate.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Drains any pending data from the receive buffer.
    /// </summary>
    public void DrainBuffer()
    {
        ClearBuffer();
        Log("Buffer drained");
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
        if (_writeCharacteristic == null)
            throw new InvalidOperationException("Write characteristic not available");

        if (_device?.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            Log("Device not connected when trying to write");
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
            throw new IOException("Device not connected");
        }

        // Serialize all writes - Windows BLE doesn't like concurrent writes
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            _writeAttempts++;
            
            var writeType = Profile.WriteWithResponse
                ? GattWriteOption.WriteWithResponse
                : GattWriteOption.WriteWithoutResponse;

            var buffer = data.AsBuffer();
            
            // Log what we're sending
            var dataStr = Encoding.ASCII.GetString(data).Replace("\r", "\\r").Replace("\n", "\\n");
            Log($"Writing {data.Length} bytes: '{dataStr}' (hex: {BitConverter.ToString(data)})");

            // Raise DataSent event
            DataSent?.Invoke(this, dataStr);

            // Use WriteValueWithResultAsync for richer error information
            GattWriteResult? result = null;
            Exception? lastException = null;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    result = await _writeCharacteristic.WriteValueWithResultAsync(buffer, writeType)
                        .AsTask(cancellationToken);

                    if (result.Status == GattCommunicationStatus.Success)
                    {
                        _writeSuccesses++;
                        Log($"Write success (attempt {attempt + 1})");
                        
                        // Optional write pacing for slow adapters
                        if (InterWriteDelayMs > 0)
                            await Task.Delay(InterWriteDelayMs, cancellationToken);
                        return;
                    }

                    var protoErr = result.ProtocolError?.ToString() ?? "none";
                    Log($"Write attempt {attempt + 1} failed: {result.Status}, ProtocolError={protoErr}");

                    // If write-without-response failed, try with response
                    if (writeType == GattWriteOption.WriteWithoutResponse && attempt == 0)
                    {
                        Log("Retrying with WriteWithResponse");
                        writeType = GattWriteOption.WriteWithResponse;
                    }

                    await Task.Delay(100, cancellationToken);
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Log($"Write exception: {ex.Message}");
                    await Task.Delay(100, cancellationToken);
                }
            }

            // All retries failed
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);

            var errorDetail = result != null
                ? $"{result.Status}, ProtocolError={result.ProtocolError?.ToString() ?? "none"}"
                : lastException?.Message ?? "Unknown error";

            throw new IOException($"Write failed after retries: {errorDetail}", lastException);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    #region Targeted UUID Enumeration

    private async Task<GattCharacteristic?> GetCharacteristicForUuidAsync(
        GattDeviceService service, Guid charUuid, CancellationToken ct)
    {
        // Try Cached first
        Log($"Getting characteristic {charUuid} (Cached)...");
        var result = await service.GetCharacteristicsForUuidAsync(charUuid, BluetoothCacheMode.Cached).AsTask(ct);

        if (result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0)
        {
            Log($"Found characteristic via Cached mode");
            return result.Characteristics[0];
        }

        // Fallback to Uncached
        Log($"Getting characteristic {charUuid} (Uncached fallback)...");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            result = await service.GetCharacteristicsForUuidAsync(charUuid, BluetoothCacheMode.Uncached).AsTask(ct);

            if (result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0)
            {
                Log($"Found characteristic via Uncached mode (attempt {attempt + 1})");
                return result.Characteristics[0];
            }

            Log($"Characteristic fetch attempt {attempt + 1}: Status={result.Status}, Count={result.Characteristics.Count}");
            await Task.Delay(300, ct);
        }

        return null;
    }

    private async Task<GattDeviceService?> GetServiceForUuidAsync(Guid serviceUuid, CancellationToken ct)
    {
        if (_device == null) return null;

        // Try Cached first (more reliable immediately after connect on Windows)
        Log($"Getting service {serviceUuid} (Cached)...");
        var result = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Cached).AsTask(ct);

        if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
        {
            Log($"Found service via Cached mode");
            return result.Services[0];
        }

        // Fallback to Uncached
        Log($"Getting service {serviceUuid} (Uncached fallback)...");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            result = await _device.GetGattServicesForUuidAsync(serviceUuid, BluetoothCacheMode.Uncached).AsTask(ct);

            if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
            {
                Log($"Found service via Uncached mode (attempt {attempt + 1})");
                return result.Services[0];
            }

            Log($"Service fetch attempt {attempt + 1}: Status={result.Status}, Count={result.Services.Count}");
            await Task.Delay(300, ct);
        }

        // Log available services for debugging
        var allServices = await _device.GetGattServicesAsync(BluetoothCacheMode.Cached).AsTask(ct);
        if (allServices.Status == GattCommunicationStatus.Success)
        {
            var uuids = string.Join(", ", allServices.Services.Select(s => s.Uuid.ToString()));
            Log($"Available services: {uuids}");
        }

        return null;
    }

    #endregion Targeted UUID Enumeration

    #region Notification Handling

    private async Task<bool> EnableNotificationsAsync(GattCharacteristic characteristic, CancellationToken ct)
    {
        var props = characteristic.CharacteristicProperties;
        Log($"Enabling notifications, Props: {props}");

        if (!props.HasFlag(GattCharacteristicProperties.Notify) &&
            !props.HasFlag(GattCharacteristicProperties.Indicate))
        {
            Log("Characteristic doesn't support Notify or Indicate");
            return false;
        }

        // Subscribe to value changes FIRST
        characteristic.ValueChanged += OnCharacteristicValueChanged;
        Log("Subscribed to ValueChanged event");

        var cccdValue = props.HasFlag(GattCharacteristicProperties.Indicate)
            ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
            : GattClientCharacteristicConfigurationDescriptorValue.Notify;

        Log($"Writing CCCD with value: {cccdValue}");

        // CCCD writes can fail the first time for non-bonded devices on Windows
        // Retry with delays
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // Use WriteValueWithResultAsync for CCCD to get detailed errors
                var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(cccdValue)
                    .AsTask(ct);

                if (result.Status == GattCommunicationStatus.Success)
                {
                    Log("CCCD write SUCCESS - notifications should now be enabled");
                    
                    // Verify we can read the CCCD back
                    try
                    {
                        var readResult = await characteristic.ReadClientCharacteristicConfigurationDescriptorAsync().AsTask(ct);
                        Log($"CCCD read back: Status={readResult.Status}, Value={readResult.ClientCharacteristicConfigurationDescriptor}");
                    }
                    catch (Exception ex)
                    {
                        Log($"CCCD read-back failed: {ex.Message}");
                    }
                    
                    return true;
                }

                var protoErr = result.ProtocolError?.ToString() ?? "none";
                Log($"CCCD write attempt {attempt + 1} failed: {result.Status}, ProtocolError={protoErr}");

                // ProtocolError can indicate auth/encryption issues
                if (result.ProtocolError != null)
                {
                    Log($"Protocol error may indicate pairing/authentication required");
                }

                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                Log($"CCCD write exception: {ex.Message}");
                await Task.Delay(500, ct);
            }
        }

        Log("Failed to enable notifications after retries - unsubscribing from ValueChanged");
        characteristic.ValueChanged -= OnCharacteristicValueChanged;
        return false;
    }

    private void OnCharacteristicValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        _notificationsReceived++;
        var length = (int)args.CharacteristicValue.Length;
        _bytesReceived += length;
        
        // Use ArrayPool to reduce allocation churn for high-frequency notifications
        var rentedArray = _arrayPool.Rent(length);

        try
        {
            args.CharacteristicValue.CopyTo(0, rentedArray, 0, length);

            // Log what we received
            var text = Encoding.ASCII.GetString(rentedArray, 0, length);
            var escaped = text.Replace("\r", "\\r").Replace("\n", "\\n");
            Log($"RX notification #{_notificationsReceived}: {length} bytes: '{escaped}'");

            // Raise DataReceived event
            DataReceived?.Invoke(this, text);

            // Create a copy for the base class (it may hold onto it)
            var data = new byte[length];
            Array.Copy(rentedArray, data, length);
            OnDataReceived(data);
        }
        finally
        {
            _arrayPool.Return(rentedArray);
        }
    }

    #endregion Notification Handling

    #region Event Handlers

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        Log($"ConnectionStatusChanged: {sender.ConnectionStatus}, UserDisconnecting: {_userDisconnecting}");

        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && !_userDisconnecting)
        {
            Log("External disconnection detected!");
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
            _gattReadyTcs?.TrySetResult(false);
        }
        else if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            TrySignalGattReady();
        }
    }

    private void OnMaxPduSizeChanged(GattSession sender, object args)
    {
        _maxPduSize = sender.MaxPduSize;
        Log($"MaxPduSizeChanged: {_maxPduSize}");
        TrySignalGattReady();
    }

    private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
    {
        Log($"SessionStatusChanged: Status={args.Status}, Error={args.Error}");

        if (args.Status == GattSessionStatus.Active)
        {
            TrySignalGattReady();
        }
        else if (args.Status == GattSessionStatus.Closed && !_userDisconnecting)
        {
            Log("GATT session closed unexpectedly");
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
        }
    }

    private void TrySignalGattReady()
    {
        // Signal readiness when we have enough to proceed
        if (_device?.ConnectionStatus == BluetoothConnectionStatus.Connected &&
            _writeCharacteristic != null)
        {
            _gattReadyTcs?.TrySetResult(true);
        }
    }

    #endregion Event Handlers

    #region Helpers

    private void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[BLE] {message}");
        
        if (EnableDebugLogging)
        {
            // Escape markup characters for Spectre.Console
            var escaped = message
                .Replace("[", "[[")
                .Replace("]", "]]")
                .Replace("{", "{{")
                .Replace("}", "}}");
            Spectre.Console.AnsiConsole.MarkupLine($"[grey][[BLE]] {escaped}[/]");
        }
    }

    /// <summary>True if the characteristic advertises any of the given properties.</summary>
    private static bool Supports(GattCharacteristic? characteristic, GattCharacteristicProperties any) =>
        characteristic != null && (characteristic.CharacteristicProperties & any) != 0;

    private static ulong ParseMacAddress(string mac)
    {
        var cleanMac = mac.Replace(":", "").Replace("-", "");
        return Convert.ToUInt64(cleanMac, 16);
    }

    #endregion Helpers
}