using ObdInsight.Core.Communication.Elm327;

namespace ObdInsight.Telemetry;

/// <summary>
/// The consumer telemetry facade (docs/TELEMETRY_SESSION_DESIGN.md): subscribe a signal
/// set across cadence tiers, receive per-tick sample batches, take one-shot snapshots.
/// Consumers never touch <c>ElmSession</c>/<c>CanMonitor</c> directly.
/// </summary>
public interface ITelemetrySession : IAsyncDisposable
{
    /// <summary>
    /// Probes signal availability and starts the cadence scheduler. Idempotent.
    /// </summary>
    ValueTask StartAsync(CancellationToken ct = default);

    /// <summary>Stops the scheduler. Safe to restart.</summary>
    ValueTask StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Live per-signal availability for the subscribed set. Updated as data appears
    /// (broadcast signals may flip Unknown → Available once the vehicle is driving).
    /// </summary>
    IReadOnlyDictionary<TelemetrySignal, SignalAvailability> Availability { get; }

    /// <summary>
    /// Streams sample batches. Each caller gets an independent bounded buffer; slow
    /// consumers drop oldest batches.
    /// </summary>
    IAsyncEnumerable<TelemetrySampleBatch> Batches(CancellationToken ct = default);

    /// <summary>Raised for every produced batch (UI-binding convenience).</summary>
    event EventHandler<TelemetrySampleBatch>? BatchAvailable;

    /// <summary>
    /// One-shot full diagnostic snapshot (pre-/post-check), independent of the
    /// subscription. Serialized against the scheduler — safe to call while running.
    /// </summary>
    ValueTask<TelemetrySnapshot> GetSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// Current link state, when a resilient transport was wired in
    /// (<see cref="IConnectionStateSource"/>); null otherwise.
    /// </summary>
    ConnectionState? ConnectionState { get; }

    /// <summary>Re-exposed link-state transitions for UI binding (B10). Never fires
    /// when no state source was wired.</summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;
}
