using Spectre.Console;
using System.Buffers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace ObdTestApp
{
    /// <summary>
    /// Provides a Bluetooth transport implementation for communicating with ELM327 OBD-II devices using the Serial Port
    /// Profile (SPP).
    /// </summary>
    /// <remarks>Use the static discovery methods to locate compatible Bluetooth devices before creating an
    /// instance of this class. The transport must be opened with OpenAsync before use, and disposed asynchronously when
    /// no longer needed. This class is not thread-safe; callers should ensure that operations are not performed
    /// concurrently.</remarks>
    public sealed class BtElmTransport : IElmTransport
    {
        private readonly string _deviceId;
        private bool _isOpen;
        private DataReader? _reader;
        private StreamSocket? _socket;
        private DataWriter? _writer;

        /// <summary>
        /// Create a Bluetooth ELM transport for a specific device ID.
        /// Use DiscoverElm327DevicesAsync to find available devices first.
        /// </summary>
        public BtElmTransport(string deviceId)
        {
            _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
        }

        public bool IsOpen => _isOpen;

        /// <summary>
        /// Alternative discovery method that returns all Bluetooth SPP devices.
        /// Filter by name manually if needed.
        /// </summary>
        public static async Task<(string DeviceId, string Name)[]> DiscoverAllBluetoothSppDevicesAsync(
            CancellationToken ct = default)
        {
            var selector = RfcommDeviceService.GetDeviceSelector(RfcommServiceId.SerialPort);
            var devices = await DeviceInformation.FindAllAsync(selector).AsTask(ct);

            return [.. devices.Select(d => (d.Id, d.Name))];
        }

        /// <summary>
        /// Discover available ELM327 Bluetooth devices.
        /// Returns device ID and name pairs.
        /// </summary>
        public static async Task<(string DeviceId, string Name)[]> DiscoverElm327DevicesAsync(
            CancellationToken ct = default)
        {
            // Query for Bluetooth devices that support RFCOMM (Serial Port Profile)
            var selector = RfcommDeviceService.GetDeviceSelector(RfcommServiceId.SerialPort);
            var devices = await DeviceInformation.FindAllAsync(selector).AsTask(ct);

            return [.. devices
                .Where(d => d.Name.Contains("OBD", StringComparison.OrdinalIgnoreCase) ||
                           d.Name.Contains("ELM", StringComparison.OrdinalIgnoreCase) ||
                           d.Name.Contains("327", StringComparison.OrdinalIgnoreCase) ||
                           d.Name.Contains("OBDII", StringComparison.OrdinalIgnoreCase))
                .Select(d => (d.Id, d.Name))];
        }

        public async ValueTask DisposeAsync()
        {
            await CleanupAsync();
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            if (_writer != null && _isOpen)
            {
                try
                {
                    await _writer.FlushAsync().AsTask(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new IOException($"Bluetooth flush error: {ex.Message}", ex);
                }
            }
        }

        public async ValueTask OpenAsync(CancellationToken ct)
        {
            if (_isOpen)
                return;

            try
            {
                // Get the Bluetooth device
                var device = await BluetoothDevice.FromIdAsync(_deviceId).AsTask(ct) ?? throw new IOException($"Bluetooth device not found: {_deviceId}");

                // Get RFCOMM services
                var servicesResult = await device.GetRfcommServicesAsync(
                    BluetoothCacheMode.Uncached).AsTask(ct);

                if (servicesResult.Error != BluetoothError.Success)
                    throw new IOException($"Failed to get RFCOMM services: {servicesResult.Error}");

                // Find the Serial Port Profile service
                var sppService = servicesResult.Services
                    .FirstOrDefault(s => s.ServiceId.Uuid == RfcommServiceId.SerialPort.Uuid) ?? throw new IOException("Device does not support Serial Port Profile (SPP)");

                // Create socket and connect
                _socket = new StreamSocket();

                // Configure socket for reliable streaming
                _socket.Control.KeepAlive = true;

                await _socket.ConnectAsync(
                    sppService.ConnectionHostName,
                    sppService.ConnectionServiceName).AsTask(ct);

                // Set up reader/writer with proper buffer sizes
                _writer = new DataWriter(_socket.OutputStream)
                {
                    UnicodeEncoding = UnicodeEncoding.Utf8
                };

                _reader = new DataReader(_socket.InputStream)
                {
                    InputStreamOptions = InputStreamOptions.Partial,
                    UnicodeEncoding = UnicodeEncoding.Utf8,
                    ByteOrder = ByteOrder.LittleEndian
                };

                _isOpen = true;
            }
            catch (Exception ex)
            {
                // Clean up on failure
                await CleanupAsync();
                throw new IOException($"Failed to connect to Bluetooth device: {ex.Message}", ex);
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (!_isOpen || _reader == null)
                throw new InvalidOperationException("Transport is not open");

            try
            {
                // Load at least 1 byte, up to buffer size (Partial mode allows returning less)
                var loaded = await _reader.LoadAsync((uint)buffer.Length).AsTask(ct);

                if (loaded == 0)
                    return 0;

                // Read the loaded bytes into our buffer
                var actualBytes = Math.Min((int)loaded, buffer.Length);
                var readBuffer = new byte[actualBytes];
                _reader.ReadBytes(readBuffer);
                readBuffer.CopyTo(buffer);

                return actualBytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new IOException($"Bluetooth read error: {ex.Message}", ex);
            }
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!_isOpen || _writer == null)
                throw new InvalidOperationException("Transport is not open");

            try
            {
                _writer.WriteBytes(data.ToArray());
                await _writer.StoreAsync().AsTask(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new IOException($"Bluetooth write error: {ex.Message}", ex);
            }
        }

        private async ValueTask CleanupAsync()
        {
            _isOpen = false;

            if (_writer != null)
            {
                try
                {
                    await _writer.FlushAsync();
                    _writer.DetachStream();
                }
                catch { /* Best effort */ }
                finally
                {
                    _writer.Dispose();
                    _writer = null;
                }
            }

            if (_reader != null)
            {
                try
                {
                    _reader.DetachStream();
                }
                catch { /* Best effort */ }
                finally
                {
                    _reader.Dispose();
                    _reader = null;
                }
            }

            if (_socket != null)
            {
                try
                {
                    _socket.Dispose();
                }
                catch { /* Best effort */ }
                finally
                {
                    _socket = null;
                }
            }
        }
    }
}