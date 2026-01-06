using System.Text;

namespace ObdInsight.Core.Transports.Tracing;

/// <summary>
/// How to match transmitted commands to recorded entries.
/// </summary>
public enum ReplayMatchingMode
{
    /// <summary>Commands must match exactly (after normalization)</summary>
    Exact,

    /// <summary>Commands must start with recorded prefix</summary>
    Prefix,

    /// <summary>Accept any command and return next recorded response</summary>
    Any
}

/// <summary>
/// A transport that replays recorded sessions for deterministic testing.
/// </summary>
/// <remarks>
/// ReplayTransport allows testing adapter and service logic without requiring
/// physical hardware. It feeds pre-recorded Rx data when the expected Tx commands
/// are sent, enabling fully deterministic unit tests.
///
/// Matching modes:
/// - Exact: Tx must match exactly
/// - Prefix: Tx must start with recorded command
/// - Any: Accept any Tx and return next Rx in sequence
/// </remarks>
public sealed class ReplayTransport : IObdTransport
{
    private readonly Lock _lock = new();
    private readonly ReplayOptions _options;
    private readonly StringBuilder _rxBuffer = new();
    private readonly Queue<TraceEntry> _rxQueue;
    private readonly TransportSession _session;
    private readonly Queue<TraceEntry> _txQueue;
    private bool _connected;
    private int _currentIndex;
    private bool _disposed;

    /// <summary>
    /// Creates a replay transport from a recorded session.
    /// </summary>
    /// <param name="session">The session to replay</param>
    /// <param name="options">Replay options</param>
    public ReplayTransport(TransportSession session, ReplayOptions? options = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _options = options ?? new ReplayOptions();

        // Separate entries into Tx and Rx queues for matching
        _txQueue = new Queue<TraceEntry>(_session.Entries.Where(e => e.Direction == TraceDirection.Tx));
        _rxQueue = new Queue<TraceEntry>(_session.Entries.Where(e => e.Direction == TraceDirection.Rx));
    }

    /// <inheritdoc />
    public event EventHandler<string>? DataReceived;

    /// <inheritdoc />
    public event EventHandler<string>? DataSent;

    /// <summary>
    /// Current position in the replay
    /// </summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>
    /// Whether replay has completed all entries
    /// </summary>
    public bool IsComplete => _currentIndex >= _session.Entries.Count;

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public string Name => $"Replay:{_session.Metadata.DeviceName ?? "Unknown"}";

    /// <summary>
    /// The session being replayed
    /// </summary>
    public TransportSession Session => _session;

    /// <summary>
    /// List of Tx commands that didn't match expected sequence
    /// </summary>
    public List<string> UnmatchedCommands { get; } = [];

    /// <inheritdoc />
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _connected = true;
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task DisconnectAsync()
    {
        _connected = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _connected = false;
    }

    /// <inheritdoc />
    public async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return await ReadUntilAsync("\r", timeout, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> ReadUntilAsync(string terminator, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
            throw new InvalidOperationException("Transport not connected.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        while (!cts.Token.IsCancellationRequested)
        {
            string currentBuffer;
            lock (_lock)
            {
                currentBuffer = _rxBuffer.ToString();
            }

            var terminatorIndex = currentBuffer.IndexOf(terminator, StringComparison.Ordinal);
            if (terminatorIndex >= 0)
            {
                var result = currentBuffer[..(terminatorIndex + terminator.Length)];
                lock (_lock)
                {
                    _rxBuffer.Remove(0, terminatorIndex + terminator.Length);
                }

                DataReceived?.Invoke(this, result);
                return result;
            }

            // Check if we have more Rx data to process
            if (!TryDequeueNextRx())
            {
                // No more data - simulate timeout if strict, otherwise return what we have
                if (_options.StrictMode)
                {
                    await Task.Delay(10, cts.Token);
                }
                else if (!string.IsNullOrEmpty(currentBuffer))
                {
                    // Return whatever is in buffer even without terminator
                    lock (_lock)
                    {
                        _rxBuffer.Clear();
                    }
                    DataReceived?.Invoke(this, currentBuffer);
                    return currentBuffer;
                }
                else
                {
                    throw new TimeoutException($"No more recorded data available. Waiting for terminator: '{EscapeForDisplay(terminator)}'");
                }
            }
            else
            {
                // Simulate timing if enabled
                if (_options.SimulateTiming)
                {
                    await Task.Delay(10, cts.Token);
                }
            }
        }

        throw new TimeoutException($"Timeout waiting for terminator: '{EscapeForDisplay(terminator)}'");
    }

    /// <summary>
    /// Reset the replay to the beginning
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _currentIndex = 0;
            _rxBuffer.Clear();
            _txQueue.Clear();
            _rxQueue.Clear();
            UnmatchedCommands.Clear();

            foreach (var entry in _session.Entries.Where(e => e.Direction == TraceDirection.Tx))
                _txQueue.Enqueue(entry);
            foreach (var entry in _session.Entries.Where(e => e.Direction == TraceDirection.Rx))
                _rxQueue.Enqueue(entry);
        }
    }

