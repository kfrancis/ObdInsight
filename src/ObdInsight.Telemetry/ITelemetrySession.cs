using ObdInsight.Core.Communication.Elm327;

namespace ObdInsight.Telemetry;

/// <summary>
///     The consumer telemetry facade (docs/TELEMETRY_SESSION_DESIGN.md): subscribe a signal
///     set across cadence tiers, receive per-tick sample batches, take one-shot snapshots.
///     Consumers never touch <c>ElmSession</c>/<c>CanMonitor</c> directly.
/// </summary>
public interface ITelemetrySession : IAsyncDisposable
{
    /// <summary>
    ///     Live per-signal availability for the subscribed set. Updated as data appears
    ///     (broadcast signals may flip Unknown → Available once the vehicle is driving).
    /// </summary>
    IReadOnlyDictionary<TelemetrySignal, SignalAvailability> Availability { get; }

    /// <summary>
    ///     Current link state, when a resilient transport was wired in
    ///     (<see cref="IConnectionStateSource" />); null otherwise.
    /// </summary>
    ConnectionState? ConnectionState { get; }

    /// <summary>
    ///     Probes signal availability and starts the cadence scheduler. Idempotent.
    /// </summary>
    ValueTask StartAsync(CancellationToken ct = default);

    /// <summary>Stops the scheduler. Safe to restart.</summary>
    ValueTask StopAsync(CancellationToken ct = default);

    /// <summary>
    ///     Streams sample batches. Each caller gets an independent bounded buffer; slow
    ///     consumers drop oldest batches. Registration happens when this is called, not on first
    ///     enumeration, so batches produced before the consumer starts iterating are buffered
    ///     rather than lost.
    /// </summary>
    IAsyncEnumerable<TelemetrySampleBatch> Batches(CancellationToken ct = default);

    /// <summary>
    ///     Streams one signal as its own type: <c>Stream(Signals.StateOfCharge)</c> yields
    ///     <c>TelemetrySample&lt;decimal&gt;</c>, <c>Stream(Signals.CellVoltages)</c> yields
    ///     <c>TelemetrySample&lt;IReadOnlyList&lt;decimal&gt;&gt;</c> — no enum switch and no
    ///     unpacking of <see cref="TelemetryValue" /> at the call site.
    /// </summary>
    /// <remarks>
    ///     Ticks where the signal has no value are skipped, so every emission carries a real
    ///     value; <see cref="Availability" /> says whether a quiet signal is merely cold or
    ///     genuinely unsupported. Buffering, drop-oldest behaviour and eager registration match
    ///     <see cref="Batches" /> — this is a projection of the same subscription.
    /// </remarks>
    /// <param name="signal">A typed handle from <see cref="Signals" />.</param>
    /// <param name="ct">Stops the stream.</param>
    IAsyncEnumerable<TelemetrySample<T>> Stream<T>(TelemetrySignal<T> signal, CancellationToken ct = default);

    /// <summary>Raised for every produced batch (UI-binding convenience).</summary>
    event EventHandler<TelemetrySampleBatch>? BatchAvailable;

    /// <summary>
    ///     One-shot full diagnostic snapshot (pre-/post-check), independent of the
    ///     subscription. Serialized against the scheduler — safe to call while running.
    /// </summary>
    ValueTask<TelemetrySnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    ///     Re-exposed link-state transitions for UI binding (B10). Never fires
    ///     when no state source was wired.
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
}
