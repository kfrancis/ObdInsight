namespace ObdInsight.Telemetry;

/// <summary>
///     Vehicle-side adapter that reads one batch of related signals (typically one capability
///     call). Batch-shaped so one underlying query serves several signals (e.g. BMS Group 01
///     carries SOC, pack V/A/kW, and SoH in a single UDS exchange).
/// </summary>
public interface ITelemetryProvider
{
    /// <summary>Signals this provider can produce.</summary>
    IReadOnlyCollection<TelemetrySignal> Signals { get; }

    /// <summary>
    ///     True when a read is served from the shared monitor cache (no adapter round-trip
    ///     once warm). Cache-only reads are bounded by
    ///     <see cref="TelemetrySessionOptions.CacheReadTimeout" /> so a cold cache cannot
    ///     stall a cadence tier.
    /// </summary>
    bool IsCacheOnly { get; }

    /// <summary>
    ///     Reads the requested subset of <see cref="Signals" />. Data absence yields
    ///     <see cref="TelemetryValue.Empty" /> entries — implementations must not throw for
    ///     missing data; cancellation propagates as <see cref="OperationCanceledException" />.
    ///     Query TimeoutException is treated as missing data. IOException and unexpected
    ///     exceptions terminate a telemetry run (and propagate from snapshots). An
    ///     OperationCanceledException without cancellation of the supplied token is a
    ///     provider failure, not a timeout. Providers must cooperate with cancellation;
    ///     stop/disposal joins outstanding reads rather than abandoning hardware work.
    /// </summary>
    ValueTask<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>> ReadAsync(
        IReadOnlySet<TelemetrySignal> requested, CancellationToken ct);
}
