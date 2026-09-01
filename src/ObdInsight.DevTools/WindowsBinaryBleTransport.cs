using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ObdInsight.DevTools;

/// <summary>
///     Windows BLE transport for binary protocol (service 6287).
///     Handles raw binary framing without ASCII conversion.
/// </summary>
public sealed class WindowsBinaryBleTransport : IBinaryBleTransport, IAsyncDisposable
{
    private readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
    private readonly BleDeviceProfile _profile;
    private readonly ConcurrentQueue<byte[]> _receiveQueue = new();
    private readonly SemaphoreSlim _receiveSignal = new(0);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _bytesReceived;

    private BluetoothLEDevice? _device;
    private volatile bool _disposing;
    private volatile bool _isConnected;

    // Diagnostic counters
    private int _notificationsReceived;
    private GattCharacteristic? _notifyCharacteristic;
    private GattDeviceService? _service;
    private GattSession? _session;
    private int _writeAttempts;
    private GattCharacteristic? _writeCharacteristic;
    private int _writeSuccesses;

    /// <summary>
    ///     Creates a new binary BLE transport with the specified profile.
    /// </summary>
    /// <param name="profile">BLE device profile (should be VeepeakBinary or similar)</param>
    public WindowsBinaryBleTransport(BleDeviceProfile profile)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <inheritdoc />
    public bool IsConnected => _isConnected && _device != null && _writeCharacteristic != null;

    /// <inheritdoc />
    public string DeviceAddress { get; private set; } = string.Empty;

    /// <inheritdoc />
    public BleConnectionState ConnectionState { get; private set; } = BleConnectionState.Disconnected;

    /// <inheritdoc />
    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    /// <inheritdoc />
    public event EventHandler<BleConnectionState>? ConnectionStateChanged;