    /// <inheritdoc />
    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
            throw new InvalidOperationException("Transport not connected.");

        DataSent?.Invoke(this, data);

        // Find matching Rx response(s) and queue them
        await QueueResponsesForCommandAsync(data, cancellationToken);
    }

    /// <inheritdoc />
    public Task<byte[]> ReadBytesAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected)
            throw new InvalidOperationException("Transport not connected.");

        var buffer = new byte[count];
        lock (_lock)
        {
            var str = _rxBuffer.ToString();
            var available = Math.Min(count, str.Length);
            var bytes = Encoding.ASCII.GetBytes(str[..available]);
            Array.Copy(bytes, buffer, available);
            _rxBuffer.Remove(0, available);
        }
        return Task.FromResult(buffer);
    }

    /// <inheritdoc />
    public Task WriteBytesAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        var str = Encoding.ASCII.GetString(data);
        return WriteAsync(str, cancellationToken);
    }

    private static string EscapeForDisplay(string s) =>
        s.Replace("\r", "\\r").Replace("\n", "\\n").Replace(">", ">");

    private static string NormalizeCommand(string command)
    {
        return command.Trim().TrimEnd('\r', '\n');
    }

    private Task QueueResponsesForCommandAsync(string command, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            // Find the next Tx entry and match it
            if (_txQueue.Count > 0)
            {
                var expectedTx = _txQueue.Peek();
                var matches = _options.MatchingMode switch
                {
                    ReplayMatchingMode.Exact => NormalizeCommand(command) == NormalizeCommand(expectedTx.Payload),
                    ReplayMatchingMode.Prefix => NormalizeCommand(command).StartsWith(NormalizeCommand(expectedTx.Payload), StringComparison.OrdinalIgnoreCase),
                    ReplayMatchingMode.Any => true,
                    _ => true
                };

                if (matches)
                {
                    _txQueue.Dequeue();
                    _currentIndex++;
                }
                else if (_options.StrictMode)
                {
                    UnmatchedCommands.Add(command);
                    throw new InvalidOperationException(
                        $"Command mismatch. Expected: '{EscapeForDisplay(expectedTx.Payload)}', Got: '{EscapeForDisplay(command)}'");
                }
                else
                {
                    UnmatchedCommands.Add(command);
                }
            }
        }

        return Task.CompletedTask;
    }

    private bool TryDequeueNextRx()
    {
        lock (_lock)
        {
            if (_rxQueue.Count == 0)
                return false;

            var entry = _rxQueue.Dequeue();
            _rxBuffer.Append(entry.Payload);
            _currentIndex++;
            return true;
        }
    }
}

/// <summary>
/// Options for replay transport behavior.
/// </summary>
public sealed record ReplayOptions
{
    /// <summary>
    /// How to match Tx commands to recorded entries
    /// </summary>
    public ReplayMatchingMode MatchingMode { get; init; } = ReplayMatchingMode.Exact;

    /// <summary>
    /// Whether to throw on command mismatches (true) or log and continue (false)
    /// </summary>
    public bool StrictMode { get; init; } = false;

    /// <summary>
    /// Whether to simulate original timing between responses
    /// </summary>
    public bool SimulateTiming { get; init; } = false;

    /// <summary>
    /// Scale factor for timing simulation (1.0 = real time, 0.5 = half speed)
    /// </summary>
    public double TimingScale { get; init; } = 1.0;
}

/// <summary>
/// Factory for creating replay transports from files.
/// </summary>
public static class ReplayTransportFactory
{
    private static readonly JsonLTransportSessionSerializer s_serializer = new();

    /// <summary>
    /// Create a replay transport from a JSONL trace file.
    /// </summary>
    /// <param name="filePath">Path to the trace file</param>
    /// <param name="options">Replay options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A configured replay transport</returns>
    public static async Task<ReplayTransport> FromFileAsync(
        string filePath,
        ReplayOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var session = await s_serializer.LoadAsync(filePath, cancellationToken);
        return new ReplayTransport(session, options);
    }

    /// <summary>
    /// Create a replay transport from embedded resource.
    /// </summary>
    /// <param name="assembly">Assembly containing the resource</param>
    /// <param name="resourceName">Name of the embedded resource</param>
    /// <param name="options">Replay options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A configured replay transport</returns>
    public static async Task<ReplayTransport> FromResourceAsync(
        System.Reflection.Assembly assembly,
        string resourceName,
        ReplayOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' not found in assembly.");

        return await FromStreamAsync(stream, options, cancellationToken);
    }

    /// <summary>
    /// Create a replay transport from a stream.
    /// </summary>
    /// <param name="stream">Stream containing JSONL trace data</param>
    /// <param name="options">Replay options</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A configured replay transport</returns>
    public static async Task<ReplayTransport> FromStreamAsync(
        Stream stream,
        ReplayOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var session = await s_serializer.LoadAsync(stream, cancellationToken);
        return new ReplayTransport(session, options);
    }
}