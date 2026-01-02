namespace ObdInsight.Core.Transports.Ble;

/// <summary>
/// Binary protocol transport for BLE OBD adapters (e.g., Veepeak binary service 6287).
/// Uses raw binary framing instead of ASCII ELM327 protocol.
/// </summary>
/// <remarks>
/// Binary protocol typically provides:
/// - Lower latency (no ASCII encoding/parsing overhead)
/// - Direct CAN frame access
/// - Faster multi-PID requests
///
/// Command format varies by adapter - use probing to discover the protocol.
/// </remarks>
public interface IBinaryBleTransport : IDisposable
{
    /// <summary>
    /// Event raised when connection state changes
    /// </summary>
    event EventHandler<BleConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Event raised when binary data is received
    /// </summary>
    event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>
    /// Current connection state
    /// </summary>
    BleConnectionState ConnectionState { get; }

    /// <summary>
    /// The device address currently connected to
    /// </summary>
    string DeviceAddress { get; }

    /// <summary>
    /// Whether the transport is connected
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Clear any pending data in the receive buffer
    /// </summary>
    void ClearReceiveBuffer();

    /// <summary>
    /// Connect to the device using binary protocol
    /// </summary>
    /// <param name="deviceAddress">MAC address of the device</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if connected successfully</returns>
    Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from the device
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Read any available data from the receive buffer
    /// </summary>
    /// <param name="timeout">How long to wait for data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Received bytes, or empty if timeout</returns>
    Task<byte[]> ReadAvailableAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a raw binary command and wait for response
    /// </summary>
    /// <param name="command">Raw command bytes</param>
    /// <param name="timeout">Response timeout</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response bytes</returns>
    Task<byte[]> SendCommandAsync(ReadOnlyMemory<byte> command, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send raw bytes without waiting for response
    /// </summary>
    /// <param name="data">Data to write</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task WriteRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
}