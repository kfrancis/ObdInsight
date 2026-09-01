namespace ObdInsight.DevTools;

/// <summary>
///     Interface for binary BLE transport used for proprietary protocols.
///     Handles raw binary framing without ASCII/ELM327 layer.
/// </summary>
public interface IBinaryBleTransport : IAsyncDisposable
{
    /// <summary>
    ///     Gets whether the transport is connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    ///     Gets the device MAC address.
    /// </summary>
    string DeviceAddress { get; }

    /// <summary>
    ///     Gets the current connection state.
    /// </summary>
    BleConnectionState ConnectionState { get; }

    /// <summary>
    ///     Event raised when data is received.
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>
    ///     Event raised when connection state changes.
    /// </summary>
    event EventHandler<BleConnectionState>? ConnectionStateChanged;

    /// <summary>
    ///     Connect to a BLE device.
    /// </summary>
    Task<bool> ConnectAsync(string deviceAddress, CancellationToken ct = default);

    /// <summary>
    ///     Disconnect from the BLE device.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    ///     Write binary data to the device.
    /// </summary>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    ///     Write raw binary data to the device.
    /// </summary>
    Task WriteRawAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>
    ///     Send a command and wait for response.
    /// </summary>
    Task<byte[]> SendCommandAsync(ReadOnlyMemory<byte> command, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    ///     Read binary data from the device (blocking until data available or timeout).
    /// </summary>
    Task<byte[]?> ReadAsync(TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    ///     Read available data from the device.
    /// </summary>
    Task<byte[]> ReadAvailableAsync(TimeSpan timeout, CancellationToken ct = default);
}
