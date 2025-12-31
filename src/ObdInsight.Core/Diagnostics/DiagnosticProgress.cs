namespace ObdInsight.Core.Diagnostics;

/// <summary>
/// Progress information for diagnostic data collection.
/// Reports detailed status including current phase, sub-operation, and progress.
/// </summary>
public record DiagnosticProgress
{
    /// <summary>
    /// Current collection phase
    /// </summary>
    public required DiagnosticPhase Phase { get; init; }

    /// <summary>
    /// Human-readable description of current operation
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Progress within current phase (0.0 to 1.0)
    /// </summary>
    public double PhaseProgress { get; init; }

    /// <summary>
    /// Overall progress across all phases (0.0 to 1.0)
    /// </summary>
    public double OverallProgress { get; init; }

    /// <summary>
    /// Current item being processed (e.g., PID command)
    /// </summary>
    public string? CurrentItem { get; init; }

    /// <summary>
    /// Items completed in current phase
    /// </summary>
    public int ItemsCompleted { get; init; }

    /// <summary>
    /// Total items in current phase
    /// </summary>
    public int ItemsTotal { get; init; }

    /// <summary>
    /// Last response received (for live logging)
    /// </summary>
    public string? LastResponse { get; init; }

    /// <summary>
    /// Whether the last operation was successful
    /// </summary>
    public bool? LastOperationSuccess { get; init; }

    /// <summary>
    /// Error message if current operation failed
    /// </summary>
    public string? Error { get; init; }
}

/// <summary>
/// Phases of diagnostic data collection
/// </summary>
public enum DiagnosticPhase
{
    /// <summary>Not started yet</summary>
    NotStarted,

    /// <summary>Collecting BLE adapter information</summary>
    BleDiscovery,

    /// <summary>Connecting to the device</summary>
    Connecting,

    /// <summary>Initializing OBD adapter</summary>
    AdapterInit,

    /// <summary>Collecting OBD adapter AT command info</summary>
    AdapterInfo,

    /// <summary>Reading vehicle identification</summary>
    VehicleId,

    /// <summary>Querying supported PIDs</summary>
    SupportedPids,

    /// <summary>Probing standard PIDs</summary>
    StandardPidProbe,

    /// <summary>Probing extended/EV PIDs</summary>
    ExtendedPidProbe,

    /// <summary>Generating report</summary>
    GeneratingReport,

    /// <summary>Collection complete</summary>
    Complete,

    /// <summary>Collection failed</summary>
    Failed
}
