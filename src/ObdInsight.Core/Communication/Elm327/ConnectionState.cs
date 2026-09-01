namespace ObdInsight.Core.Communication.Elm327;

/// <summary>Link state of a resilient transport (docs/RESILIENCE_DESIGN.md).</summary>
public enum ConnectionState
{
    /// <summary>Initial open in progress (or not yet opened).</summary>
    Connecting,

    /// <summary>Link up; I/O flows.</summary>
    Connected,

    /// <summary>Link dropped; automatic reconnection in progress. I/O blocks.</summary>
    Reconnecting,

    /// <summary>Reconnection exhausted; I/O throws until an explicit re-open.</summary>
    Lost
}

public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(ConnectionState oldState, ConnectionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }

    public ConnectionState OldState { get; }
    public ConnectionState NewState { get; }
}

/// <summary>
///     Bindable connection-state signal for UI (re-exposed by the telemetry session).
///     Transitions fire in order from a single supervisor — no concurrent duplicates.
/// </summary>
public interface IConnectionStateSource
{
    ConnectionState State { get; }

    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
}
