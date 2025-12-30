using System.Text;

namespace ObdInsight.Core;

/// <summary>
/// Base class for BLE transport implementations.
/// Provides common buffering and response handling logic.
/// Platform-specific implementations derive from this.
/// </summary>
public abstract class BleTransportBase : IBleTransport
{
    private readonly Lock _bufferLock = new();
    private readonly StringBuilder _receiveBuffer = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    protected BleTransportBase(BleDeviceProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public event EventHandler<BleConnectionState>? ConnectionStateChanged;

    public event EventHandler<string>? DataReceived;

    public event EventHandler<string>? DataSent;

    public BleConnectionState ConnectionState { get; protected set; } = BleConnectionState.Disconnected;
    public string DeviceAddress { get; protected set; } = string.Empty;
    public abstract bool IsConnected { get; }
    public string Name => Profile.Name;
    public Guid ServiceUuid => Profile.ServiceUuid;
    protected BleDeviceProfile Profile { get; }

    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(DeviceAddress))
        {
            throw new InvalidOperationException("Device address not set. Use ConnectAsync(string deviceAddress) instead.");
        }
        return ConnectAsync(DeviceAddress, cancellationToken);
    }

    public abstract Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default);

    public abstract Task DisconnectAsync();

    public virtual void Dispose()
    {
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return await ReadUntilAsync("\r", timeout, cancellationToken);
    }

    public async Task<string> ReadUntilAsync(string terminator, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        while (!linkedCts.Token.IsCancellationRequested)
        {
            string currentBuffer;
            lock (_bufferLock)
            {
                currentBuffer = _receiveBuffer.ToString();
            }

            var terminatorIndex = currentBuffer.IndexOf(terminator, StringComparison.Ordinal);
            if (terminatorIndex >= 0)
            {
                var result = currentBuffer[..(terminatorIndex + terminator.Length)];
                lock (_bufferLock)
                {
                    _receiveBuffer.Remove(0, terminatorIndex + terminator.Length);
                }
                return result;
            }

            try
            {
                await Task.Delay(10, linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"Timeout waiting for terminator '{EscapeForDisplay(terminator)}'");
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var bytes = Encoding.ASCII.GetBytes(data);

            // Split into chunks if needed (BLE has MTU limits)
            var offset = 0;
            while (offset < bytes.Length)
            {
                var chunkSize = Math.Min(Profile.MaxWriteSize, bytes.Length - offset);
                var chunk = new byte[chunkSize];
                Array.Copy(bytes, offset, chunk, 0, chunkSize);

                await WriteCharacteristicAsync(chunk, cancellationToken);
                offset += chunkSize;

                // Small delay between chunks to avoid overwhelming the device
                if (offset < bytes.Length)
                {
                    await Task.Delay(10, cancellationToken);
                }
            }

            DataSent?.Invoke(this, data);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Clear the receive buffer
    /// </summary>
    protected void ClearBuffer()
    {
        lock (_bufferLock)
        {
            _receiveBuffer.Clear();
        }
    }

    /// <summary>
    /// Called by platform implementations when data is received from BLE characteristic
    /// </summary>
    protected void OnDataReceived(byte[] data)
    {
        var text = Encoding.ASCII.GetString(data);

        lock (_bufferLock)
        {
            _receiveBuffer.Append(text);
        }

        DataReceived?.Invoke(this, text);
    }

    /// <summary>
    /// Update connection state and raise event
    /// </summary>
    protected void SetConnectionState(BleConnectionState state)
    {
        if (ConnectionState != state)
        {
            ConnectionState = state;
            ConnectionStateChanged?.Invoke(this, state);
        }
    }

    /// <summary>
    /// Platform-specific write implementation
    /// </summary>
    protected abstract Task WriteCharacteristicAsync(byte[] data, CancellationToken cancellationToken);

    private static string EscapeForDisplay(string s) =>
        s.Replace("\r", "\\r").Replace("\n", "\\n").Replace(">", ">");
}