using Serilog;
using System;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace ObdTestApp.Core.Communication.Elm327
{
    public sealed class BleElmTransport : IElmTransport
    {
        // VEEPEAK ASCII ELM327 Service UUIDs (for standard ELM327 AT commands)
        // Primary service for ASCII communication
        private static readonly Guid SerialServiceUuid =
            new("0000fff0-0000-1000-8000-00805f9b34fb");

        // Write characteristic for sending commands
        private static readonly Guid WriteCharacteristicUuid =
            new("0000fff2-0000-1000-8000-00805f9b34fb");

        // Notify characteristic for receiving responses
        private static readonly Guid NotifyCharacteristicUuid =
            new("0000fff1-0000-1000-8000-00805f9b34fb");

        private readonly SemaphoreSlim _bufferLock = new(1, 1);
        private readonly string _deviceId;
        private readonly Queue<byte> _receiveBuffer = new();
        private BluetoothLEDevice? _device;
        private bool _isOpen;
        private GattCharacteristic? _writeCharacteristic; // For sending commands
        private GattCharacteristic? _notifyCharacteristic; // For receiving responses
        private GattDeviceService? _serialService;
        private int _rxNotificationCount;
        private int _rxTotalBytes;
        private int _txTotalBytes;

        /// <summary>
        /// Enable verbose debug logging to console (useful for troubleshooting connectivity issues).
        /// </summary>
        public bool EnableDebugLogging { get; set; }

        /// <summary>
        /// Initializes a new instance of the BleElmTransport class for the specified device identifier.
        /// </summary>
        /// <param name="deviceId">The unique identifier of the Bluetooth device to connect to. Cannot be null.</param>
        /// <exception cref="ArgumentNullException">Thrown if deviceId is null.</exception>
        public BleElmTransport(string deviceId)
        {
            _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
        }

        public bool IsOpen => _isOpen;

        /// <summary>
        /// Asynchronously releases the resources used by the current instance.
        /// </summary>
        /// <returns>A task that represents the asynchronous dispose operation.</returns>
        public async ValueTask DisposeAsync()
        {
            await CleanupAsync();
        }

        /// <summary>
        /// Clears all data from the buffer.
        /// </summary>
        public void ClearBuffer()
        {
            _bufferLock.Wait();
            try
            {
                _receiveBuffer.Clear();
            }
            finally
            {
                _bufferLock.Release();
            }
        }

        public ValueTask FlushAsync(CancellationToken ct)
        {
            // BLE writes are immediately transmitted
            return ValueTask.CompletedTask;
        }

        public static ulong MAC802DOT3(string macAddress)
        {
            var hex = macAddress.Replace(":", "");
            return Convert.ToUInt64(hex, 16);
        }

        /// <summary>
        /// Asynchronously opens a connection to the Bluetooth Low Energy (BLE) device and prepares it for
        /// communication.
        /// </summary>
        /// <remarks>If the connection is already open, this method returns immediately without performing
        /// any action. After a successful call, the device is ready for data transmission and reception. This method
        /// must be called before attempting to communicate with the BLE device.</remarks>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous open operation.</returns>
        /// <exception cref="IOException">Thrown if the BLE device, serial service, or required characteristics cannot be found, or if enabling
        /// notifications fails.</exception>
        public async ValueTask OpenAsync(CancellationToken ct)
        {
            if (_isOpen) return;

            try
            {
                _rxNotificationCount = 0;
                _rxTotalBytes = 0;
                _txTotalBytes = 0;
                
                Log($"Opening BLE connection to {_deviceId}");
                
                // Connect to BLE device
                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(MAC802DOT3(_deviceId)).AsTask(ct);
                if (_device == null)
                {
                    Log("ERROR: BLE device not found");
                    throw new IOException("BLE device not found");
                }

                Log($"Device found: {_device.Name}, ConnectionStatus: {_device.ConnectionStatus}");
                
                // Subscribe to connection status changes
                _device.ConnectionStatusChanged += OnConnectionStatusChanged;

                var resolved = await ResolveSerialServiceAsync(_device, ct);
                if (resolved == null)
                {
                    Log("ERROR: Serial service with required characteristics not found");
                    throw new IOException("Serial service with required characteristics not found");
                }

                (_serialService, _writeCharacteristic, _notifyCharacteristic) = resolved.Value;
                Log($"Service found: {_serialService.Uuid}");
                Log($"Write characteristic: {_writeCharacteristic.Uuid}");
                Log($"Notify characteristic: {_notifyCharacteristic.Uuid}");
                Log($"Write properties: {_writeCharacteristic.CharacteristicProperties}");
                Log($"Notify properties: {_notifyCharacteristic.CharacteristicProperties}");

                // Wait for device to be connected (accessing services triggers connection)
                Log($"Current ConnectionStatus: {_device.ConnectionStatus}");
                for (var i = 0; i < 50 && _device.ConnectionStatus != BluetoothConnectionStatus.Connected; i++)
                {
                    await Task.Delay(100, ct);
                    if (i % 10 == 0)
                        Log($"Waiting for connection... ({i * 100}ms, status: {_device.ConnectionStatus})");
                }

                if (_device.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    Log($"WARNING: Device still not connected after 5s (status: {_device.ConnectionStatus}), proceeding anyway...");
                }
                else
                {
                    Log($"Device connected! Status: {_device.ConnectionStatus}");
                }

                // Check if device requires pairing and pair if needed
                await EnsureDevicePairedAsync(_device, ct);

                // Verify the characteristics support their required operations
                var writeProps = _writeCharacteristic.CharacteristicProperties;
                var notifyProps = _notifyCharacteristic.CharacteristicProperties;

                var canWrite = writeProps.HasFlag(GattCharacteristicProperties.Write) ||
                              writeProps.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
                var canNotify = notifyProps.HasFlag(GattCharacteristicProperties.Notify) ||
                               notifyProps.HasFlag(GattCharacteristicProperties.Indicate);

                Log($"Write characteristic capabilities: CanWrite={canWrite}");
                Log($"Notify characteristic capabilities: CanNotify={canNotify}");

                if (!canWrite)
                {
                    Log("ERROR: Write characteristic does not support Write operations");
                    throw new IOException("Write characteristic does not support Write operations");
                }

                if (!canNotify)
                {
                    Log("ERROR: Notify characteristic does not support Notify/Indicate operations");
                    throw new IOException("Notify characteristic does not support notifications - cannot receive data");
                }

                // Subscribe to ValueChanged BEFORE enabling notifications
                _notifyCharacteristic.ValueChanged += OnCharacteristicValueChanged;
                Log("Subscribed to ValueChanged event");

                // Small delay to ensure event subscription is registered
                await Task.Delay(100, ct);

                // Check characteristic properties to determine the right CCCD value
                var cccdValue = notifyProps.HasFlag(GattCharacteristicProperties.Indicate)
                    ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                    : GattClientCharacteristicConfigurationDescriptorValue.Notify;

                Log($"Using CCCD value: {cccdValue}");

                // Enable notifications with retries
                GattCommunicationStatus cccdResult = GattCommunicationStatus.Unreachable;
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    Log($"Enabling notifications (attempt {attempt}/3)...");

                    // Try WriteClientCharacteristicConfigurationDescriptorWithResultAsync for better error info
                    try
                    {
                        var result = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorWithResultAsync(
                            cccdValue).AsTask(ct);
                        
                        cccdResult = result.Status;
                        
                        if (result.Status == GattCommunicationStatus.Success)
                        {
                            Log($"Notifications enabled successfully on attempt {attempt}");
                            break;
                        }
                        
                        var protocolError = result.ProtocolError?.ToString() ?? "none";
                        Log($"Notification enable attempt {attempt} failed: Status={result.Status}, ProtocolError={protocolError}");
                        
                        // If ProtocolError 3 (authentication), try re-pairing
                        if (result.ProtocolError == 3 && attempt == 1)
                        {
                            Log("ProtocolError 3 detected - attempting to unpair and re-pair...");
                            await UnpairAndRepairAsync(_device, ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Exception enabling notifications on attempt {attempt}: {ex.Message}");
                    }
                    
                    if (attempt < 3)
                    {
                        await Task.Delay(500 * attempt, ct); // Exponential backoff
                    }
                }

                if (cccdResult != GattCommunicationStatus.Success)
                {
                    Log($"ERROR: Failed to enable notifications after 3 attempts: {cccdResult}");
                    throw new IOException($"Failed to enable notifications: {cccdResult}");
                }

                _isOpen = true;
                Log("BLE connection opened successfully");
            }
            catch (Exception ex)
            {
                Log($"ERROR during OpenAsync: {ex.Message}");
                await CleanupAsync();
                throw new IOException($"Failed to connect to BLE device: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Asynchronously reads data into the provided buffer, waiting up to 5 seconds for data to become available.
        /// </summary>
        /// <remarks>If no data is available immediately, the method waits for up to 5 seconds for data to
        /// arrive before returning. The operation can be canceled by passing a cancellation token.</remarks>
        /// <param name="buffer">The memory buffer to receive the data. The method writes up to <paramref name="buffer"/>.Length bytes into
        /// this buffer.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the read operation.</param>
        /// <returns>The number of bytes read into the buffer. Returns 0 if no data is available within the 5-second timeout.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the transport is not open.</exception>
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (!_isOpen)
                throw new InvalidOperationException("Transport is not open");

            // Wait for data with timeout
            var startTime = DateTime.UtcNow;
            var deadline = startTime.AddSeconds(5);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                // Check buffer first (before acquiring lock for better diagnostics)
                var hasData = false;
                await _bufferLock.WaitAsync(ct);
                try
                {
                    hasData = _receiveBuffer.Count > 0;
                    
                    if (hasData)
                    {
                        var count = Math.Min(buffer.Length, _receiveBuffer.Count);
                        for (var i = 0; i < count; i++)
                            buffer.Span[i] = _receiveBuffer.Dequeue();
                        
                        if (EnableDebugLogging)
                        {
                            var elapsed = DateTime.UtcNow - startTime;
                            var text = System.Text.Encoding.ASCII.GetString(buffer.Slice(0, count).ToArray());
                            var escaped = text.Replace("\r", "\\r").Replace("\n", "\\n");
                            Log($"[BLE READ] {count} bytes after {elapsed.TotalMilliseconds:F0}ms: '{escaped}' (buffer has {_receiveBuffer.Count} remaining)");
                        }
                        
                        return count;
                    }
                }
                finally
                {
                    _bufferLock.Release();
                }

                // Only delay if no data - this allows fast polling when data is flowing
                if (!hasData)
                {
                    await Task.Delay(10, ct); // Poll interval
                }
            }

            // Timeout occurred
            var actualWait = DateTime.UtcNow - startTime;
            if (EnableDebugLogging)
            {
                Log($"[BLE READ TIMEOUT] No data received after {actualWait.TotalMilliseconds:F0}ms (buffer count: {_receiveBuffer.Count})");
            }
            
            return 0; // Timeout
        }

        /// <summary>
        /// Asynchronously writes the specified data to the underlying Bluetooth Low Energy (BLE) transport.
        /// </summary>
        /// <remarks>Data is automatically split into multiple chunks if it exceeds the BLE MTU size. A
        /// small delay is introduced between chunks to avoid overwhelming the BLE adapter.</remarks>
        /// <param name="data">The data to write to the BLE transport. The data will be sent in one or more chunks, depending on the BLE
        /// maximum transmission unit (MTU).</param>
        /// <param name="ct">A cancellation token that can be used to cancel the write operation.</param>
        /// <returns>A task that represents the asynchronous write operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the transport is not open.</exception>
        /// <exception cref="IOException">Thrown if the BLE write operation fails.</exception>
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!_isOpen || _writeCharacteristic == null)
                throw new InvalidOperationException("Transport is not open");

            // BLE has MTU limits - need to chunk large writes
            const int maxChunkSize = 20; // BLE 4.x default MTU - 3 for ATT header

            if (EnableDebugLogging)
            {
                var text = System.Text.Encoding.ASCII.GetString(data.ToArray());
                var escaped = text.Replace("\r", "\\r").Replace("\n", "\\n");
                Log($"[BLE WRITE] {data.Length} bytes: '{escaped}' (will send in {(data.Length + maxChunkSize - 1) / maxChunkSize} chunk(s))");
            }

            for (var offset = 0; offset < data.Length; offset += maxChunkSize)
            {
                var chunkSize = Math.Min(maxChunkSize, data.Length - offset);
                var chunk = data.Slice(offset, chunkSize);

                var writer = new DataWriter();
                writer.WriteBytes(chunk.ToArray());

                // Use WriteWithResponse for VEEPEAK as it's more reliable for ELM327 clones
                var result = await _writeCharacteristic.WriteValueAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithResponse).AsTask(ct);

                if (result != GattCommunicationStatus.Success)
                {
                    Log($"ERROR: BLE write failed: {result}");
                    throw new IOException($"BLE write failed: {result}");
                }

                _txTotalBytes += chunkSize;
                if (EnableDebugLogging)
                {
                    var chunkText = System.Text.Encoding.ASCII.GetString(chunk.ToArray());
                    var chunkEscaped = chunkText.Replace("\r", "\\r").Replace("\n", "\\n");
                    Log($"  [BLE WRITE chunk {offset / maxChunkSize + 1}] {chunkSize} bytes: '{chunkEscaped}'");
                }

                // Small delay between chunks to avoid overwhelming the adapter
                if (offset + chunkSize < data.Length)
                    await Task.Delay(10, ct);
            }
            if (EnableDebugLogging)
                Log($"[BLE WRITE complete] Session TX total: {_txTotalBytes} bytes");
        }

        private async ValueTask CleanupAsync()
        {
            _isOpen = false;

            if (_notifyCharacteristic != null)
            {
                try
                {
                    _notifyCharacteristic.ValueChanged -= OnCharacteristicValueChanged;
                    await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch { /* Best effort */ }
            }

            if (_device != null)
            {
                try
                {
                    _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
                }
                catch { /* Best effort */ }
                
                _device?.Dispose();
                _device = null;
            }
            
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            _serialService?.Dispose();
            _serialService = null;
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            Log($"ConnectionStatusChanged: {sender.ConnectionStatus}");
        }

        /// <summary>
        /// Ensures the device is paired if required. Many BLE devices need pairing before allowing notifications.
        /// </summary>
        private async Task EnsureDevicePairedAsync(BluetoothLEDevice device, CancellationToken ct)
        {
            try
            {
                // Check current pairing status
                var deviceInfo = device.DeviceInformation;
                var pairingInfo = deviceInfo.Pairing;
                
                Log($"Pairing status: IsPaired={pairingInfo.IsPaired}, CanPair={pairingInfo.CanPair}, ProtectionLevel={pairingInfo.ProtectionLevel}");

                if (pairingInfo.IsPaired)
                {
                    Log("Device is already paired");
                    return;
                }

                if (!pairingInfo.CanPair)
                {
                    Log("Device does not support pairing (may not be required)");
                    return;
                }

                // Device can be paired but isn't yet - attempt pairing
                Log("Device requires pairing. Initiating pairing request...");
                
                // Register custom pairing handler for PIN/confirmation scenarios
                pairingInfo.Custom.PairingRequested += OnPairingRequested;
                
                try
                {
                    var pairingResult = await pairingInfo.Custom.PairAsync(
                        DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ProvidePin,
                        DevicePairingProtectionLevel.EncryptionAndAuthentication).AsTask(ct);
                    
                    Log($"Pairing result: Status={pairingResult.Status}, ProtectionLevelUsed={pairingResult.ProtectionLevelUsed}");

                    if (pairingResult.Status == DevicePairingResultStatus.Paired ||
                        pairingResult.Status == DevicePairingResultStatus.AlreadyPaired)
                    {
                        Log("Device paired successfully!");
                    }
                    else
                    {
                        Log($"WARNING: Pairing did not succeed: {pairingResult.Status}");
                        
                        // Some devices may work without pairing, so we'll continue and let notification enable attempt determine success
                        if (pairingResult.Status == DevicePairingResultStatus.AuthenticationFailure ||
                            pairingResult.Status == DevicePairingResultStatus.AuthenticationTimeout)
                        {
                            Log("Authentication required but failed. User may need to confirm pairing on device.");
                        }
                    }
                }
                finally
                {
                    pairingInfo.Custom.PairingRequested -= OnPairingRequested;
                }
            }
            catch (Exception ex)
            {
                Log($"Exception during pairing check/attempt: {ex.Message}");
                // Continue anyway - device might not require pairing
            }
        }

        /// <summary>
        /// Attempts to unpair and re-pair the device. Useful when authentication fails.
        /// </summary>
        private async Task UnpairAndRepairAsync(BluetoothLEDevice device, CancellationToken ct)
        {
            try
            {
                var deviceInfo = device.DeviceInformation;
                var pairingInfo = deviceInfo.Pairing;

                if (pairingInfo.IsPaired)
                {
                    Log("Unpairing device...");
                    var unpairResult = await pairingInfo.UnpairAsync().AsTask(ct);
                    Log($"Unpair result: {unpairResult.Status}");
                    
                    // Wait for unpair to complete
                    await Task.Delay(1000, ct);
                }

                // Re-pair
                Log("Re-pairing device...");
                await EnsureDevicePairedAsync(device, ct);
            }
            catch (Exception ex)
            {
                Log($"Exception during unpair/re-pair: {ex.Message}");
            }
        }

        private void OnPairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            Log($"Pairing requested: Kind={args.PairingKind}");
            
            switch (args.PairingKind)
            {
                case DevicePairingKinds.ConfirmOnly:
                    // Auto-accept confirmation requests (common for BLE devices)
                    Log("Auto-accepting pairing confirmation");
                    args.Accept();
                    break;
                    
                case DevicePairingKinds.ProvidePin:
                    // Try common default PINs for BLE devices
                    Log("PIN required - trying default PIN: 0000");
                    args.Accept("0000");
                    break;
                    
                case DevicePairingKinds.DisplayPin:
                    Log($"Display PIN on device: {args.Pin}");
                    args.Accept();
                    break;
                    
                case DevicePairingKinds.ConfirmPinMatch:
                    Log($"Confirm PIN match: {args.Pin}");
                    args.Accept();
                    break;
                    
                default:
                    Log($"Unhandled pairing kind: {args.PairingKind}");
                    args.Accept(); // Try accepting anyway
                    break;
            }
        }

        private void OnCharacteristicValueChanged(
                                    GattCharacteristic sender,
            GattValueChangedEventArgs args)
        {
            // BLE notification received - add to buffer
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);

            _rxNotificationCount++;
            _rxTotalBytes += bytes.Length;

            if (EnableDebugLogging)
            {
                var text = System.Text.Encoding.ASCII.GetString(bytes);
                var escaped = text.Replace("\r", "\\r").Replace("\n", "\\n");
                Log($"RX notification #{_rxNotificationCount}: {bytes.Length} bytes: '{escaped}' (total RX: {_rxTotalBytes})");
            }

            _bufferLock.Wait();
            try
            {
                foreach (var b in bytes)
                    _receiveBuffer.Enqueue(b);
            }
            finally
            {
                _bufferLock.Release();
            }
        }

        private async Task<(GattDeviceService Service, GattCharacteristic Write, GattCharacteristic Notify)?>
            ResolveSerialServiceAsync(BluetoothLEDevice device, CancellationToken ct)
        {
            Log("Resolving serial service and characteristics...");

            // Try the known service UUID first (cached then uncached)
            var targeted = await TryGetServiceForUuidAsync(device, SerialServiceUuid, ct);
            if (targeted != null)
            {
                Log($"Found service {SerialServiceUuid}");

                // Get the write and notify characteristics
                var writeChar = await TryGetCharacteristicAsync(targeted, WriteCharacteristicUuid, ct);
                var notifyChar = await TryGetCharacteristicAsync(targeted, NotifyCharacteristicUuid, ct);

                if (writeChar != null && notifyChar != null)
                {
                    // Verify write characteristic can write
                    var writeProps = writeChar.CharacteristicProperties;
                    var canWrite = writeProps.HasFlag(GattCharacteristicProperties.Write) ||
                                  writeProps.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);

                    // Verify notify characteristic can notify
                    var notifyProps = notifyChar.CharacteristicProperties;
                    var canNotify = notifyProps.HasFlag(GattCharacteristicProperties.Notify) ||
                                   notifyProps.HasFlag(GattCharacteristicProperties.Indicate);

                    if (canWrite && canNotify)
                    {
                        Log($"Found write characteristic {WriteCharacteristicUuid} with Write capabilities");
                        Log($"Found notify characteristic {NotifyCharacteristicUuid} with Notify capabilities");
                        return (targeted, writeChar, notifyChar);
                    }

                    Log($"Characteristics found but missing required properties: Write={canWrite}, Notify={canNotify}");
                }
                else
                {
                    Log($"Write or Notify characteristics not found in service");
                }

                targeted.Dispose();
            }
            else
            {
                Log($"Service {SerialServiceUuid} not found");
            }

            // Fallback: Try alternative service UUID for some VEEPEAK variants
            Log("Trying alternative service UUID 0000FFE0...");
            var altServiceUuid = new Guid("0000ffe0-0000-1000-8000-00805f9b34fb");
            var altCharUuid = new Guid("0000ffe1-0000-1000-8000-00805f9b34fb");

            var altService = await TryGetServiceForUuidAsync(device, altServiceUuid, ct);
            if (altService != null)
            {
                Log($"Found alternative service {altServiceUuid}");

                // For this service, same characteristic is used for both write and notify
                var altChar = await TryGetCharacteristicAsync(altService, altCharUuid, ct);
                if (altChar != null)
                {
                    var props = altChar.CharacteristicProperties;
                    var canWrite = props.HasFlag(GattCharacteristicProperties.Write) ||
                                  props.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
                    var canNotify = props.HasFlag(GattCharacteristicProperties.Notify) ||
                                   props.HasFlag(GattCharacteristicProperties.Indicate);

                    if (canWrite && canNotify)
                    {
                        Log($"Found alternative characteristic {altCharUuid} with Write and Notify capabilities");
                        return (altService, altChar, altChar);
                    }
                }

                altService.Dispose();
            }

            Log("ERROR: No suitable service/characteristic combination found");
            return null;
        }


        private static async Task<GattDeviceService?> TryGetServiceForUuidAsync(
            BluetoothLEDevice device,
            Guid uuid,
            CancellationToken ct)
        {
            foreach (var cacheMode in new[] { BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached })
            {
                var result = await device.GetGattServicesForUuidAsync(uuid, cacheMode).AsTask(ct);
                if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
                    return result.Services[0];
            }

            return null;
        }

        private static async Task<GattCharacteristic?> TryGetCharacteristicAsync(
            GattDeviceService service,
            Guid characteristicUuid,
            CancellationToken ct)
        {
            var result = await service.GetCharacteristicsForUuidAsync(characteristicUuid).AsTask(ct);
            return result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0
                ? result.Characteristics[0]
                : null;
        }

        
        private void Log(string message)
        {
            // Always log to Serilog for file logging
            Serilog.Log.Debug("[BleTransport] {Message}", message);
            System.Diagnostics.Debug.WriteLine($"[BleTransport] {message}");

            if (EnableDebugLogging)
            {
                // Escape markup characters for Spectre.Console
                var escaped = message
                    .Replace("[", "[[")
                    .Replace("]", "]]")
                    .Replace("{", "{{")
                    .Replace("}", "}}");
                Spectre.Console.AnsiConsole.MarkupLine($"[grey][[BleTransport]] {escaped}[/]");
            }
        }
    }
}
