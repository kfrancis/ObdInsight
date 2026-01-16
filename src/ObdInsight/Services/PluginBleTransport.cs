using ObdInsight.Core.Transports.Ble;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using System.Diagnostics;
using System.Text;

namespace ObdInsight.Services;

/// <summary>
/// Plugin.BLE-based BLE transport implementation for OBD communication.
/// </summary>
public partial class PluginBleTransport : IBleTransport, IAsyncDisposable
{
    private readonly IAdapter _adapter;
    private readonly BleDeviceProfile _profile;
    private readonly StringBuilder _receiveBuffer = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private IDevice? _device;
    private bool _disposed;
    private ICharacteristic? _notifyCharacteristic;
    private IService? _service;
    private ICharacteristic? _writeCharacteristic;

    /// <summary>
    /// Number of retry attempts for service/characteristic discovery.
    /// </summary>
    private const int MaxDiscoveryRetries = 5;

    /// <summary>
    /// Delay between discovery retry attempts.
    /// </summary>
    private static readonly TimeSpan DiscoveryRetryDelay = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// Delay after initial connection before service discovery.
    /// Windows BLE needs time to stabilize after connect.
    /// </summary>
    private static readonly TimeSpan PostConnectDelay = TimeSpan.FromMilliseconds(1000);

    public PluginBleTransport(IAdapter adapter, BleDeviceProfile profile)
    {
        _adapter = adapter;
        _profile = profile;
    }

    /// <inheritdoc/>
    public event EventHandler<BleConnectionState>? ConnectionStateChanged;

    /// <inheritdoc/>
    public event EventHandler<string>? DataReceived;

    /// <inheritdoc/>
    public event EventHandler<string>? DataSent;

    /// <inheritdoc/>
    public BleConnectionState ConnectionState => _device?.State switch
    {
        DeviceState.Connected => BleConnectionState.Connected,
        DeviceState.Connecting => BleConnectionState.Connecting,
        DeviceState.Disconnected => BleConnectionState.Disconnected,
        DeviceState.Limited => BleConnectionState.Disconnected,
        _ => BleConnectionState.Disconnected
    };

    /// <inheritdoc/>
    public string DeviceAddress => _device?.Id.ToString() ?? string.Empty;

    /// <inheritdoc/>
    public bool IsConnected => _device?.State == DeviceState.Connected;

    /// <inheritdoc/>
    public string Name => _profile.Name;

    /// <inheritdoc/>
    public Guid ServiceUuid => _profile.ServiceUuid;

