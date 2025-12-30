namespace ObdInsight.Core.Transports.Tracing;

/// <summary>
/// Interface for recording transport I/O operations.
/// </summary>
/// <remarks>
/// Tracers capture all data sent and received through a transport
/// for later replay in deterministic tests.
/// </remarks>
public interface ITransportTracer : IDisposable
{
    /// <summary>
    /// Whether tracing is currently active
    /// </summary>
    bool IsRecording { get; }

    /// <summary>
    /// The current session being recorded, null if not recording
    /// </summary>
    TransportSession? CurrentSession { get; }

    /// <summary>
    /// Start recording a new trace session
    /// </summary>
    /// <param name="metadata">Initial metadata for the session</param>
    void StartRecording(TraceSessionMetadata? metadata = null);

    /// <summary>
    /// Stop recording and finalize the session
    /// </summary>
    /// <returns>The completed session</returns>
    TransportSession StopRecording();

    /// <summary>
    /// Record a transmitted (Tx) payload
    /// </summary>
    /// <param name="payload">Data sent to transport</param>
    void RecordTx(string payload);

    /// <summary>
    /// Record a received (Rx) payload
    /// </summary>
    /// <param name="payload">Data received from transport</param>
    void RecordRx(string payload);

    /// <summary>
    /// Update session metadata (e.g., after detecting protocol)
    /// </summary>
    /// <param name="updater">Function to update metadata</param>
    void UpdateMetadata(Func<TraceSessionMetadata, TraceSessionMetadata> updater);

    /// <summary>
    /// Event raised when a trace entry is recorded
    /// </summary>
    event EventHandler<TraceEntry>? EntryRecorded;
}

/// <summary>
/// Interface for serializing and deserializing transport sessions.
/// </summary>
public interface ITransportSessionSerializer
{
    /// <summary>
    /// Save a session to a file in JSONL format
    /// </summary>
    /// <param name="session">The session to save</param>
    /// <param name="filePath">Path to save to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveAsync(TransportSession session, string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Save a session to a stream in JSONL format
    /// </summary>
    /// <param name="session">The session to save</param>
    /// <param name="stream">Stream to write to</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveAsync(TransportSession session, Stream stream, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load a session from a JSONL file
    /// </summary>
    /// <param name="filePath">Path to load from</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The loaded session</returns>
    Task<TransportSession> LoadAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load a session from a stream
    /// </summary>
    /// <param name="stream">Stream to read from</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The loaded session</returns>
    Task<TransportSession> LoadAsync(Stream stream, CancellationToken cancellationToken = default);
}