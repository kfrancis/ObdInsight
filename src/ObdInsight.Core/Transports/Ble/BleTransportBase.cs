using System.Text;

namespace ObdInsight.Core.Transports.Ble;

/// <summary>
/// Base class for BLE transport implementations.
/// Provides common buffering and response handling logic.
/// Platform-specific implementations derive from this.
/// </summary>
/// <remarks>
/// This base class handles:
/// - Receive buffer management (thread-safe)
/// - Write chunking for BLE MTU limits
/// - Connection state management
/// - Event raising
///
/// Platform implementations (Windows, Android, iOS) must implement:
/// - ConnectAsync (platform-specific BLE connection)
/// - DisconnectAsync
/// - WriteCharacteristicAsync
/// </remarks>
public abstract class BleTransportBase : IBleTransport
{
    private readonly Lock _bufferLock = new();
    private readonly StringBuilder _receiveBuffer = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Creates a new BLE transport with the given profile
    /// </summary>
    /// <param name="profile">BLE device profile defining service/characteristic UUIDs</param>
    protected BleTransportBase(BleDeviceProfile profile)
    {
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <inheritdoc />
    public event EventHandler<BleConnectionState>? ConnectionStateChanged;

    /// <inheritdoc />
    public event EventHandler<string>? DataReceived;

    /// <inheritdoc />
    public event EventHandler<string>? DataSent;

    /// <inheritdoc />
    public BleConnectionState ConnectionState { get; protected set; } = BleConnectionState.Disconnected;

    /// <inheritdoc />
    public string DeviceAddress { get; protected set; } = string.Empty;

    /// <inheritdoc />
    public abstract bool IsConnected { get; }

    /// <inheritdoc />
    public string Name => Profile.Name;

    /// <inheritdoc />
    public Guid ServiceUuid => Profile.ServiceUuid;

    /// <summary>
    /// The BLE device profile being used
    /// </summary>
    protected BleDeviceProfile Profile { get; }

    /// <inheritdoc />
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(DeviceAddress))
        {
            throw new InvalidOperationException("Device address not set. Use ConnectAsync(string deviceAddress) instead.");
        }
        return ConnectAsync(DeviceAddress, cancellationToken);
    }

    /// <inheritdoc />
    public abstract Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public abstract Task DisconnectAsync();

    /// <inheritdoc />
    public virtual void Dispose()
    {
        _writeLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return await ReadUntilAsync("\r", timeout, cancellationToken);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

                // Longer delay between chunks to avoid overwhelming the BLE adapter
                // Some ELM327 clones need more time between writes
                if (offset < bytes.Length)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }

            DataSent?.Invoke(this, data);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadBytesAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var result = new byte[count];
        var offset = 0;

        while (offset < count && !linkedCts.Token.IsCancellationRequested)
        {
            lock (_bufferLock)
            {
                var bufferContent = _receiveBuffer.ToString();
                var available = Math.Min(count - offset, bufferContent.Length);
                if (available > 0)
                {
                    var bytes = Encoding.ASCII.GetBytes(bufferContent[..available]);
                    Array.Copy(bytes, 0, result, offset, bytes.Length);
                    _receiveBuffer.Remove(0, available);
                    offset += available;
                }
            }

            if (offset < count)
            {
                try
                {
                    await Task.Delay(10, linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timeout reading {count} bytes");
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task WriteBytesAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var chunkSize = Math.Min(Profile.MaxWriteSize, data.Length - offset);
                var chunk = new byte[chunkSize];
                Array.Copy(data, offset, chunk, 0, chunkSize);

                await WriteCharacteristicAsync(chunk, cancellationToken);
                offset += chunkSize;

                if (offset < data.Length)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }

            DataSent?.Invoke(this, Encoding.ASCII.GetString(data));
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
    /// <param name="data">The raw bytes received</param>
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
    /// <param name="state">The new connection state</param>
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
    /// <param name="data">Bytes to write to the characteristic</param>
    /// <param name="cancellationToken">Cancellation token</param>
    protected abstract Task WriteCharacteristicAsync(byte[] data, CancellationToken cancellationToken);

    private static string EscapeForDisplay(string s) =>
        s.Replace("\r", "\\r").Replace("\n", "\\n").Replace(">", ">");
}