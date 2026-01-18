namespace ObdTestApp;

/// <summary>
/// Defines the communication mode for interacting with an ECU.
/// These modes are mutually exclusive - the ELM327 adapter operates in one mode at a time.
/// </summary>
public enum EcuCommunicationMode
{
    /// <summary>
    /// Active request/response mode. Send queries and wait for responses.
    /// Used for Mode 21/22 diagnostic queries (BMS, Charger, etc.).
    /// The adapter responds with data and returns to prompt (">").
    /// </summary>
    RequestResponse,

    /// <summary>
    /// Passive monitoring mode. Listen to broadcast CAN frames without sending requests.
    /// Used for EV-CAN broadcast data (HVBAT, Inverter, etc.).
    /// Requires AT MA (Monitor All), AT MR (Monitor Receiver), or AT MT (Monitor Transmitter) commands.
    /// The adapter continuously streams CAN frames until interrupted.
    /// </summary>
    PassiveMonitoring
}
