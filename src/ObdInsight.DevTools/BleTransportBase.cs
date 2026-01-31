using ObdInsight.Core.Communication.Elm327;
using System.Collections.Concurrent;
using System.Text;

namespace ObdInsight.DevTools;

/// <summary>
/// Connection states for BLE transport
/// </summary>
public enum BleConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting
}

/// <summary>
/// Base class for BLE transports providing buffering and IElmTransport implementation.
/// DevTools-specific implementation - the main app may have a different architecture.
/// </summary>
public abstract class BleTransportBase : IElmTransport
{
    private readonly BlockingCollection<byte> _receiveBuffer = new();
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private volatile BleConnectionState _connectionState;

    protected BleTransportBase(BleDeviceProfile profile)
    {
        Profile = profile;
    }

    public BleDeviceProfile Profile { get; }

    /// <summary>
    /// Gets the device address (MAC address).
    /// </summary>
    public string? DeviceAddress { get; protected set; }

    /// <summary>
    /// Gets whether the transport is connected.
    /// </summary>
    public abstract bool IsConnected { get; }

    /// <summary>
    /// IElmTransport.IsOpen maps to IsConnected.
    /// </summary>
    bool IElmTransport.IsOpen => IsConnected;

    /// <summary>
    /// Connect to the BLE device.
    /// </summary>
    public abstract Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from the BLE device.
    /// </summary>
    public abstract Task DisconnectAsync();

    /// <summary>
    /// Write data to the BLE characteristic (chunking handled by derived class).
    /// </summary>
    protected abstract Task WriteCharacteristicAsync(byte[] data, CancellationToken cancellationToken);

    /// <summary>
    /// Called by derived class when data is received via BLE notification.
    /// </summary>
    protected void OnDataReceived(byte[] data)
    {
        foreach (var b in data)
        {
            _receiveBuffer.Add(b);
        }
    }

    /// <summary>
    /// Clear the receive buffer.
    /// </summary>
    public void ClearBuffer()
    {
        while (_receiveBuffer.TryTake(out _)) { }
    }

    /// <summary>
    /// Set the connection state (for derived classes to report state changes).
    /// </summary>
    protected void SetConnectionState(BleConnectionState state)
    {
        _connectionState = state;
    }

    /// <summary>
    /// Get the current connection state.
    /// </summary>
    public BleConnectionState ConnectionState => _connectionState;

    #region IElmTransport Implementation

    ValueTask IElmTransport.OpenAsync(CancellationToken ct)
    {
        throw new NotSupportedException(
            "Use ConnectAsync(deviceAddress) instead. IElmTransport.OpenAsync is not supported for BLE - device address must be provided.");
    }

    async ValueTask<int> IElmTransport.ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (!IsConnected)
            return 0;

        await _readGate.WaitAsync(ct);
        try
        {
            int bytesRead = 0;

            // Read what's available up to buffer size
            while (bytesRead < buffer.Length && _receiveBuffer.TryTake(out var b, 0))
            {
                buffer.Span[bytesRead++] = b;
            }

            // If nothing was immediately available, wait for at least one byte
            if (bytesRead == 0 && _receiveBuffer.TryTake(out var firstByte, Timeout.Infinite, ct))
            {
                buffer.Span[0] = firstByte;
                bytesRead = 1;

                // Then grab any additional bytes that arrived
                while (bytesRead < buffer.Length && _receiveBuffer.TryTake(out var b, 0))
                {
                    buffer.Span[bytesRead++] = b;
                }
            }

            return bytesRead;
        }
        finally
        {
            _readGate.Release();
        }
    }

    async ValueTask IElmTransport.WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Transport is not connected");

        var array = data.ToArray();
        await WriteCharacteristicAsync(array, ct);
    }

    ValueTask IElmTransport.FlushAsync(CancellationToken ct)
    {
        // BLE characteristic writes are immediately sent - no buffering to flush
        return ValueTask.CompletedTask;
    }

    void IElmTransport.ClearBuffer()
    {
        ClearBuffer();
    }

    public virtual void Dispose()
    {
        _receiveBuffer.Dispose();
        _readGate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        Dispose();
    }

    #endregion
}
