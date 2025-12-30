namespace ObdInsight.Core.Diagnostics;

/// <summary>
/// Complete diagnostic report for a vehicle/adapter combination.
/// Used to gather all information needed to add support for new vehicles or OBD adapters.
/// </summary>
public record DiagnosticReport
{
    /// <summary>
    /// Timestamp when the report was generated
    /// </summary>
    public required DateTime GeneratedAt { get; init; }

    /// <summary>
    /// Version of the diagnostic tool
    /// </summary>
    public required string ToolVersion { get; init; }

    /// <summary>
    /// User-provided vehicle information
    /// </summary>
    public required UserVehicleInfo UserVehicleInfo { get; init; }

    /// <summary>
    /// BLE adapter information
    /// </summary>
    public BleAdapterInfo? BleAdapterInfo { get; init; }

    /// <summary>
    /// ELM327/OBD adapter information
    /// </summary>
    public ObdAdapterInfo? ObdAdapterInfo { get; init; }

    /// <summary>
    /// Vehicle identification from ECU
    /// </summary>
    public VehicleIdentification? VehicleId { get; init; }

    /// <summary>
    /// Supported PIDs discovered from ECU
    /// </summary>
    public SupportedPidsInfo? SupportedPids { get; init; }

    /// <summary>
    /// Results of standard PID probes
    /// </summary>
    public IReadOnlyList<PidProbeResult> StandardPidResults { get; init; } = [];

    /// <summary>
    /// Results of extended/manufacturer-specific probes
    /// </summary>
    public IReadOnlyList<PidProbeResult> ExtendedPidResults { get; init; } = [];

    /// <summary>
    /// Any errors encountered during collection
    /// </summary>
    public IReadOnlyList<DiagnosticError> Errors { get; init; } = [];

    /// <summary>
    /// Additional notes from the collection process
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// User-provided information about the vehicle being diagnosed
/// </summary>
public record UserVehicleInfo
{
    public required int Year { get; init; }
    public required string Make { get; init; }
    public required string Model { get; init; }
    public string? Trim { get; init; }
    public string? EngineType { get; init; } // e.g., "2.0L Turbo", "Electric", "Hybrid"
    public string? TransmissionType { get; init; } // e.g., "Automatic", "CVT", "Manual"
    public string? AdditionalNotes { get; init; }
}

/// <summary>
/// Information about the BLE OBD adapter hardware
/// </summary>
public record BleAdapterInfo
{
    public required string DeviceName { get; init; }
    public required string MacAddress { get; init; }
    public int? Rssi { get; init; }
    public IReadOnlyList<BleServiceInfo> Services { get; init; } = [];
    public IReadOnlyDictionary<string, byte[]>? ManufacturerData { get; init; }
}

/// <summary>
/// Information about a BLE GATT service
/// </summary>
public record BleServiceInfo
{
    public required Guid ServiceUuid { get; init; }
    public IReadOnlyList<BleCharacteristicInfo> Characteristics { get; init; } = [];
}

/// <summary>
/// Information about a BLE GATT characteristic
/// </summary>
public record BleCharacteristicInfo
{
    public required Guid CharacteristicUuid { get; init; }
    public required IReadOnlyList<string> Properties { get; init; }
}

/// <summary>
/// Information about the OBD adapter (ELM327 or compatible)
/// </summary>
public record ObdAdapterInfo
{
    /// <summary>
    /// Response to ATZ (reset) command
    /// </summary>
    public string? ResetResponse { get; init; }

    /// <summary>
    /// Response to ATI (version) command - identifies adapter type/version
    /// </summary>
    public string? VersionResponse { get; init; }

    /// <summary>
    /// Response to AT@1 (device description) command
    /// </summary>
    public string? DeviceDescription { get; init; }

    /// <summary>
    /// Response to ATRV (voltage) command
    /// </summary>
    public string? VoltageResponse { get; init; }

    /// <summary>
    /// Response to ATDP (describe protocol) command
    /// </summary>
    public string? ProtocolDescription { get; init; }

    /// <summary>
    /// Response to ATDPN (describe protocol number) command
    /// </summary>
    public string? ProtocolNumber { get; init; }

    /// <summary>
    /// Raw responses to various AT commands for debugging
    /// </summary>
    public IReadOnlyDictionary<string, string> RawAtResponses { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Vehicle identification information from ECU
/// </summary>
public record VehicleIdentification
{
    /// <summary>
    /// Vehicle Identification Number (17 chars)
    /// </summary>
    public string? Vin { get; init; }

    /// <summary>
    /// Raw VIN response for debugging
    /// </summary>
    public string? RawVinResponse { get; init; }

    /// <summary>
    /// Calibration ID (Mode 09 PID 04)
    /// </summary>
    public string? CalibrationId { get; init; }

    /// <summary>
    /// ECU name (Mode 09 PID 0A)
    /// </summary>
    public string? EcuName { get; init; }
}

/// <summary>
/// Information about supported PIDs
/// </summary>
public record SupportedPidsInfo
{
    /// <summary>
    /// Mode 01 supported PIDs (live data)
    /// </summary>
    public IReadOnlyList<string> Mode01Pids { get; init; } = [];

    /// <summary>
    /// Mode 09 supported PIDs (vehicle info)
    /// </summary>
    public IReadOnlyList<string> Mode09Pids { get; init; } = [];

    /// <summary>
    /// Raw responses to PID support queries
    /// </summary>
    public IReadOnlyDictionary<string, string> RawResponses { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Result of probing a specific PID
/// </summary>
public record PidProbeResult
{
    public required string Command { get; init; }
    public required string Description { get; init; }
    public required bool Success { get; init; }
    public string? RawResponse { get; init; }
    public string? ParsedValue { get; init; }
    public string? Error { get; init; }
    public TimeSpan ResponseTime { get; init; }
}

/// <summary>
/// Error encountered during diagnostic collection
/// </summary>
public record DiagnosticError
{
    public required string Phase { get; init; }
    public required string Message { get; init; }
    public string? Details { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
