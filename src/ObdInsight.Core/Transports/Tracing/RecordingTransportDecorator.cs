namespace ObdInsight.Core.Transports.Tracing;

/// <summary>
/// Decorator that wraps an <see cref="IObdTransport"/> to record all I/O operations.
/// </summary>
/// <remarks>
/// Use this decorator to record real device sessions for later replay in tests.
/// All transport operations pass through to the inner transport while being traced.
/// </remarks>
public sealed class RecordingTransportDecorator : IObdTransport
{
    private readonly IObdTransport _inner;
    private readonly ITransportTracer _tracer;
    private bool _disposed;

    /// <summary>
    /// Creates a recording decorator around an existing transport.
    /// </summary>
    /// <param name="inner">The transport to wrap</param>
    /// <param name="tracer">The tracer to record operations</param>
    public RecordingTransportDecorator(IObdTransport inner, ITransportTracer tracer)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _tracer = tracer ?? throw new ArgumentNullException(nameof(tracer));
    }

    /// <summary>
    /// The underlying transport being decorated
    /// </summary>
    public IObdTransport InnerTransport => _inner;

    /// <summary>
    /// The tracer recording operations
    /// </summary>
    public ITransportTracer Tracer => _tracer;

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public bool IsConnected => _inner.IsConnected;

    /// <inheritdoc />
    public event EventHandler<string>? DataReceived
    {
        add => _inner.DataReceived += value;
        remove => _inner.DataReceived -= value;
    }

    /// <inheritdoc />
    public event EventHandler<string>? DataSent
    {
        add => _inner.DataSent += value;
        remove => _inner.DataSent -= value;
    }

    /// <summary>
    /// Start recording a trace session
    /// </summary>
    /// <param name="metadata">Optional metadata for the session</param>
    public void StartRecording(TraceSessionMetadata? metadata = null)
    {
        var sessionMetadata = metadata ?? new TraceSessionMetadata
        {
            StartedAt = DateTimeOffset.UtcNow,
            TransportType = _inner.GetType().Name,
            DeviceName = _inner.Name
        };

        _tracer.StartRecording(sessionMetadata);
    }

    /// <summary>
    /// Stop recording and get the completed session
    /// </summary>
    /// <returns>The recorded session</returns>
    public TransportSession StopRecording()
    {
        return _tracer.StopRecording();
    }

    /// <inheritdoc />
    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var result = await _inner.ConnectAsync(cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync()
    {
        await _inner.DisconnectAsync();
    }

    /// <inheritdoc />
    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        _tracer.RecordTx(data);
        await _inner.WriteAsync(data, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var result = await _inner.ReadLineAsync(timeout, cancellationToken);
        _tracer.RecordRx(result);
        return result;
    }

    /// <inheritdoc />
    public async Task<string> ReadUntilAsync(string terminator, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var result = await _inner.ReadUntilAsync(terminator, timeout, cancellationToken);
        _tracer.RecordRx(result);
        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _tracer.Dispose();
        _inner.Dispose();
    }
}

/// <summary>
/// Extension methods for adding recording to transports.
/// </summary>
public static class RecordingTransportExtensions
{
    /// <summary>
    /// Wrap a transport with recording capability.
    /// </summary>
    /// <param name="transport">The transport to wrap</param>
    /// <returns>A decorated transport that records all I/O</returns>
    public static RecordingTransportDecorator WithRecording(this IObdTransport transport)
    {
        return new RecordingTransportDecorator(transport, new TransportTracer());
    }

    /// <summary>
    /// Wrap a transport with recording capability using a custom tracer.
    /// </summary>
    /// <param name="transport">The transport to wrap</param>
    /// <param name="tracer">The tracer to use for recording</param>
    /// <returns>A decorated transport that records all I/O</returns>
    public static RecordingTransportDecorator WithRecording(this IObdTransport transport, ITransportTracer tracer)
    {
        return new RecordingTransportDecorator(transport, tracer);
    }
}