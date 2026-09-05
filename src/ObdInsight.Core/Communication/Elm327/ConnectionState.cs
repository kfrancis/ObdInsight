namespace ObdInsight.Core.Communication.Elm327;

/// <summary>Connection lifecycle state. VehicleConnection reports diagnostic readiness, not merely an open link.</summary>
public enum ConnectionState
{
    /// <summary>Initial open in progress (or not yet opened).</summary>
    Connecting,

    /// <summary>Connected; for VehicleConnection, ELM initialization and vehicle detection have succeeded.</summary>
    Connected,

    /// <summary>Connection dropped; the previous generation ended and a fresh graph is being established.</summary>
    Reconnecting,

    /// <summary>Connection ended, recovery exhausted, or owner disposed.</summary>
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
