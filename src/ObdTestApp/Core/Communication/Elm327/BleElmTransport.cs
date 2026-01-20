using Serilog;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ObdTestApp.Core.Communication.Elm327
{
    public sealed class BleElmTransport : IElmTransport
    {
        private static readonly Guid s_notifyCharacteristicUuid = new("0000fff1-0000-1000-8000-00805f9b34fb");
        private static readonly Guid s_serialServiceUuid = new("0000fff0-0000-1000-8000-00805f9b34fb");
        private static readonly Guid s_writeCharacteristicUuid = new("0000fff2-0000-1000-8000-00805f9b34fb");
        private readonly SemaphoreSlim _bufferLock = new(1, 1);
        private readonly string _deviceId;
        private readonly Queue<byte> _receiveBuffer = new();
        private BluetoothLEDevice? _device;
        private bool _isOpen;
        private GattCharacteristic? _notifyCharacteristic;
        private GattDeviceService? _serialService;
        private GattCharacteristic? _writeCharacteristic;

        public BleElmTransport(string deviceId)
        {
            _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
        }

        public bool EnableDebugLogging { get; set; }
        public bool IsOpen => _isOpen;

        public static ulong MAC802DOT3(string macAddress)
        {
            var hex = macAddress.Replace(":", "");
            return Convert.ToUInt64(hex, 16);
        }

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
            if (_isOpen) return;

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(MAC802DOT3(_deviceId)).AsTask(ct);
            if (_device == null)
                throw new IOException("BLE device not found");

            var result = await _device.GetGattServicesForUuidAsync(s_serialServiceUuid).AsTask(ct);
            if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
                throw new IOException("Serial service not found");

            _serialService = result.Services[0];

            _writeCharacteristic = await FindCharacteristicAsync(_serialService, s_writeCharacteristicUuid, ct);
            _notifyCharacteristic = await FindCharacteristicAsync(_serialService, s_notifyCharacteristicUuid, ct);
            if (_writeCharacteristic == null || _notifyCharacteristic == null)
                throw new IOException("Required characteristics not found");

            // Verify characteristic supports notifications
            var props = _notifyCharacteristic.CharacteristicProperties;
            if (!props.HasFlag(GattCharacteristicProperties.Notify) && !props.HasFlag(GattCharacteristicProperties.Indicate))
                throw new IOException("Characteristic doesn't support notifications");

            // Subscribe to value changes before enabling notifications
            _notifyCharacteristic.ValueChanged += OnNotifyValueChanged;

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

                    var status = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue);

                    if (status == GattCommunicationStatus.Success)
                    {
                        if (EnableDebugLogging)
                            Log.Debug("Notifications enabled successfully on attempt {Attempt}", attempt + 1);

                        notificationsEnabled = true;
                        break;
                    }

                    if (EnableDebugLogging)
                        Log.Warning("CCCD write attempt {Attempt} returned {Status}", attempt + 1, status);

                    lastException = new IOException($"CCCD write returned {status}");
                }
                catch (Exception ex)
                {
                    if (EnableDebugLogging)
                        Log.Warning(ex, "CCCD write attempt {Attempt} threw exception", attempt + 1);

                    lastException = ex;
                }

                // Wait before retry (exponential backoff: 100ms, 200ms, 400ms)
                if (attempt < 2)
                    await Task.Delay(100 * (1 << attempt), ct);
            }

            if (!notificationsEnabled)
            {
                _notifyCharacteristic.ValueChanged -= OnNotifyValueChanged;
                throw new IOException("Failed to enable notifications after 3 attempts", lastException);
            }

            _isOpen = true;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var timeout = TimeSpan.FromMilliseconds(250);
            var deadline = DateTime.UtcNow + timeout;

            while (_receiveBuffer.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, ct);
            }

            _bufferLock.Wait(ct);
            try
            {
                var count = Math.Min(buffer.Length, _receiveBuffer.Count);
                for (var i = 0; i < count; i++)
                    buffer.Span[i] = _receiveBuffer.Dequeue();
                return count;
            }
            finally
            {
                _bufferLock.Release();
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            if (_writeCharacteristic == null)
                throw new IOException("Transport not open");

            var writer = new DataWriter();
            writer.WriteBytes(buffer.ToArray());
            var status = await _writeCharacteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
            if (status != GattCommunicationStatus.Success)
                throw new IOException("Write failed");
        }

        private static async Task<GattCharacteristic?> FindCharacteristicAsync(GattDeviceService service, Guid uuid, CancellationToken ct)
        {
            var characteristics = await service.GetCharacteristicsForUuidAsync(uuid).AsTask(ct);
            return characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0
                ? characteristics.Characteristics[0]
                : null;
        }

        private async Task CleanupAsync()
        {
            try
            {
                if (_notifyCharacteristic != null)
                {
                    _notifyCharacteristic.ValueChanged -= OnNotifyValueChanged;
                    await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cleanup error: {Message}", ex.Message);
            }
            finally
            {
                _serialService?.Dispose();
                _device?.Dispose();
                _serialService = null;
                _device = null;
                _isOpen = false;
                ClearBuffer();
            }
        }

        private void OnNotifyValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
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
    }
}