    /// <inheritdoc/>
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Use ConnectAsync(deviceAddress) for BLE transport.");
    }

    /// <inheritdoc/>
    public async Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        Log($"ConnectAsync started for {deviceAddress}");

        try
        {
            ConnectionStateChanged?.Invoke(this, BleConnectionState.Connecting);

            // Parse device ID and connect
            if (!Guid.TryParse(deviceAddress, out var deviceId))
            {
                Log($"Address is not a GUID, will scan for device");
                _device = await FindDeviceByAddressAsync(deviceAddress, cancellationToken);
            }
            else
            {
                Log($"Connecting to known device ID: {deviceId}");
                try
                {
                    _device = await _adapter.ConnectToKnownDeviceAsync(deviceId,
                        cancellationToken: cancellationToken);
                    Log($"ConnectToKnownDeviceAsync completed, device={_device?.Name ?? "null"}");
                }
                catch (Exception ex)
                {
                    Log($"ConnectToKnownDeviceAsync failed: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
            }

            if (_device is null)
            {
                Log("Device is null after connection attempt");
                ConnectionStateChanged?.Invoke(this, BleConnectionState.Disconnected);
                return false;
            }

            Log($"Connected to device: {_device.Name}, State: {_device.State}");

            // Set high connection interval for better throughput
            _device.UpdateConnectionInterval(ConnectionInterval.High);

            // Wait for Windows BLE to stabilize after connection
            Log($"Waiting {PostConnectDelay.TotalMilliseconds}ms for connection to stabilize...");
            await Task.Delay(PostConnectDelay, cancellationToken);

            // Get the OBD service with retry logic
            Log($"Looking for service: {_profile.ServiceUuid}");
            _service = await GetServiceWithRetryAsync(_profile.ServiceUuid, cancellationToken);
            if (_service is null)
            {
                Log("Service not found after retries");
                await DisconnectAsync();
                return false;
            }

            Log($"Service found: {_service.Id}");

            // Get characteristics with retry logic
            Log($"Looking for write characteristic: {_profile.WriteCharacteristicUuid}");
            _writeCharacteristic = await GetCharacteristicWithRetryAsync(_service, _profile.WriteCharacteristicUuid, cancellationToken);

            Log($"Looking for notify characteristic: {_profile.NotifyCharacteristicUuid}");
            _notifyCharacteristic = await GetCharacteristicWithRetryAsync(_service, _profile.NotifyCharacteristicUuid, cancellationToken);

            if (_writeCharacteristic is null)
            {
                Log("Write characteristic not found");
                await DisconnectAsync();
                return false;
            }

            if (_notifyCharacteristic is null)
            {
                Log("Notify characteristic not found");
                await DisconnectAsync();
                return false;
            }

            Log($"Write characteristic: {_writeCharacteristic.Id}, Props: {_writeCharacteristic.Properties}");
            Log($"Notify characteristic: {_notifyCharacteristic.Id}, Props: {_notifyCharacteristic.Properties}");

            // Subscribe to notifications with retry
            if (_notifyCharacteristic.CanUpdate)
            {
                _notifyCharacteristic.ValueUpdated += OnCharacteristicValueUpdated;

                for (int attempt = 0; attempt < MaxDiscoveryRetries; attempt++)
                {
                    try
                    {
                        Log($"Starting notifications (attempt {attempt + 1})...");
                        await _notifyCharacteristic.StartUpdatesAsync(cancellationToken);
                        Log("Notifications started successfully");
                        break;
                    }
                    catch (Exception ex) when (attempt < MaxDiscoveryRetries - 1)
                    {
                        Log($"StartUpdatesAsync attempt {attempt + 1} failed: {ex.Message}");
                        await Task.Delay(DiscoveryRetryDelay, cancellationToken);
                    }
                }
            }
            else
            {
                Log("Warning: Notify characteristic does not support updates");
            }

            sw.Stop();
            Log($"Connection completed successfully in {sw.ElapsedMilliseconds}ms");

            ConnectionStateChanged?.Invoke(this, BleConnectionState.Connected);
            return true;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            Log($"Connection cancelled after {sw.ElapsedMilliseconds}ms");
            ConnectionStateChanged?.Invoke(this, BleConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Log($"Connection failed after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
            ConnectionStateChanged?.Invoke(this, BleConnectionState.Disconnected);
            throw;
        }
    }

    /// <summary>
    /// Gets a service with retry logic to handle Windows BLE transient failures.
    /// </summary>
    private async Task<IService?> GetServiceWithRetryAsync(Guid serviceUuid, CancellationToken ct)
    {
        if (_device is null) return null;

        for (int attempt = 0; attempt < MaxDiscoveryRetries; attempt++)
        {
            try
            {
                Log($"GetServiceAsync attempt {attempt + 1}...");
                var service = await _device.GetServiceAsync(serviceUuid, ct);
                if (service is not null)
                {
                    Log($"Service found on attempt {attempt + 1}");
                    return service;
                }
                Log($"GetServiceAsync returned null on attempt {attempt + 1}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log($"GetServiceAsync attempt {attempt + 1}: Plugin.BLE internal timeout");
            }
            catch (Exception ex) when (attempt < MaxDiscoveryRetries - 1)
            {
                Log($"GetServiceAsync attempt {attempt + 1} failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (attempt < MaxDiscoveryRetries - 1)
            {
                Log($"Waiting {DiscoveryRetryDelay.TotalMilliseconds}ms before retry...");
                await Task.Delay(DiscoveryRetryDelay, ct);
            }
        }

        // Try listing all services to help debug
        try
        {
            Log("Listing all services for debugging...");
            var allServices = await _device.GetServicesAsync(ct);
            if (allServices?.Count > 0)
            {
                foreach (var s in allServices)
                {
                    Log($"  Available service: {s.Id}");
                }
            }
            else
            {
                Log("  No services found");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to list services: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Gets a characteristic with retry logic to handle Windows BLE transient failures.
    /// </summary>
    private async Task<ICharacteristic?> GetCharacteristicWithRetryAsync(IService service, Guid charUuid, CancellationToken ct)
    {
        for (int attempt = 0; attempt < MaxDiscoveryRetries; attempt++)
        {
            try
            {
                Log($"GetCharacteristicAsync attempt {attempt + 1} for {charUuid}...");
                var characteristic = await service.GetCharacteristicAsync(charUuid, ct);
                if (characteristic is not null)
                {
                    Log($"Characteristic found on attempt {attempt + 1}");
                    return characteristic;
                }
                Log($"GetCharacteristicAsync returned null on attempt {attempt + 1}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log($"GetCharacteristicAsync attempt {attempt + 1}: Plugin.BLE internal timeout");
            }
            catch (Exception ex) when (attempt < MaxDiscoveryRetries - 1)
            {
                Log($"GetCharacteristicAsync attempt {attempt + 1} failed: {ex.GetType().Name}: {ex.Message}");
            }

            if (attempt < MaxDiscoveryRetries - 1)
            {
                await Task.Delay(DiscoveryRetryDelay, ct);
            }
        }

        // Try listing all characteristics to help debug
        try
        {
            Log("Listing all characteristics for debugging...");
            var allChars = await service.GetCharacteristicsAsync(ct);
            if (allChars?.Count > 0)
            {
                foreach (var c in allChars)
                {
                    Log($"  Available characteristic: {c.Id}, Props: {c.Properties}");
                }
            }
            else
            {
                Log("  No characteristics found");
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to list characteristics: {ex.Message}");
        }

        return null;
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[PluginBLE] {message}");
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync()
    {
        Log("DisconnectAsync called");
        ConnectionStateChanged?.Invoke(this, BleConnectionState.Disconnecting);

        if (_notifyCharacteristic is not null)
        {
            _notifyCharacteristic.ValueUpdated -= OnCharacteristicValueUpdated;
            try
            {
                await _notifyCharacteristic.StopUpdatesAsync();
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        if (_device is not null)
        {
            try
            {
                await _adapter.DisconnectDeviceAsync(_device);
            }
            catch
            {
                // Ignore errors during cleanup
            }
        }

        _service = null;
        _writeCharacteristic = null;
        _notifyCharacteristic = null;
        _device = null;

        Log("DisconnectAsync completed");
        ConnectionStateChanged?.Invoke(this, BleConnectionState.Disconnected);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_notifyCharacteristic is not null)
            {
                _notifyCharacteristic.ValueUpdated -= OnCharacteristicValueUpdated;
            }

            _service = null;
            _writeCharacteristic = null;
            _notifyCharacteristic = null;
            _device = null;
        }
        catch
        {
            // Best effort cleanup
        }

        _writeLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await DisconnectAsync();
        }
        catch
        {
            // Best effort cleanup
        }

        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc/>
    public async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return await ReadUntilAsync("\r", timeout, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<string> ReadUntilAsync(string terminator, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            lock (_receiveBuffer)
            {
                var bufferContent = _receiveBuffer.ToString();
                var terminatorIndex = bufferContent.IndexOf(terminator, StringComparison.Ordinal);

                if (terminatorIndex >= 0)
                {
                    var result = bufferContent[..(terminatorIndex + terminator.Length)];
                    _receiveBuffer.Remove(0, terminatorIndex + terminator.Length);
                    return result;
                }
            }

            await Task.Delay(10, cts.Token);
        }

        throw new TimeoutException($"Timeout waiting for terminator '{terminator}'");
    }

    /// <inheritdoc/>
    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        if (_writeCharacteristic is null)
            throw new InvalidOperationException("Not connected.");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var bytes = Encoding.ASCII.GetBytes(data);

            var maxSize = _profile.MaxWriteSize;
            for (int i = 0; i < bytes.Length; i += maxSize)
            {
                var chunk = bytes.Skip(i).Take(maxSize).ToArray();

                await _writeCharacteristic.WriteAsync(chunk, cancellationToken);
            }

            DataSent?.Invoke(this, data);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<byte[]> ReadBytesAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var result = new List<byte>();

        while (result.Count < count && !cts.Token.IsCancellationRequested)
        {
            string currentBuffer;
            lock (_receiveBuffer)
            {
                currentBuffer = _receiveBuffer.ToString();
            }

            if (currentBuffer.Length > 0)
            {
                var bytesToRead = Math.Min(count - result.Count, currentBuffer.Length);
                var bytes = Encoding.ASCII.GetBytes(currentBuffer[..bytesToRead]);
                result.AddRange(bytes);

                lock (_receiveBuffer)
                {
                    _receiveBuffer.Remove(0, bytesToRead);
                }
            }
            else
            {
                await Task.Delay(10, cts.Token);
            }
        }

        if (result.Count == 0)
            throw new TimeoutException($"Timeout reading {count} bytes");

        return [.. result];
    }

    /// <inheritdoc/>
    public async Task WriteBytesAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (_writeCharacteristic is null)
            throw new InvalidOperationException("Not connected.");

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var maxSize = _profile.MaxWriteSize;
            for (int i = 0; i < data.Length; i += maxSize)
            {
                var chunk = data.Skip(i).Take(maxSize).ToArray();
                await _writeCharacteristic.WriteAsync(chunk, cancellationToken);
            }

            var stringData = Encoding.ASCII.GetString(data);
            DataSent?.Invoke(this, stringData);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<IDevice?> FindDeviceByAddressAsync(string address, CancellationToken cancellationToken)
    {
        Log($"FindDeviceByAddressAsync: scanning for {address}");
        IDevice? foundDevice = null;
        var tcs = new TaskCompletionSource<IDevice?>();

        void OnDeviceDiscovered(object? sender, DeviceEventArgs e)
        {
            var deviceIdMatch = e.Device.Id.ToString().Equals(address, StringComparison.OrdinalIgnoreCase);
            var nameMatch = e.Device.Name?.Equals(address, StringComparison.OrdinalIgnoreCase) == true;

            if (deviceIdMatch || nameMatch)
            {
                Log($"Found device: {e.Device.Name} ({e.Device.Id})");
                foundDevice = e.Device;
                tcs.TrySetResult(e.Device);
            }
        }

        _adapter.DeviceDiscovered += OnDeviceDiscovered;

        try
        {
            var scanTask = _adapter.StartScanningForDevicesAsync(cancellationToken: cancellationToken);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            await _adapter.StopScanningForDevicesAsync();

            if (foundDevice is not null)
            {
                Log($"Connecting to found device: {foundDevice.Name}");
                await _adapter.ConnectToDeviceAsync(foundDevice, cancellationToken: cancellationToken);
            }
            else
            {
                Log("Device not found during scan");
            }

            return foundDevice;
        }
        finally
        {
            _adapter.DeviceDiscovered -= OnDeviceDiscovered;
        }
    }

    private void OnCharacteristicValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        var data = Encoding.ASCII.GetString(e.Characteristic.Value);

        lock (_receiveBuffer)
        {
            _receiveBuffer.Append(data);
        }

        DataReceived?.Invoke(this, data);
    }

    public void DrainBuffer()
    {
        lock (_receiveBuffer)
        {
            _receiveBuffer.Clear();
        }
    }
}