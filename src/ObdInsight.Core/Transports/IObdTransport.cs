namespace ObdInsight.Core.Transports;

/// <summary>
/// Core transport interface for OBD communication (BLE, WiFi, Serial, etc.).
/// Transports handle the low-level byte transfer without understanding OBD protocols.
/// </summary>
/// <remarks>
/// The transport layer is responsible for:
/// - Physical connection management (connect/disconnect)
/// - Raw data transmission (write bytes/strings)
/// - Raw data reception (read lines/terminated strings)
/// - Connection state tracking
///
/// Transports do NOT interpret OBD commands - that's the adapter's job.
/// This separation allows testing adapters with mock transports.
/// </remarks>
public interface IObdTransport : IDisposable
{
    /// <summary>
    /// Event raised when data is received
    /// </summary>
    event EventHandler<string>? DataReceived;

    /// <summary>
    /// Event raised when data is sent
    /// </summary>
    event EventHandler<string>? DataSent;

    /// <summary>
    /// Whether the transport is currently connected
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Display name for this transport
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Connect to the remote device
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if connection succeeded</returns>
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from the remote device
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Read a single line (CR terminated) from the transport
    /// </summary>
    /// <param name="timeout">Maximum time to wait for data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The line read (including terminator)</returns>
    Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read until a specific terminator string is received
    /// </summary>
    /// <param name="terminator">String to wait for</param>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All data read up to and including the terminator</returns>
    Task<string> ReadUntilAsync(string terminator, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write data to the transport
    /// </summary>
    /// <param name="data">String data to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task WriteAsync(string data, CancellationToken cancellationToken = default);
}