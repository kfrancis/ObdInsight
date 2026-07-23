namespace ObdInsight.Telemetry;

/// <summary>
///     Raw ATMA CAN-frame capture: every frame the adapter sees on the bus, timestamped,
///     independent of the decoded-signal <see cref="ITelemetrySession" /> path.
/// </summary>
/// <remarks>
///     Mutually exclusive with <see cref="ITelemetrySession" /> (and with any other
///     query/monitor activity) on the same underlying adapter connection: the ELM327 can only
///     be in request/response mode or monitoring mode at once, so a caller must pick one of
///     "normal telemetry" or "raw monitor mode" per connection, not run both concurrently.
///     Start this only when no <see cref="ITelemetrySession" /> is running against the same
///     session, and vice versa.
/// </remarks>
public interface IRawCanMonitor : IAsyncDisposable
{
    /// <summary>Whether the monitoring loop is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>Enters ATMA monitoring mode and starts the read loop. Idempotent while running.</summary>
    ValueTask StartAsync(CancellationToken ct = default);

    /// <summary>Stops the read loop and returns the adapter to request/response mode.</summary>
    ValueTask StopAsync(CancellationToken ct = default);

    /// <summary>
    ///     Streams every captured frame with its receive timestamp. The stream completes when
    ///     monitoring ends (<see cref="StopAsync" />, adapter disconnect, or an unrecoverable
    ///     adapter error). Multiple concurrent callers each get an independent buffered stream.
    /// </summary>
    IAsyncEnumerable<RawCanFrame> MonitorRawFramesAsync(CancellationToken ct = default);
}
