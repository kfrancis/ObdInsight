using System.Text.Json.Serialization;

namespace ObdInsight.Core.Transports.Tracing;

/// <summary>
/// Direction of data transfer in a trace entry.
/// </summary>
public enum TraceDirection
{
    /// <summary>Data sent to the transport (transmitted)</summary>
    Tx,

    /// <summary>Data received from the transport</summary>
    Rx
}

/// <summary>
/// A single trace entry representing one data transfer event.
/// </summary>
/// <remarks>
/// Entries are immutable records suitable for JSONL serialization.
/// Each entry captures the exact payload and timing for replay.
/// </remarks>
public sealed record TraceEntry
{
    /// <summary>
    /// Timestamp of the event (UTC)
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Direction of data transfer (Tx or Rx)
    /// </summary>
    public required TraceDirection Direction { get; init; }

    /// <summary>
    /// The actual data payload transferred
    /// </summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Time elapsed since session start (for replay timing)
    /// </summary>
    public TimeSpan ElapsedTime { get; init; }

    /// <summary>
    /// Optional sequence number for ordering
    /// </summary>
    public int SequenceNumber { get; init; }
}

/// <summary>
/// Metadata about the transport session being traced.
/// </summary>
public sealed record TraceSessionMetadata
{
    /// <summary>
    /// When the trace session started (UTC)
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the trace session ended (UTC), null if still recording
    /// </summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>
    /// Transport type used (e.g., "WindowsBleTransport", "ReplayTransport")
    /// </summary>
    public string? TransportType { get; init; }

    /// <summary>
    /// Device identifier (e.g., MAC address for BLE)
    /// </summary>
    public string? DeviceAddress { get; init; }

    /// <summary>
    /// Device name if available
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Protocol detected or configured (e.g., "AUTO, ISO 15765-4 CAN")
    /// </summary>
    public string? Protocol { get; init; }

    /// <summary>
    /// Whether headers are enabled in responses
    /// </summary>
    public bool? HeadersEnabled { get; init; }

    /// <summary>
    /// Whether echo is enabled
    /// </summary>
    public bool? EchoEnabled { get; init; }

    /// <summary>
    /// Adapter version string if detected
    /// </summary>
    public string? AdapterVersion { get; init; }

    /// <summary>
    /// Vehicle VIN if detected (masked for privacy)
    /// </summary>
    public string? VehicleVin { get; init; }

    /// <summary>
    /// User-provided description of the session
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Version of the trace format
    /// </summary>
    public string TraceVersion { get; init; } = "1.0";

    /// <summary>
    /// Additional custom metadata
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object>? ExtensionData { get; init; }
}

/// <summary>
/// Complete transport trace session containing metadata and all entries.
/// </summary>
/// <remarks>
/// A session represents a complete recording of transport I/O operations.
/// Sessions can be saved to JSONL files and replayed for deterministic testing.
/// </remarks>
public sealed record TransportSession
{
    /// <summary>
    /// Unique identifier for this session
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Session metadata
    /// </summary>
    public required TraceSessionMetadata Metadata { get; init; }

    /// <summary>
    /// All trace entries in order
    /// </summary>
    public required IReadOnlyList<TraceEntry> Entries { get; init; }

    /// <summary>
    /// Total number of entries
    /// </summary>
    [JsonIgnore]
    public int EntryCount => Entries.Count;

    /// <summary>
    /// Total bytes transmitted
    /// </summary>
    [JsonIgnore]
    public long TotalBytesTx => Entries
        .Where(e => e.Direction == TraceDirection.Tx)
        .Sum(e => e.Payload.Length);

    /// <summary>
    /// Total bytes received
    /// </summary>
    [JsonIgnore]
    public long TotalBytesRx => Entries
        .Where(e => e.Direction == TraceDirection.Rx)
        .Sum(e => e.Payload.Length);

    /// <summary>
    /// Session duration
    /// </summary>
    [JsonIgnore]
    public TimeSpan Duration => Metadata.EndedAt.HasValue
        ? Metadata.EndedAt.Value - Metadata.StartedAt
        : Entries.Count > 0
            ? Entries[^1].ElapsedTime
            : TimeSpan.Zero;
}