namespace ObdInsight.Core.Communication.Elm327;

/// <summary>
///     A transport that can proactively signal link loss (BLE disconnect, socket close)
///     instead of leaving callers to infer it from timed-out reads. The B10 resilience
///     supervisor subscribes to this to trigger reconnection promptly.
/// </summary>
public interface IConnectionAwareTransport : IElmTransport
{
    /// <summary>Raised once when the underlying link drops after a successful open.</summary>
    event EventHandler? ConnectionLost;
}
