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
    /// Completion of the current/most recent run (already complete before the first start).
    /// Capture after StartAsync. Intentional stop completes normally; producer failure faults
    /// this task and all run streams, including unexpected cancellation from a provider.
    /// A subsequent start creates a new completion task and requires new subscriptions.
    /// </summary>
    Task Completion { get; }

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
    ///     Probes signal availability and starts the cadence scheduler. Concurrent calls
    ///     share startup. The initiating token cancels probing, not the running scheduler;
    ///     other callers cancel only their wait. Restart while stopping is rejected.
    /// </summary>
    ValueTask StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Requests stop and joins the producer. Cancellation cancels only this wait;
    /// await a subsequent StopAsync before restarting. Producer errors are reported by
    /// Completion and streams, not rethrown by StopAsync or DisposeAsync.
    /// </summary>
    ValueTask StopAsync(CancellationToken ct = default);

    /// <summary>
    ///     Streams sample batches. Each caller gets an independent bounded buffer; slow
    ///     consumers drop oldest batches. Registration happens when this is called, not on first
    ///     enumeration, so batches produced before the consumer starts iterating are buffered
    ///     rather than lost. Buffered batches drain before termination or failure.
    ///     Subscribing after termination observes that terminal outcome; start a new run first
    ///     to subscribe to it. Streams registered before the first start belong to that run.
    /// </summary>
    IAsyncEnumerable<TelemetrySampleBatch> Batches(CancellationToken ct = default);

    /// <summary>
    ///     Streams one signal as its own type: <c>Stream(Signals.StateOfCharge)</c> yields
    ///     <c>TelemetrySample&lt;decimal&gt;</c>, <c>Stream(Signals.CellVoltages)</c> yields
    ///     <c>TelemetrySample&lt;IReadOnlyList&lt;decimal?&gt;&gt;</c> — no enum switch and no
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

    /// <summary>
    /// Raised synchronously on the producer (no UI dispatch). Exceptions from individual
    /// handlers are logged and isolated. Handlers must be short and must not synchronously
    /// wait for StopAsync or DisposeAsync. Use streams for asynchronous processing.
    /// </summary>
    event EventHandler<TelemetrySampleBatch>? BatchAvailable;

    /// <summary>
    ///     One-shot full diagnostic snapshot (pre-/post-check), independent of the
    ///     subscription. Serialized against the scheduler — safe to call while running.
    /// </summary>
    ValueTask<TelemetrySnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    ///     Re-exposed link-state transitions for UI binding (B10). Never fires
    ///     when no state source was wired. Handler exceptions are isolated and logged;
    ///     callbacks run synchronously without UI dispatch, as with BatchAvailable.
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
}
