namespace ObdTestApp.Core.Protocols;

/// <summary>
/// Defines the communication mode for interacting with an ECU.
/// These modes determine how data is acquired from the vehicle.
/// </summary>
public enum EcuCommunicationMode
{
    /// <summary>
    /// Active request/response mode using UDS/ISO-TP diagnostic requests.
    /// Send query, receive response, adapter returns to prompt.
    /// Used for: Mode 21/22 queries (BMS, Charger VIN, etc.)
    /// </summary>
    RequestResponse,

    /// <summary>
    /// Passive monitoring of unsolicited broadcast frames.
    /// Uses AT MA (Monitor All) or AT MR (Monitor Receiver).
    /// Used for: True broadcast frames that appear without any requests (e.g., 0x1DB battery status)
    /// WARNING: Not all "broadcast" frames are truly unsolicited - some require session/wake.
    /// </summary>
    PassiveMonitoring,

    /// <summary>
    /// Active broadcast monitoring - sends session activation or keep-alive,
    /// then monitors for broadcast responses.
    /// Used for: Wake-dependent broadcast frames (modules that sleep until activated)
    /// </summary>
    ActiveMonitoring,

    /// <summary>
    /// Filtered single-ID monitoring using AT MR xxx.
    /// More reliable than AT MA for specific frames, prevents buffer overflow.
    /// </summary>
    FilteredMonitoring
}
