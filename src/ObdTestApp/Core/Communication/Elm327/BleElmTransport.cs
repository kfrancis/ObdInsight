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
        private static readonly Guid SerialServiceUuid = new("0000fff0-0000-1000-8000-00805f9b34fb");
        private static readonly Guid WriteCharacteristicUuid = new("0000fff2-0000-1000-8000-00805f9b34fb");
        private static readonly Guid NotifyCharacteristicUuid = new("0000fff1-0000-1000-8000-00805f9b34fb");

        private readonly SemaphoreSlim _bufferLock = new(1, 1);
        private readonly string _deviceId;
        private readonly Queue<byte> _receiveBuffer = new();
        private BluetoothLEDevice? _device;
        private bool _isOpen;
        private GattCharacteristic? _writeCharacteristic;
        private GattCharacteristic? _notifyCharacteristic;
        private GattDeviceService? _serialService;

        public bool EnableDebugLogging { get; set; }

        public BleElmTransport(string deviceId)
        {
            _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
        }

        public bool IsOpen => _isOpen;

        public async ValueTask DisposeAsync()
        {
            await CleanupAsync();
        }

        public void ClearBuffer()
        {
            _bufferLock.Wait();
            try { _receiveBuffer.Clear(); }
            finally { _bufferLock.Release(); }
        }

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public async ValueTask OpenAsync(CancellationToken ct)
        {
            if (_isOpen) return;

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(MAC802DOT3(_deviceId)).AsTask(ct);
            if (_device == null)
                throw new IOException("BLE device not found");

            var result = await _device.GetGattServicesForUuidAsync(SerialServiceUuid).AsTask(ct);
            if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
                throw new IOException("Serial service not found");

            _serialService = result.Services[0];

            _writeCharacteristic = await FindCharacteristicAsync(_serialService, WriteCharacteristicUuid, ct);
            _notifyCharacteristic = await FindCharacteristicAsync(_serialService, NotifyCharacteristicUuid, ct);
            if (_writeCharacteristic == null || _notifyCharacteristic == null)
                throw new IOException("Required characteristics not found");

            var status = await _notifyCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (status != GattCommunicationStatus.Success)
                throw new IOException("Failed to enable notifications");

            _notifyCharacteristic.ValueChanged += OnNotifyValueChanged;
            _isOpen = true;
        }

        private static async Task<GattCharacteristic?> FindCharacteristicAsync(GattDeviceService service, Guid uuid, CancellationToken ct)
        {
            var characteristics = await service.GetCharacteristicsForUuidAsync(uuid).AsTask(ct);
            return characteristics.Status == GattCommunicationStatus.Success && characteristics.Characteristics.Count > 0
                ? characteristics.Characteristics[0]
                : null;
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

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var timeout = TimeSpan.FromMilliseconds(250);
            var deadline = DateTime.UtcNow + timeout;

            while (_receiveBuffer.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10, ct);
            }

            _bufferLock.Wait();
            try
            {
                var count = Math.Min(buffer.Length, _receiveBuffer.Count);
                for (int i = 0; i < count; i++)
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

        public static ulong MAC802DOT3(string macAddress)
        {
            var hex = macAddress.Replace(":", "");
            return Convert.ToUInt64(hex, 16);
        }
    }
}
