namespace ObdInsight.Core.Vehicles;

/// <summary>
/// Extended OBD service with vehicle-specific awareness.
/// Provides access to both standard OBD-II data and vehicle-specific data points.
/// </summary>
public interface IVehicleObdService : IObdService
{
    /// <summary>
    /// The current vehicle profile being used for data interpretation.
    /// </summary>
    IVehicleProfile VehicleProfile { get; }

    /// <summary>
    /// Whether the vehicle profile supports electric vehicle data.
    /// </summary>
    bool SupportsEvData => VehicleProfile.IsElectric;

    /// <summary>
    /// Gets all data categories supported by the current vehicle profile.
    /// </summary>
    IReadOnlySet<VehicleDataCategory> SupportedCategories => VehicleProfile.SupportedCategories;

    /// <summary>
    /// Queries a vehicle-specific data point using the profile's encoding/decoding.
    /// </summary>
    Task<VehicleDataResult> GetDataAsync(VehicleDataPoint dataPoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets multiple data points in a single batch.
    /// </summary>
    Task<IReadOnlyList<VehicleDataResult>> GetDataBatchAsync(
        IEnumerable<VehicleDataPoint> dataPoints,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a specific data point is supported by the current vehicle profile.
    /// </summary>
    bool IsDataPointSupported(VehicleDataPoint dataPoint);

    // EV-specific convenience methods (return null for non-EV vehicles)

    /// <summary>
    /// Gets the high-voltage battery state of charge (%).
    /// </summary>
    Task<double?> GetBatterySocAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the battery state of health (%).
    /// </summary>
    Task<double?> GetBatterySohAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the high-voltage battery pack voltage.
    /// </summary>
    Task<double?> GetBatteryVoltageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the estimated remaining range.
    /// </summary>
    Task<double?> GetRangeRemainingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current charging status (if applicable).
    /// </summary>
    Task<string?> GetChargingStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets comprehensive battery information in a single request.
    /// </summary>
    Task<BatteryInfo?> GetBatteryInfoAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Comprehensive battery information for EVs
/// </summary>
public record BatteryInfo(
    double StateOfCharge,
    double StateOfHealth,
    double Voltage,
    double Current,
    double Temperature,
    double Capacity,
    double RangeRemaining,
    string ChargingStatus
)
{
    /// <summary>
    /// Calculated power in kW (voltage * current / 1000)
    /// Positive = discharging, Negative = charging
    /// </summary>
    public double PowerKw => Voltage * Current / 1000.0;

    /// <summary>
    /// Whether the vehicle is currently charging
    /// </summary>
    public bool IsCharging => ChargingStatus != "Not Charging" && Current < 0;
}

/// <summary>
/// Options for vehicle detection and service initialization
/// </summary>
public record VehicleServiceOptions
{
    /// <summary>
    /// Whether to automatically detect the vehicle type from VIN/ECU probing
    /// </summary>
    public bool AutoDetectVehicle { get; init; } = true;

    /// <summary>
    /// Manually specified vehicle profile to use (overrides auto-detection)
    /// </summary>
    public IVehicleProfile? ManualProfile { get; init; }

    /// <summary>
    /// Whether to run vehicle-specific initialization commands after adapter init
    /// </summary>
    public bool RunVehicleInit { get; init; } = true;

    /// <summary>
    /// Timeout for vehicle detection process
    /// </summary>
    public TimeSpan DetectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
}