    /// <inheritdoc />
    public async Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default)
    {
        try
        {
            _disposing = false;
            _notificationsReceived = 0;
            _bytesReceived = 0;
            _writeAttempts = 0;
            _writeSuccesses = 0;

            SetConnectionState(BleConnectionState.Connecting);
            DeviceAddress = deviceAddress;

            Log($"Connecting to {deviceAddress} using binary profile {_profile.Name}...");

            var mac = ParseMacAddress(deviceAddress);
            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(mac).AsTask(cancellationToken);
            if (_device is null)
            {
                Log("Failed to get device");
                SetConnectionState(BleConnectionState.Disconnected);
                return false;
            }

            Log($"Got device: {_device.Name}");
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;

            // Create GATT session for maintained connection
            _session = await GattSession.FromDeviceIdAsync(_device.BluetoothDeviceId).AsTask(cancellationToken);
            if (_session is not null)
            {
                _session.MaintainConnection = true;
                Log($"Session created, MaxPduSize={_session.MaxPduSize}");
            }

            // Get service (try Cached first, then Uncached)
            var svcResult = await _device.GetGattServicesForUuidAsync(
                _profile.ServiceUuid, BluetoothCacheMode.Cached).AsTask(cancellationToken);

            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            {
                Log("Service not found in cache, trying uncached...");
                svcResult = await _device.GetGattServicesForUuidAsync(
                    _profile.ServiceUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            }

            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            {
                Log($"Service {_profile.ServiceUuid} not found. Status: {svcResult.Status}");

                // Log available services for debugging
                var allServices =
                    await _device.GetGattServicesAsync(BluetoothCacheMode.Cached).AsTask(cancellationToken);
                if (allServices.Status == GattCommunicationStatus.Success)
                {
                    var uuids = string.Join(", ", allServices.Services.Select(s => s.Uuid.ToString()));
                    Log($"Available services: {uuids}");
                }

                await DisconnectAsync();
                return false;
            }

            _service = svcResult.Services[0];
            Log($"Found service: {_service.Uuid}");

            // Get write characteristic
            _writeCharacteristic = await GetCharacteristicAsync(
                _service, _profile.WriteCharacteristicUuid, cancellationToken);
            if (_writeCharacteristic is null)
            {
                Log($"Write characteristic {_profile.WriteCharacteristicUuid} not found");
                await DisconnectAsync();
                return false;
            }

            Log($"Write characteristic: Props={_writeCharacteristic.CharacteristicProperties}");

            // Get notify characteristic
            _notifyCharacteristic = await GetCharacteristicAsync(
                _service, _profile.NotifyCharacteristicUuid, cancellationToken);
            if (_notifyCharacteristic is null)
            {
                Log($"Notify characteristic {_profile.NotifyCharacteristicUuid} not found");
                await DisconnectAsync();
                return false;
            }

            Log($"Notify characteristic: Props={_notifyCharacteristic.CharacteristicProperties}");

            // Enable notifications
            if (_notifyCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                _notifyCharacteristic.ValueChanged += OnValueChanged;

                var cccdResult = await _notifyCharacteristic
                    .WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);

                if (cccdResult.Status != GattCommunicationStatus.Success)
                {
                    Log($"CCCD write failed: {cccdResult.Status}, ProtocolError={cccdResult.ProtocolError}");

                    // Try again after a short delay
                    await Task.Delay(500, cancellationToken);
                    cccdResult = await _notifyCharacteristic
                        .WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                            GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);

                    if (cccdResult.Status != GattCommunicationStatus.Success)
                    {
                        Log("CCCD retry also failed - notifications may not work");
                        if (_profile.NotificationsRequired)
                        {
                            await DisconnectAsync();
                            return false;
                        }
                    }
                }

                Log("Notifications enabled");
            }
            else if (_notifyCharacteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate))
            {
                _notifyCharacteristic.ValueChanged += OnValueChanged;

                var cccdResult = await _notifyCharacteristic
                    .WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Indicate).AsTask(cancellationToken);

                if (cccdResult.Status != GattCommunicationStatus.Success)
                {
                    Log($"CCCD (Indicate) write failed: {cccdResult.Status}");
                    if (_profile.NotificationsRequired)
                    {
                        await DisconnectAsync();
                        return false;
                    }
                }

                Log("Indications enabled");
            }

            _isConnected = true;
            SetConnectionState(BleConnectionState.Connected);
            Log($"Binary transport connected! Diagnostics: {GetDiagnostics()}");

            return true;
        }
        catch (Exception ex)
        {
            Log($"Connect failed: {ex.Message}");
            await DisconnectAsync();
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> SendCommandAsync(ReadOnlyMemory<byte> command, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        // Clear any pending data
        ClearReceiveBuffer();

        // Send command
        await WriteRawAsync(command, cancellationToken);

        // Wait for response with timeout
        return await ReadAvailableAsync(timeout, cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_writeCharacteristic is null)
            throw new InvalidOperationException("Not connected");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            _writeAttempts++;
            var buffer = data.ToArray().AsBuffer();

            Log($"TX: {BitConverter.ToString(data.ToArray())}");

            // Determine write type based on characteristic properties and profile
            var writeType = GattWriteOption.WriteWithoutResponse;

            if (_profile.WriteWithResponse ||
                !_writeCharacteristic.CharacteristicProperties.HasFlag(
                    GattCharacteristicProperties.WriteWithoutResponse))
            {
                writeType = GattWriteOption.WriteWithResponse;
            }

            var result = await _writeCharacteristic.WriteValueWithResultAsync(buffer, writeType)
                .AsTask(cancellationToken);

            if (result.Status != GattCommunicationStatus.Success)
            {
                Log($"Write failed: {result.Status}, ProtocolError={result.ProtocolError}");
                throw new IOException($"Write failed: {result.Status}, ProtocolError={result.ProtocolError}");
            }

            _writeSuccesses++;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            // Wait for data to arrive
            await _receiveSignal.WaitAsync(timeoutCts.Token);

            // Collect all available data
            var allData = new List<byte>();
            while (_receiveQueue.TryDequeue(out var chunk))
            {
                allData.AddRange(chunk);
            }

            // Drain any extra signals
            while (_receiveSignal.CurrentCount > 0)
            {
                await _receiveSignal.WaitAsync(0);
            }

            return [.. allData];
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            // Timeout - return whatever we have
            var allData = new List<byte>();
            while (_receiveQueue.TryDequeue(out var chunk))
            {
                allData.AddRange(chunk);
            }

            if (allData.Count > 0)
                return [.. allData];

            throw new TimeoutException($"No response within {timeout.TotalMilliseconds}ms");
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        _disposing = true;
        SetConnectionState(BleConnectionState.Disconnecting);
        Log($"Disconnecting. Stats: {GetDiagnostics()}");

        if (_notifyCharacteristic is not null)
        {
            _notifyCharacteristic.ValueChanged -= OnValueChanged;
            try
            {
                await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch
            {
                /* ignore */
            }
        }

        _service?.Dispose();

        if (_session is not null)
        {
            _session.MaintainConnection = false;
            _session.Dispose();
        }

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _device.Dispose();
        }

        _device = null;
        _session = null;
        _service = null;
        _writeCharacteristic = null;
        _notifyCharacteristic = null;
        _isConnected = false;

        SetConnectionState(BleConnectionState.Disconnected);
        Log("Disconnected");
    }

    /// <summary>
    ///     Adapter method for interface compatibility.
    /// </summary>
    public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        return WriteRawAsync(data, ct);
    }

    /// <summary>
    ///     Adapter method for interface compatibility.
    /// </summary>
    public Task<byte[]?> ReadAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        return ReadAvailableAsync(timeout, ct)!;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _writeLock.Dispose();
        _receiveSignal.Dispose();
    }

    /// <summary>
    ///     Gets diagnostic statistics about the connection.
    /// </summary>
    public string GetDiagnostics() =>
        $"Notifications: {_notificationsReceived}, Bytes: {_bytesReceived}, Writes: {_writeSuccesses}/{_writeAttempts}";

    /// <inheritdoc />
    public void ClearReceiveBuffer()
    {
        while (_receiveQueue.TryDequeue(out _)) { }

        while (_receiveSignal.CurrentCount > 0)
        {
            try { _receiveSignal.Wait(0); }
            catch { break; }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposing = true;
        DisconnectAsync().GetAwaiter().GetResult();
        _writeLock.Dispose();
        _receiveSignal.Dispose();
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        _notificationsReceived++;
        var length = (int)args.CharacteristicValue.Length;
        _bytesReceived += length;

        // Use ArrayPool to reduce allocation churn
        var rentedArray = _arrayPool.Rent(length);
        try
        {
            args.CharacteristicValue.CopyTo(0, rentedArray, 0, length);

            // Create a copy for the queue
            var data = new byte[length];
            Array.Copy(rentedArray, data, length);

            Log($"RX #{_notificationsReceived}: {BitConverter.ToString(data)}");

            // Also try to show as ASCII if applicable
            if (BinaryObdCommands.TryInterpretAsAscii(data, out var ascii))
            {
                var escaped = ascii.Replace("\r", "\\r").Replace("\n", "\\n");
                Log($"   ASCII: {escaped}");
            }

            _receiveQueue.Enqueue(data);
            _receiveSignal.Release();

            DataReceived?.Invoke(this, data);
        }
        finally
        {
            _arrayPool.Return(rentedArray);
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        Log($"Connection status: {sender.ConnectionStatus}");
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && !_disposing)
        {
            _isConnected = false;
            SetConnectionState(BleConnectionState.Disconnected);
        }
    }

    private void SetConnectionState(BleConnectionState state)
    {
        if (ConnectionState != state)
        {
            ConnectionState = state;
            ConnectionStateChanged?.Invoke(this, state);
        }
    }

    private async Task<GattCharacteristic?> GetCharacteristicAsync(
        GattDeviceService service, Guid uuid, CancellationToken ct)
    {
        // Try cached first
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Cached).AsTask(ct);
        if (result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0)
            return result.Characteristics[0];

        // Fallback to uncached
        result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(ct);
        return result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0
            ? result.Characteristics[0]
            : null;
    }

    private static ulong ParseMacAddress(string mac) =>
        Convert.ToUInt64(mac.Replace(":", "").Replace("-", ""), 16);

    private static void Log(string msg)
    {
        Console.WriteLine($"[BinaryBLE] {msg}");
        Debug.WriteLine($"[BinaryBLE] {msg}");
    }
}
