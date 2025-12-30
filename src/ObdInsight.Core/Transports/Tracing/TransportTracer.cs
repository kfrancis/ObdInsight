using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObdInsight.Core.Transports.Tracing;

/// <summary>
/// Records transport I/O operations for replay testing.
/// </summary>
/// <remarks>
/// Thread-safe implementation that captures Tx/Rx data with precise timing.
/// </remarks>
public sealed class TransportTracer : ITransportTracer
{
    private readonly Lock _lock = new();
    private readonly List<TraceEntry> _entries = [];
    private readonly Stopwatch _stopwatch = new();
    private TraceSessionMetadata? _metadata;
    private int _sequenceNumber;
    private bool _disposed;

    /// <inheritdoc />
    public bool IsRecording { get; private set; }

    /// <inheritdoc />
    public TransportSession? CurrentSession
    {
        get
        {
            lock (_lock)
            {
                if (!IsRecording || _metadata == null)
                    return null;

                return new TransportSession
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    Metadata = _metadata,
                    Entries = [.. _entries]
                };
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<TraceEntry>? EntryRecorded;

    /// <inheritdoc />
    public void StartRecording(TraceSessionMetadata? metadata = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (IsRecording)
                throw new InvalidOperationException("Already recording. Stop current session first.");

            _metadata = metadata ?? new TraceSessionMetadata
            {
                StartedAt = DateTimeOffset.UtcNow
            };

            // Ensure StartedAt is set if not provided
            if (_metadata.StartedAt == default)
            {
                _metadata = _metadata with { StartedAt = DateTimeOffset.UtcNow };
            }

            _entries.Clear();
            _sequenceNumber = 0;
            _stopwatch.Restart();
            IsRecording = true;
        }
    }

    /// <inheritdoc />
    public TransportSession StopRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (!IsRecording)
                throw new InvalidOperationException("Not currently recording.");

            _stopwatch.Stop();
            IsRecording = false;

            var endedAt = DateTimeOffset.UtcNow;
            _metadata = _metadata! with { EndedAt = endedAt };

            return new TransportSession
            {
                SessionId = Guid.NewGuid().ToString("N"),
                Metadata = _metadata,
                Entries = [.. _entries]
            };
        }
    }

    /// <inheritdoc />
    public void RecordTx(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RecordEntry(TraceDirection.Tx, payload);
    }

    /// <inheritdoc />
    public void RecordRx(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        RecordEntry(TraceDirection.Rx, payload);
    }

    /// <inheritdoc />
    public void UpdateMetadata(Func<TraceSessionMetadata, TraceSessionMetadata> updater)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_metadata != null)
            {
                _metadata = updater(_metadata);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (IsRecording)
        {
            try
            {
                StopRecording();
            }
            catch
            {
                // Suppress exceptions during dispose
            }
        }
    }

    private void RecordEntry(TraceDirection direction, string payload)
    {
        if (!IsRecording)
            return;

        TraceEntry entry;
        lock (_lock)
        {
            if (!IsRecording)
                return;

            entry = new TraceEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Direction = direction,
                Payload = payload,
                ElapsedTime = _stopwatch.Elapsed,
                SequenceNumber = _sequenceNumber++
            };
            _entries.Add(entry);
        }

        EntryRecorded?.Invoke(this, entry);
    }
}

/// <summary>
/// Serializes and deserializes transport sessions using JSONL format.
/// </summary>
/// <remarks>
/// JSONL (JSON Lines) format: one JSON object per line.
/// First line is metadata, subsequent lines are trace entries.
/// This format supports streaming and is easy to inspect/edit.
/// </remarks>
public sealed class JsonLTransportSessionSerializer : ITransportSessionSerializer
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public async Task SaveAsync(TransportSession session, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(filePath);
        await SaveAsync(session, stream, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveAsync(TransportSession session, Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(stream);

        await using var writer = new StreamWriter(stream, leaveOpen: true);

        // Write header with session info
        var header = new SessionHeader
        {
            Type = "session",
            SessionId = session.SessionId,
            Metadata = session.Metadata
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(header, s_jsonOptions));

        // Write each entry on its own line
        foreach (var entry in session.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entryWrapper = new EntryWrapper
            {
                Type = "entry",
                Entry = entry
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(entryWrapper, s_jsonOptions));
        }

        await writer.FlushAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TransportSession> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Trace file not found.", filePath);

        await using var stream = File.OpenRead(filePath);
        return await LoadAsync(stream, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TransportSession> LoadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(stream, leaveOpen: true);
        var entries = new List<TraceEntry>();
        string? sessionId = null;
        TraceSessionMetadata? metadata = null;

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            // Try to determine line type
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeElement))
            {
                var type = typeElement.GetString();
                if (type == "session")
                {
                    var header = JsonSerializer.Deserialize<SessionHeader>(line, s_jsonOptions)
                        ?? throw new InvalidOperationException("Failed to parse session header.");
                    sessionId = header.SessionId;
                    metadata = header.Metadata;
                }
                else if (type == "entry")
                {
                    var wrapper = JsonSerializer.Deserialize<EntryWrapper>(line, s_jsonOptions)
                        ?? throw new InvalidOperationException("Failed to parse entry.");
                    if (wrapper.Entry != null)
                        entries.Add(wrapper.Entry);
                }
            }
            else
            {
                // Legacy or simple format - try to parse as entry
                var entry = JsonSerializer.Deserialize<TraceEntry>(line, s_jsonOptions);
                if (entry != null)
                    entries.Add(entry);
            }
        }

        if (metadata == null)
        {
            // Create default metadata if none found
            metadata = new TraceSessionMetadata
            {
                StartedAt = entries.Count > 0 ? entries[0].Timestamp : DateTimeOffset.UtcNow,
                EndedAt = entries.Count > 0 ? entries[^1].Timestamp : DateTimeOffset.UtcNow
            };
        }

        return new TransportSession
        {
            SessionId = sessionId ?? Guid.NewGuid().ToString("N"),
            Metadata = metadata,
            Entries = entries
        };
    }

    private sealed record SessionHeader
    {
        public string Type { get; init; } = "session";
        public string? SessionId { get; init; }
        public TraceSessionMetadata? Metadata { get; init; }
    }

    private sealed record EntryWrapper
    {
        public string Type { get; init; } = "entry";
        public TraceEntry? Entry { get; init; }
    }
}
