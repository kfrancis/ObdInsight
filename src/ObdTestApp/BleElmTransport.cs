using System;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ObdTestApp
{
    public sealed class BleElmTransport : IElmTransport
    {
        private static readonly Guid RxCharacteristicUuid =
            new("0000ffe1-0000-1000-8000-00805f9b34fb");

        // Common BLE Serial Service UUIDs (adapter-dependent)
        // You'd need to discover the actual UUIDs for your specific adapter
        private static readonly Guid SerialServiceUuid =
            new("0000ffe0-0000-1000-8000-00805f9b34fb");

        // Example
        private static readonly Guid TxCharacteristicUuid =
            new("0000ffe1-0000-1000-8000-00805f9b34fb");

        private readonly SemaphoreSlim _bufferLock = new(1, 1);
        private readonly string _deviceId;
        private readonly Queue<byte> _receiveBuffer = new();
        private BluetoothLEDevice? _device;
        private bool _isOpen;
        private GattCharacteristic? _rxCharacteristic;
        private GattCharacteristic? _txCharacteristic; // Write to adapter
        private GattDeviceService? _serialService;
                                                       // Read from adapter
                                                       // Example
                                                       // Example

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

        public ValueTask FlushAsync(CancellationToken ct)
        {
            // BLE writes are immediately transmitted
            return ValueTask.CompletedTask;
        }

        public static ulong MAC802DOT3(string macAddress)
        {
            string hex = macAddress.Replace(":", "");
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
                // Connect to BLE device
                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(MAC802DOT3(_deviceId)).AsTask(ct);
                if (_device == null)
                    throw new IOException("BLE device not found");

                var resolved = await ResolveSerialServiceAsync(_device, ct);
                if (resolved == null)
                    throw new IOException("Serial service with required characteristics not found");

                (_serialService, _txCharacteristic, _rxCharacteristic) = resolved.Value;

                // Subscribe to notifications
                var cccdResult = await _rxCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);

                if (cccdResult != GattCommunicationStatus.Success)
                    throw new IOException("Failed to enable notifications");

                _rxCharacteristic.ValueChanged += OnCharacteristicValueChanged;

                _isOpen = true;
            }
            catch (Exception ex)
            {
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
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await _bufferLock.WaitAsync(ct);
                try
                {
                    if (_receiveBuffer.Count > 0)
                    {
                        var count = Math.Min(buffer.Length, _receiveBuffer.Count);
                        for (int i = 0; i < count; i++)
                            buffer.Span[i] = _receiveBuffer.Dequeue();
                        return count;
                    }
                }
                finally
                {
                    _bufferLock.Release();
                }

                await Task.Delay(10, ct); // Poll interval
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
            if (!_isOpen || _txCharacteristic == null)
                throw new InvalidOperationException("Transport is not open");

            // BLE has MTU limits - need to chunk large writes
            const int maxChunkSize = 20; // BLE 4.x default MTU - 3 for ATT header

            for (int offset = 0; offset < data.Length; offset += maxChunkSize)
            {
                var chunkSize = Math.Min(maxChunkSize, data.Length - offset);
                var chunk = data.Slice(offset, chunkSize);

                var writer = new DataWriter();
                writer.WriteBytes(chunk.ToArray());

                var result = await _txCharacteristic.WriteValueAsync(
                    writer.DetachBuffer(),
                    GattWriteOption.WriteWithoutResponse).AsTask(ct);

                if (result != GattCommunicationStatus.Success)
                    throw new IOException($"BLE write failed: {result}");

                // Small delay between chunks to avoid overwhelming the adapter
                if (offset + chunkSize < data.Length)
                    await Task.Delay(10, ct);
            }
        }

        private async ValueTask CleanupAsync()
        {
            _isOpen = false;

            if (_rxCharacteristic != null)
            {
                try
                {
                    _rxCharacteristic.ValueChanged -= OnCharacteristicValueChanged;
                    await _rxCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch { /* Best effort */ }
            }

            _device?.Dispose();
            _device = null;
            _txCharacteristic = null;
            _rxCharacteristic = null;
            _serialService?.Dispose();
            _serialService = null;
        }

        private void OnCharacteristicValueChanged(
                                    GattCharacteristic sender,
            GattValueChangedEventArgs args)
        {
            // BLE notification received - add to buffer
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);

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

        private async Task<(GattDeviceService Service, GattCharacteristic Tx, GattCharacteristic Rx)?>
            ResolveSerialServiceAsync(BluetoothLEDevice device, CancellationToken ct)
        {
            // Try the known service UUID first (cached then uncached)
            var targeted = await TryGetServiceForUuidAsync(device, SerialServiceUuid, ct);
            if (targeted != null)
            {
                var tx = await TryGetCharacteristicAsync(targeted, TxCharacteristicUuid, ct);
                var rx = await TryGetCharacteristicAsync(targeted, RxCharacteristicUuid, ct);

                if (tx != null && rx != null)
                    return (targeted, tx, rx);

                targeted.Dispose();
            }

            // Fallback: enumerate all services and look for the expected characteristics
            var anyService = await TryFindServiceByCharacteristicsAsync(device, ct);
            if (anyService != null)
                return anyService;

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

        private static async Task<(GattDeviceService Service, GattCharacteristic Tx, GattCharacteristic Rx)?>
            TryFindServiceByCharacteristicsAsync(BluetoothLEDevice device, CancellationToken ct)
        {
            foreach (var cacheMode in new[] { BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached })
            {
                var servicesResult = await device.GetGattServicesAsync(cacheMode).AsTask(ct);
                if (servicesResult.Status != GattCommunicationStatus.Success)
                    continue;

                foreach (var service in servicesResult.Services)
                {
                    var tx = await TryGetCharacteristicAsync(service, TxCharacteristicUuid, ct);
                    var rx = await TryGetCharacteristicAsync(service, RxCharacteristicUuid, ct);

                    if (tx != null && rx != null)
                        return (service, tx, rx);

                    service.Dispose();
                }
            }

            return null;
        }
    }
}