using ObdInsight.Core.Transports.Ble;

namespace ObdInsight.Services;

/// <summary>
/// Singleton service that manages the shared OBD device connection state.
/// </summary>
public sealed class ConnectedDeviceService : IConnectedDeviceService, IDisposable
{
    private readonly object _lock = new();
    private IBleTransport? _transport;
    private bool _disposed;

    /// <inheritdoc />
    public IBleTransport? Transport
    {
        get
        {
            lock (_lock)
            {
                return _transport;
            }
        }
    }

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _transport?.IsConnected == true;
            }
        }
    }

    /// <inheritdoc />
    public string? DeviceName { get; private set; }

    /// <inheritdoc />
    public string? DeviceAddress { get; private set; }

    /// <inheritdoc />
    public BleDeviceProfile? DeviceProfile { get; private set; }

    /// <inheritdoc />
    public event EventHandler<DeviceConnectionChangedEventArgs>? ConnectionChanged;

    /// <inheritdoc />
    public void SetConnectedDevice(IBleTransport transport, string deviceName, string deviceAddress, BleDeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(deviceName);
        ArgumentNullException.ThrowIfNull(deviceAddress);
        ArgumentNullException.ThrowIfNull(profile);

        lock (_lock)
        {
            // Dispose old transport if exists
            if (_transport is not null && _transport != transport)
            {
                _transport.ConnectionStateChanged -= OnTransportConnectionStateChanged;
                _transport.Dispose();
            }

            _transport = transport;
            DeviceName = deviceName;
            DeviceAddress = deviceAddress;
            DeviceProfile = profile;

            // Subscribe to connection state changes from transport
            _transport.ConnectionStateChanged += OnTransportConnectionStateChanged;
        }

        RaiseConnectionChanged(true, deviceName, deviceAddress);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        IBleTransport? transportToDispose;

        lock (_lock)
        {
            transportToDispose = _transport;
            _transport = null;
            DeviceName = null;
            DeviceAddress = null;
            DeviceProfile = null;
        }

        if (transportToDispose is not null)
        {
            transportToDispose.ConnectionStateChanged -= OnTransportConnectionStateChanged;

            try
            {
                await transportToDispose.DisconnectAsync();
            }
            catch
            {
                // Ignore disconnect errors
            }

            transportToDispose.Dispose();
        }

        RaiseConnectionChanged(false, null, null);
    }

    private void OnTransportConnectionStateChanged(object? sender, BleConnectionState state)
    {
        if (state == BleConnectionState.Disconnected)
        {
            // Transport disconnected externally
            string? name, address;
            lock (_lock)
            {
                name = DeviceName;
                address = DeviceAddress;

                if (_transport is not null)
                {
                    _transport.ConnectionStateChanged -= OnTransportConnectionStateChanged;
                    _transport = null;
                }

                DeviceName = null;
                DeviceAddress = null;
                DeviceProfile = null;
            }

            RaiseConnectionChanged(false, name, address);
        }
    }

    private void RaiseConnectionChanged(bool isConnected, string? deviceName, string? deviceAddress)
    {
        ConnectionChanged?.Invoke(this, new DeviceConnectionChangedEventArgs
        {
            IsConnected = isConnected,
            DeviceName = deviceName,
            DeviceAddress = deviceAddress
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            if (_transport is not null)
            {
                _transport.ConnectionStateChanged -= OnTransportConnectionStateChanged;
                _transport.Dispose();
                _transport = null;
            }
        }
    }
}
