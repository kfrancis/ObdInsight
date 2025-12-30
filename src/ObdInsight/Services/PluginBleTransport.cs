using System.Text;
using ObdInsight.Core;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

namespace ObdInsight.Services;

/// <summary>
/// Plugin.BLE-based BLE transport implementation for OBD communication.
/// </summary>
public sealed class PluginBleTransport : IBleTransport
{
    private readonly IAdapter _adapter;
    private readonly BleDeviceProfile _profile;
    private readonly StringBuilder _receiveBuffer = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private IDevice? _device;
    private IService? _service;
    private ICharacteristic? _writeCharacteristic;
    private ICharacteristic? _notifyCharacteristic;
    private bool _disposed;

    public PluginBleTransport(IAdapter adapter, BleDeviceProfile profile)
    {
        _adapter = adapter;
        _profile = profile;
    }

    /// <inheritdoc/>
    public string Name => _profile.Name;

    /// <inheritdoc/>
    public string DeviceAddress => _device?.Id.ToString() ?? string.Empty;

    /// <inheritdoc/>
    public Guid ServiceUuid => _profile.ServiceUuid;

    /// <inheritdoc/>
    public bool IsConnected => _device?.State == DeviceState.Connected;

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
    public event EventHandler<string>? DataReceived;

    /// <inheritdoc/>
    public event EventHandler<string>? DataSent;

    /// <inheritdoc/>
    public event EventHandler<BleConnectionState>? ConnectionStateChanged;

    /// <inheritdoc/>
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Use ConnectAsync(deviceAddress) for BLE transport.");
    }

    /// <inheritdoc/>
    public async Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default)
    {
        try
        {
            ConnectionStateChanged?.Invoke(this, BleConnectionState.Connecting);

            // Parse device ID
            if (!Guid.TryParse(deviceAddress, out var deviceId))
            {
                // Try to find device by scanning briefly
                _device = await FindDeviceByAddressAsync(deviceAddress, cancellationToken);
            }
            else
            {
                // Connect using known device ID
                _device = await _adapter.ConnectToKnownDeviceAsync(deviceId, 
                    cancellationToken: cancellationToken);
            }

            if (_device is null)
            {
                ConnectionStateChanged?.Invoke(this, BleConnectionState.Disconnected);
                return false;
            }

            // Subscribe to connection state changes
            _device.UpdateConnectionInterval(ConnectionInterval.High);

            // Get the OBD service
            _service = await _device.GetServiceAsync(_profile.ServiceUuid, cancellationToken);
            if (_service is null)
            {
                await DisconnectAsync();
                return false;
            }

            // Get characteristics
            _writeCharacteristic = await _service.GetCharacteristicAsync(_profile.WriteCharacteristicUuid);
            _notifyCharacteristic = await _service.GetCharacteristicAsync(_profile.NotifyCharacteristicUuid);

            if (_writeCharacteristic is null || _notifyCharacteristic is null)
            {
                await DisconnectAsync();
                return false;
            }

            // Subscribe to notifications
            if (_notifyCharacteristic.CanUpdate)
            {
                _notifyCharacteristic.ValueUpdated += OnCharacteristicValueUpdated;
                await _notifyCharacteristic.StartUpdatesAsync(cancellationToken);
            }

            ConnectionStateChanged?.Invoke(this, BleConnectionState.Connected);
            return true;
        }
        catch (Exception)
        {
            ConnectionStateChanged?.Invoke(this, BleConnectionState.Disconnected);
            throw;
        }
    }

    private async Task<IDevice?> FindDeviceByAddressAsync(string address, CancellationToken cancellationToken)
    {
        IDevice? foundDevice = null;
        var tcs = new TaskCompletionSource<IDevice?>();

        void OnDeviceDiscovered(object? sender, DeviceEventArgs e)
        {
            if (e.Device.Id.ToString().Equals(address, StringComparison.OrdinalIgnoreCase) ||
                e.Device.Name?.Equals(address, StringComparison.OrdinalIgnoreCase) == true)
            {
                foundDevice = e.Device;
                tcs.TrySetResult(e.Device);
            }
        }

        _adapter.DeviceDiscovered += OnDeviceDiscovered;

        try
        {
            // Start a brief scan
            var scanTask = _adapter.StartScanningForDevicesAsync(cancellationToken: cancellationToken);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            await _adapter.StopScanningForDevicesAsync();

            if (foundDevice is not null)
            {
                await _adapter.ConnectToDeviceAsync(foundDevice, cancellationToken: cancellationToken);
            }

            return foundDevice;
        }
        finally
        {
            _adapter.DeviceDiscovered -= OnDeviceDiscovered;
        }
    }

    /// <inheritdoc/>
    public async Task DisconnectAsync()
    {
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

        ConnectionStateChanged?.Invoke(this, BleConnectionState.Disconnected);
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

            // Split into chunks if needed
            var maxSize = _profile.MaxWriteSize;
            for (int i = 0; i < bytes.Length; i += maxSize)
            {
                var chunk = bytes.Skip(i).Take(maxSize).ToArray();

                if (_profile.WriteWithResponse)
                {
                    await _writeCharacteristic.WriteAsync(chunk, cancellationToken);
                }
                else
                {
                    await _writeCharacteristic.WriteAsync(chunk, cancellationToken);
                }
            }

            DataSent?.Invoke(this, data);
        }
        finally
        {
            _writeLock.Release();
        }
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

    private void OnCharacteristicValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        var data = Encoding.ASCII.GetString(e.Characteristic.Value);

        lock (_receiveBuffer)
        {
            _receiveBuffer.Append(data);
        }

        DataReceived?.Invoke(this, data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DisconnectAsync().GetAwaiter().GetResult();
        _writeLock.Dispose();
    }
}
