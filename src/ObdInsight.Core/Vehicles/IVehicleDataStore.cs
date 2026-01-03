using System.ComponentModel;

namespace ObdInsight.Core.Vehicles;

/// <summary>
/// Observable store for vehicle data that widgets bind to.
/// Provides standardized properties regardless of which vehicle profile is active.
/// </summary>
/// <remarks>
/// This interface serves as the single source of truth for widget data binding.
/// It abstracts away vehicle-specific OBD queries (Mode 21, custom PIDs, etc.)
/// and presents a uniform interface that widgets can bind to.
///
/// The implementation polls IVehicleObdService and updates properties when
/// new data arrives. Widgets automatically update via INotifyPropertyChanged.
///
/// Example usage in XAML:
/// <code>
/// &lt;Label Text="{Binding DataStore.BatterySoc, StringFormat='{0:F1}%'}" /&gt;
/// </code>
/// </remarks>
public interface IVehicleDataStore : INotifyPropertyChanged
{
    #region EV Battery Data

    /// <summary>
    /// Battery state of charge (0-100%).
    /// Null if not available or vehicle is not an EV.
    /// </summary>
    double? BatterySoc { get; }

    /// <summary>
    /// Battery state of health (0-100%).
    /// Indicates battery degradation over time.
    /// </summary>
    double? BatterySoh { get; }

    /// <summary>
    /// High-voltage battery pack voltage (V).
    /// </summary>
    double? BatteryVoltage { get; }

    /// <summary>
    /// Battery current (A). Positive = discharging, Negative = charging.
    /// </summary>
    double? BatteryCurrent { get; }

    /// <summary>
    /// Battery pack temperature (°C).
    /// May be averaged from multiple sensors.
    /// </summary>
    double? BatteryTemp { get; }

    /// <summary>
    /// Battery capacity in amp-hours (Ah).
    /// </summary>
    double? BatteryCapacity { get; }

    /// <summary>
    /// Calculated battery power (kW).
    /// Positive = discharging, Negative = charging.
    /// </summary>
    double? PowerKw { get; }

    #endregion

    #region Range and Charging

    /// <summary>
    /// Estimated remaining range (km or miles based on user preference).
    /// </summary>
    double? RangeRemaining { get; }

    /// <summary>
    /// Current charging status description.
    /// Examples: "Not Charging", "Level 2 Charging", "DC Fast Charging"
    /// </summary>
    string? ChargingStatus { get; }

    /// <summary>
    /// Whether the vehicle is currently charging.
    /// </summary>
    bool IsCharging { get; }

    /// <summary>
    /// Charging power (kW) when actively charging.
    /// </summary>
    double? ChargePower { get; }

    /// <summary>
    /// Estimated time to full charge (minutes).
    /// </summary>
    int? TimeToFullCharge { get; }

    #endregion

    #region Standard OBD Data

    /// <summary>
    /// Vehicle speed (km/h).
    /// </summary>
    double? Speed { get; }

    /// <summary>
    /// Total odometer reading (km).
    /// </summary>
    double? Odometer { get; }

    /// <summary>
    /// Ambient/outside temperature (°C).
    /// </summary>
    double? AmbientTemp { get; }

    /// <summary>
    /// Cabin/interior temperature (°C).
    /// </summary>
    double? CabinTemp { get; }

    /// <summary>
    /// Vehicle Identification Number.
    /// </summary>
    string? Vin { get; }

    #endregion

    #region ICE Vehicle Data (for hybrids or ICE vehicles)

    /// <summary>
    /// Engine RPM (revolutions per minute).
    /// </summary>
    double? EngineRpm { get; }

    /// <summary>
    /// Engine coolant temperature (°C).
    /// </summary>
    double? CoolantTemp { get; }

    /// <summary>
    /// Fuel level (0-100%).
    /// </summary>
    double? FuelLevel { get; }

    /// <summary>
    /// Throttle position (0-100%).
    /// </summary>
    double? ThrottlePosition { get; }

    /// <summary>
    /// Calculated engine load (0-100%).
    /// </summary>
    double? EngineLoad { get; }

    #endregion

    #region Metadata

    /// <summary>
    /// The currently active vehicle profile.
    /// Null if no vehicle is connected or detected.
    /// </summary>
    IVehicleProfile? ActiveProfile { get; }

    /// <summary>
    /// Display name of the connected vehicle (from profile).
    /// </summary>
    string? VehicleName { get; }

    /// <summary>
    /// Whether the data store is connected to a vehicle.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Whether this is an electric vehicle.
    /// </summary>
    bool IsElectricVehicle { get; }

    /// <summary>
    /// Timestamp of the last successful data update.
    /// </summary>
    DateTimeOffset LastUpdated { get; }

    /// <summary>
    /// Whether the data is considered stale (no update within threshold).
    /// Default threshold is 30 seconds.
    /// </summary>
    bool IsDataStale { get; }

    /// <summary>
    /// Current polling interval.
    /// </summary>
    TimeSpan PollingInterval { get; }

    /// <summary>
    /// Categories of data supported by the current vehicle profile.
    /// </summary>
    IReadOnlySet<VehicleDataCategory> SupportedCategories { get; }

    #endregion

    #region Query Support

    /// <summary>
    /// Checks if a specific data point is available from the current vehicle profile.
    /// </summary>
    /// <param name="dataPoint">The data point to check</param>
    /// <returns>True if the data point can be queried</returns>
    bool IsDataPointAvailable(VehicleDataPoint dataPoint);

    /// <summary>
    /// Gets the current value for a specific data point.
    /// </summary>
    /// <param name="dataPoint">The data point to retrieve</param>
    /// <returns>The value, or null if not available</returns>
    object? GetValue(VehicleDataPoint dataPoint);

    /// <summary>
    /// Gets the unit string for a specific data point.
    /// </summary>
    /// <param name="dataPoint">The data point</param>
    /// <returns>Unit string (e.g., "%", "V", "°C")</returns>
    string? GetUnit(VehicleDataPoint dataPoint);

    #endregion

    #region Control

    /// <summary>
    /// Starts polling for vehicle data.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StartPollingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops polling for vehicle data.
    /// </summary>
    Task StopPollingAsync();

    /// <summary>
    /// Forces an immediate refresh of all supported data points.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the vehicle profile and associated service.
    /// Called when a vehicle is detected or manually selected.
    /// </summary>
    /// <param name="vehicleService">The vehicle OBD service to use</param>
    void SetVehicleService(IVehicleObdService vehicleService);

    /// <summary>
    /// Clears all data and disconnects from the vehicle service.
    /// </summary>
    void Clear();

    #endregion
}

/// <summary>
/// Event args for data point update events
/// </summary>
public class DataPointUpdatedEventArgs : EventArgs
{
    public DataPointUpdatedEventArgs(VehicleDataPoint dataPoint, object? value, bool success)
    {
        DataPoint = dataPoint;
        Value = value;
        Success = success;
        Timestamp = DateTimeOffset.UtcNow;
    }

    public VehicleDataPoint DataPoint { get; }
    public object? Value { get; }
    public bool Success { get; }
    public DateTimeOffset Timestamp { get; }
}
