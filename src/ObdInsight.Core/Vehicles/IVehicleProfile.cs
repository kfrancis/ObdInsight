namespace ObdInsight.Core.Vehicles;

/// <summary>
/// Defines the communication protocol style for a vehicle.
/// Different vehicles may use standard OBD-II or manufacturer-specific protocols.
/// </summary>
public enum VehicleProtocol
{
    /// <summary>
    /// Standard OBD-II protocol (ISO 15765-4 CAN, ISO 14230 KWP2000, etc.)
    /// </summary>
    StandardObd2,

    /// <summary>
    /// Nissan/Infiniti CAR CAN protocol for Leaf and other EVs
    /// </summary>
    NissanCarCan,

    /// <summary>
    /// Tesla proprietary protocol
    /// </summary>
    Tesla,

    /// <summary>
    /// Chevrolet Volt/Bolt extended PIDs
    /// </summary>
    GmEv,

    /// <summary>
    /// BMW i-Series protocol
    /// </summary>
    BmwI,

    /// <summary>
    /// Volkswagen/Audi UDS-based protocol
    /// </summary>
    VagUds
}

/// <summary>
/// Defines vehicle-specific capabilities and data interpretation.
/// Implementations provide custom PID support, data decoders, and vehicle metadata.
/// </summary>
public interface IVehicleProfile
{
    /// <summary>
    /// Display name of the vehicle profile (e.g., "2017 Nissan Leaf")
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Manufacturer name
    /// </summary>
    string Manufacturer { get; }

    /// <summary>
    /// Vehicle model name
    /// </summary>
    string Model { get; }

    /// <summary>
    /// Model years this profile supports
    /// </summary>
    Range<int> SupportedYears { get; }

    /// <summary>
    /// Primary communication protocol for this vehicle
    /// </summary>
    VehicleProtocol Protocol { get; }

    /// <summary>
    /// Whether this is an electric or hybrid vehicle
    /// </summary>
    bool IsElectric { get; }

    /// <summary>
    /// VIN prefixes that identify this vehicle type (WMI codes)
    /// </summary>
    IReadOnlyList<string> VinPrefixes { get; }

    /// <summary>
    /// Custom PIDs supported by this vehicle beyond standard OBD-II
    /// </summary>
    IReadOnlyList<VehiclePid> CustomPids { get; }

    /// <summary>
    /// Data categories this profile can provide (e.g., Battery, Range, Charging)
    /// </summary>
    IReadOnlySet<VehicleDataCategory> SupportedCategories { get; }

    /// <summary>
    /// Gets the command to request a specific data point from this vehicle
    /// </summary>
    ObdCommand? GetCommand(VehicleDataPoint dataPoint);

    /// <summary>
    /// Decodes raw response bytes into a typed value for the given data point
    /// </summary>
    VehicleDataResult DecodeResponse(VehicleDataPoint dataPoint, byte[] responseBytes);

    /// <summary>
    /// Validates whether this profile matches a given VIN
    /// </summary>
    bool MatchesVin(string vin);

    /// <summary>
    /// Gets initialization commands needed for this vehicle (beyond standard ELM327 init)
    /// </summary>
    IReadOnlyList<ObdCommand> GetInitializationCommands();
}

/// <summary>
/// Categories of data a vehicle profile can provide
/// </summary>
public enum VehicleDataCategory
{
    /// <summary>Engine data (RPM, load, temps) - ICE vehicles</summary>
    Engine,

    /// <summary>Transmission data (gear, temps)</summary>
    Transmission,

    /// <summary>Battery/HV system data - EVs and Hybrids</summary>
    Battery,

    /// <summary>Charging status and stats - EVs</summary>
    Charging,

    /// <summary>Range and efficiency data</summary>
    Range,

    /// <summary>Climate control system</summary>
    Climate,

    /// <summary>Tire pressure monitoring</summary>
    Tpms,

    /// <summary>Standard diagnostics (DTCs, MIL)</summary>
    Diagnostics,

    /// <summary>Vehicle speed, odometer</summary>
    Movement,

    /// <summary>Fuel system data - ICE vehicles</summary>
    Fuel
}

/// <summary>
/// Specific data points that can be requested from a vehicle
/// </summary>
public enum VehicleDataPoint
{
    // Standard OBD-II
    Rpm,
    Speed,
    CoolantTemp,
    IntakeTemp,
    ThrottlePosition,
    EngineLoad,
    FuelLevel,
    FuelPressure,
    Vin,
    DtcCodes,

    // EV-specific
    BatteryStateOfCharge,
    BatteryStateOfHealth,
    BatteryVoltage,
    BatteryCurrent,
    BatteryTemp,
    BatteryCapacity,
    BatteryCellVoltages,

    // Range/efficiency
    RangeRemaining,
    EnergyConsumption,

    // Charging
    ChargingStatus,
    ChargerVoltage,
    ChargerCurrent,
    ChargePower,
    TimeToFullCharge,

    // Climate
    CabinTemp,
    HvacPower,

    // Other
    Odometer,
    AmbientTemp
}

/// <summary>
/// Result of decoding a vehicle data response
/// </summary>
public record VehicleDataResult(
    VehicleDataPoint DataPoint,
    bool Success,
    object? Value,
    string? Unit,
    string? Error = null
)
{
    public static VehicleDataResult Ok<T>(VehicleDataPoint dataPoint, T value, string unit) =>
        new(dataPoint, true, value, unit);

    public static VehicleDataResult Fail(VehicleDataPoint dataPoint, string error) =>
        new(dataPoint, false, null, null, error);

    public T? GetValue<T>() => Value is T typed ? typed : default;
}

/// <summary>
/// Defines a vehicle-specific PID with its encoding and decoding logic
/// </summary>
public record VehiclePid(
    string Name,
    string Command,
    VehicleDataPoint DataPoint,
    string Unit,
    string? Description = null,
    TimeSpan? Timeout = null
)
{
    /// <summary>
    /// Function to decode the raw byte response into a value
    /// </summary>
    public Func<byte[], object?>? Decoder { get; init; }

    /// <summary>
    /// Expected response header for validation (e.g., "7BB" for Nissan Leaf battery)
    /// </summary>
    public string? ExpectedHeader { get; init; }

    /// <summary>
    /// Number of expected response frames (for multi-frame responses)
    /// </summary>
    public int ExpectedFrames { get; init; } = 1;
}

/// <summary>
/// Represents a year range for vehicle model support
/// </summary>
public readonly struct Range<T> where T : IComparable<T>
{
    public T Start { get; }
    public T End { get; }

    public Range(T start, T end)
    {
        Start = start;
        End = end;
    }

    public bool Contains(T value) =>
        value.CompareTo(Start) >= 0 && value.CompareTo(End) <= 0;

    public override string ToString() => $"{Start}-{End}";
}
