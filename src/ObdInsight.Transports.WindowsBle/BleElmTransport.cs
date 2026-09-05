using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using ObdInsight.Core.Communication.Elm327;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ObdInsight.Transports.WindowsBle
{
    public sealed class BleElmTransport : IConnectionAwareTransport
    {
        private static readonly Guid s_notifyCharacteristicUuid = new("0000fff1-0000-1000-8000-00805f9b34fb");
        private static readonly Guid s_serialServiceUuid = new("0000fff0-0000-1000-8000-00805f9b34fb");
        private static readonly Guid s_writeCharacteristicUuid = new("0000fff2-0000-1000-8000-00805f9b34fb");
        private readonly SemaphoreSlim _bufferLock = new(1, 1);
        private readonly string _deviceId;
        private readonly ILogger<BleElmTransport> _logger;
        private readonly Queue<byte> _receiveBuffer = new();
        private BluetoothLEDevice? _device;
        private GattCharacteristic? _notifyCharacteristic;
        private GattDeviceService? _serialService;
        private GattCharacteristic? _writeCharacteristic;
        private int _connectionLost;

        public event EventHandler? ConnectionLost;

        public BleElmTransport(string deviceId, ILogger<BleElmTransport>? logger = null)
        {
            _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
            _logger = logger ?? NullLogger<BleElmTransport>.Instance;
        }

        public bool EnableDebugLogging { get; set; }
        public bool IsOpen { get; private set; }

        public void ClearBuffer()
        {
            _bufferLock.Wait();
            try { _receiveBuffer.Clear(); }
            finally { _bufferLock.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await CleanupAsync();
        }

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public async ValueTask OpenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _connectionLost) != 0) throw new IOException("Create a fresh BLE transport after loss or disposal.");
            if (IsOpen) return;

            if (EnableDebugLogging)
                _logger.LogDebug("Connecting to BLE device {DeviceId}...", _deviceId);

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(MAC802DOT3(_deviceId)).AsTask(ct);
            if (_device == null)
                throw new IOException("BLE device not found");

            if (EnableDebugLogging)
                _logger.LogDebug("BLE device found. Name: {Name}, ConnectionStatus: {Status}",
                    _device.Name ?? "Unknown", _device.ConnectionStatus);

            // Check connection status and wait for connection with exponential backoff
            if (_device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                if (EnableDebugLogging)
                    _logger.LogWarning("Device not connected (status: {Status}), waiting for connection...",
                        _device.ConnectionStatus);

                // Subscribe to connection status changes
                var connectionTcs = new TaskCompletionSource<bool>();

                void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
                {
                    if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    {
                        if (EnableDebugLogging)
                            _logger.LogDebug("Device connected via status change event");
                        connectionTcs.TrySetResult(true);
                    }
                }

                _device.ConnectionStatusChanged += OnConnectionStatusChanged;

                try
                {
                    // Try to trigger connection by requesting GATT services (forces Windows to connect)
                    if (EnableDebugLogging)
                        _logger.LogDebug("Requesting GATT services to trigger connection...");

                    var servicesResult = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);

                    if (servicesResult.Status == GattCommunicationStatus.Success &&
                        _device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                    {
                        if (EnableDebugLogging)
                            _logger.LogDebug("Connection established via GetGattServicesAsync");
                    }
                    else
                    {
                        // Wait up to 5 seconds for connection with polling
                        for (var i = 0; i < 10; i++)
                        {
                            if (_device.ConnectionStatus == BluetoothConnectionStatus.Connected)
                                break;

                            if (EnableDebugLogging && i == 0)
                                _logger.LogDebug("Waiting for device to connect... (attempt {Attempt}/10)", i + 1);

                            await Task.Delay(500, ct);
                        }
                    }

                    // Final check
                    if (_device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                    {
                        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                        throw new IOException(
                            $"Device not reachable. Status: {_device.ConnectionStatus}\n" +
                            "Troubleshooting:\n" +
                            "  1. Ensure device is powered on and in range\n" +
                            "  2. Check Windows Bluetooth is enabled\n" +
                            "  3. Try: Restart-Service bthserv (PowerShell as Admin)\n" +
                            "  4. Pair device in Windows Settings if not already paired");
                    }

                    if (EnableDebugLogging)
                        _logger.LogDebug("Device connection established successfully");
                }
                finally
                {
                    _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                }
            }

            if (EnableDebugLogging)
                _logger.LogDebug("Retrieving GATT services...");

            var result = await _device.GetGattServicesForUuidAsync(s_serialServiceUuid).AsTask(ct);
            if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
                throw new IOException($"Serial service not found. Status: {result.Status}");

            _serialService = result.Services[0];

            if (EnableDebugLogging)
                _logger.LogDebug("Service found. Session Status: {SessionStatus}", _serialService.Session?.SessionStatus);

            _writeCharacteristic = await FindCharacteristicAsync(_serialService, s_writeCharacteristicUuid, ct);
            _notifyCharacteristic = await FindCharacteristicAsync(_serialService, s_notifyCharacteristicUuid, ct);
            if (_writeCharacteristic == null || _notifyCharacteristic == null)
                throw new IOException("Required characteristics not found");

            // Verify characteristic supports notifications
            var props = _notifyCharacteristic.CharacteristicProperties;
            if (!props.HasFlag(GattCharacteristicProperties.Notify) &&
                !props.HasFlag(GattCharacteristicProperties.Indicate))
                throw new IOException("Characteristic doesn't support notifications");

            if (EnableDebugLogging)
                _logger.LogDebug("Characteristic properties: {Props}", props);

            // Subscribe to value changes before enabling notifications
            _notifyCharacteristic.ValueChanged += OnNotifyValueChanged;

            // Allow more time for the BLE session to fully establish
            if (EnableDebugLogging)
                _logger.LogDebug("Waiting for BLE session to stabilize...");
            await Task.Delay(500, ct);

            // Enable notifications with retry logic - Windows BLE stack can be flaky on first attempt
            var notificationsEnabled = false;
            Exception? lastException = null;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var cccdValue = props.HasFlag(GattCharacteristicProperties.Indicate)
                        ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                        : GattClientCharacteristicConfigurationDescriptorValue.Notify;

                    if (EnableDebugLogging)
                        _logger.LogDebug("Attempt {Attempt}: Writing CCCD ({CccdValue}) to characteristic...",
                            attempt + 1, cccdValue);

                    var status =
                        await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);

                    if (status == GattCommunicationStatus.Success)
                    {
                        if (EnableDebugLogging)
                            _logger.LogDebug("✓ Notifications enabled successfully on attempt {Attempt}", attempt + 1);

                        notificationsEnabled = true;
                        break;
                    }

                    if (EnableDebugLogging)
                        _logger.LogWarning(
                            "CCCD write attempt {Attempt} returned {Status}. Device connection: {ConnStatus}, Session: {SessionStatus}",
                            attempt + 1, status, _device.ConnectionStatus, _serialService.Session?.SessionStatus);

                    // If unreachable, provide helpful diagnostic info
                    if (status == GattCommunicationStatus.Unreachable)
                    {
                        _logger.LogError("Device unreachable. Troubleshooting steps:\n" +
                                  "  1. Check if device is powered on and in range\n" +
                                  "  2. Restart Windows Bluetooth service: Restart-Service bthserv\n" +
                                  "  3. Remove device from Windows Bluetooth settings and re-pair\n" +
                                  "  4. Reboot Windows if issue persists");
                    }

                    lastException = new IOException($"CCCD write returned {status}");
                }
                catch (Exception ex)
                {
                    if (EnableDebugLogging)
                        _logger.LogWarning(ex, "CCCD write attempt {Attempt} threw exception", attempt + 1);

                    lastException = ex;
                }

                // Wait before retry (exponential backoff: 200ms, 400ms)
                if (attempt < 2)
                {
                    var delayMs = 200 * (1 << attempt);
                    if (EnableDebugLogging)
                        _logger.LogDebug("Waiting {DelayMs}ms before retry...", delayMs);
                    await Task.Delay(delayMs, ct);
                }
            }

            if (!notificationsEnabled)
            {
                _notifyCharacteristic.ValueChanged -= OnNotifyValueChanged;
                throw new IOException("Failed to enable notifications after 3 attempts", lastException);
            }

            IsOpen = true;
            _device.ConnectionStatusChanged += OnLinkStatusChanged;
            OnLinkStatusChanged(_device, EventArgs.Empty);
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (buffer.IsEmpty) return 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _connectionLost) != 0) throw new IOException("BLE connection ended.");
                await _bufferLock.WaitAsync(ct);
                try
                {
                    var count = Math.Min(buffer.Length, _receiveBuffer.Count);
                    if (count > 0)
                    {
                        for (var i = 0; i < count; i++) buffer.Span[i] = _receiveBuffer.Dequeue();
                        return count;
                    }
                }
                finally { _bufferLock.Release(); }
                // Quiet is not EOF. No queue access outside the buffer lock.
                await Task.Delay(10, ct);
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (_writeCharacteristic == null)
                throw new IOException("Transport not open");

            ct.ThrowIfCancellationRequested();
            using var writer = new DataWriter();
            writer.WriteBytes(data.ToArray());
            var status =
                await _writeCharacteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse).AsTask(ct);
            if (status != GattCommunicationStatus.Success)
                throw new IOException("Write failed");
        }

        public static ulong MAC802DOT3(string macAddress)
        {
            var hex = macAddress.Replace(":", "");
            return Convert.ToUInt64(hex, 16);
        }

        private static async Task<GattCharacteristic?> FindCharacteristicAsync(GattDeviceService service, Guid uuid,
            CancellationToken ct)
        {
            var characteristics = await service.GetCharacteristicsForUuidAsync(uuid).AsTask(ct);
            return characteristics.Status == GattCommunicationStatus.Success &&
                   characteristics.Characteristics.Count > 0
                ? characteristics.Characteristics[0]
                : null;
        }

        private async Task CleanupAsync()
        {
            Interlocked.Exchange(ref _connectionLost, 1);
            if (_device is { } device) device.ConnectionStatusChanged -= OnLinkStatusChanged;
            try
            {
                if (_notifyCharacteristic != null)
                {
                    _notifyCharacteristic.ValueChanged -= OnNotifyValueChanged;
                    await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup error: {Message}", ex.Message);
            }
            finally
            {
                _serialService?.Dispose();
                _device?.Dispose();
                _serialService = null;
                _device = null;
                _writeCharacteristic = null;
                _notifyCharacteristic = null;
                IsOpen = false;
                ClearBuffer();
            }
        }

        private void OnNotifyValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);

            _bufferLock.Wait();
            try
            {
                if (Volatile.Read(ref _connectionLost) != 0 || !ReferenceEquals(sender, _notifyCharacteristic)) return;
                foreach (var b in bytes)
                    _receiveBuffer.Enqueue(b);
            }
            finally
            {
                _bufferLock.Release();
            }
        }

        private void OnLinkStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (!ReferenceEquals(sender, _device) || sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected ||
                Interlocked.Exchange(ref _connectionLost, 1) != 0) return;
            IsOpen = false;
            if (ConnectionLost is not { } handlers) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { _logger.LogWarning(ex, "Connection loss subscriber failed"); }
            }
        }
    }
}
