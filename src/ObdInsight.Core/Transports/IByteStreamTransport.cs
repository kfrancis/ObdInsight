namespace ObdInsight.Core.Transports;

/// <summary>
/// Generic byte-stream transport interface for any communication channel (BLE, WiFi, Serial, USB).
/// Provides low-level byte transfer without understanding OBD protocols.
/// </summary>
public interface IByteStreamTransport : IDisposable
{
    event EventHandler<string>? DataReceived;
    event EventHandler<string>? DataSent;
    bool IsConnected { get; }
    string Name { get; }
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<string> ReadUntilAsync(string terminator, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task WriteAsync(string data, CancellationToken cancellationToken = default);
    Task<byte[]> ReadBytesAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task WriteBytesAsync(byte[] data, CancellationToken cancellationToken = default);
}
