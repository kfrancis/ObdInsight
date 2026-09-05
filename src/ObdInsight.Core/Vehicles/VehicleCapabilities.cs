namespace ObdInsight.Core.Vehicles;

public interface IAntilockBrakingSystem : IVehicleCapability
{
    /// <summary>
    ///     Gets current ABS status including wheel speeds and vehicle speed.
    /// </summary>
    ValueTask<AbsStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    ///     Streams ABS status as the underlying broadcast frames arrive (0x130/0x245/0x284/0x285/0x292/0x354 on the Leaf),
    ///     re-emitting
    ///     on every contributing frame with the rest of the record taken from the shared monitor's
    ///     latest-frame cache.
    /// </summary>
    /// <param name="minInterval">
    ///     Minimum spacing between emissions; default (zero) emits on every contributing frame.
    ///     Emissions inside the interval are skipped rather than queued, so the next one always
    ///     carries the newest state.
    /// </param>
    /// <param name="ct">Stops the stream. The stream also completes when monitoring ends.</param>
    IAsyncEnumerable<AbsStatus> StreamStatusAsync(TimeSpan minInterval = default, CancellationToken ct = default);
}

/// <summary>
///     Battery Management System interface - generic across all vehicle makes/models.
/// </summary>
public interface IBatteryManagementSystem : IVehicleCapability
{
    /// <summary>
    ///     Gets detailed cell voltage information if available.
    ///     Returns null if vehicle doesn't support individual cell monitoring.
    /// </summary>
    ValueTask<CellVoltageData?> GetCellVoltagesAsync(CancellationToken ct = default);

    /// <summary>
    ///     Gets comprehensive battery status (SOC, voltage, current, capacity, health).
    /// </summary>
    ValueTask<BatteryStatus> GetStatusAsync(CancellationToken ct = default);
}

public interface IBodyControl : IVehicleCapability
{
    ValueTask<BodyControlStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    ///     Streams body control status as the underlying broadcast frames arrive (0x60D/0x625 on the Leaf), re-emitting
    ///     on every contributing frame with the rest of the record taken from the shared monitor's
    ///     latest-frame cache.
    /// </summary>
    /// <param name="minInterval">
    ///     Minimum spacing between emissions; default (zero) emits on every contributing frame.
    ///     Emissions inside the interval are skipped rather than queued, so the next one always
    ///     carries the newest state.
    /// </param>
    /// <param name="ct">Stops the stream. The stream also completes when monitoring ends.</param>
    IAsyncEnumerable<BodyControlStatus> StreamStatusAsync(TimeSpan minInterval = default,
        CancellationToken ct = default);
}

public interface IBrake : IVehicleCapability
{
    ValueTask<BrakeStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    ///     Streams brake status as the underlying broadcast frames arrive (0x1CA on the Leaf), re-emitting
    ///     on every contributing frame with the rest of the record taken from the shared monitor's
    ///     latest-frame cache.
    /// </summary>
    /// <param name="minInterval">
    ///     Minimum spacing between emissions; default (zero) emits on every contributing frame.
    ///     Emissions inside the interval are skipped rather than queued, so the next one always
    ///     carries the newest state.
    /// </param>
    /// <param name="ct">Stops the stream. The stream also completes when monitoring ends.</param>
    IAsyncEnumerable<BrakeStatus> StreamStatusAsync(TimeSpan minInterval = default, CancellationToken ct = default);
}

public interface IHvac : IVehicleCapability
{
    ValueTask<HvacStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    ///     Streams HVAC status as the underlying broadcast frames arrive (0x54A/0x54B/0x54C/0x54F on the Leaf), re-emitting
    ///     on every contributing frame with the rest of the record taken from the shared monitor's
    ///     latest-frame cache.
    /// </summary>
    /// <param name="minInterval">
    ///     Minimum spacing between emissions; default (zero) emits on every contributing frame.
    ///     Emissions inside the interval are skipped rather than queued, so the next one always
    ///     carries the newest state.
    /// </param>
    /// <param name="ct">Stops the stream. The stream also completes when monitoring ends.</param>
    IAsyncEnumerable<HvacStatus> StreamStatusAsync(TimeSpan minInterval = default, CancellationToken ct = default);
}

public interface IHvbat : IVehicleCapability
{
    ValueTask<HvbatStatus> GetStatusAsync(CancellationToken ct = default);
}

/// <summary>
///     Motor/Inverter interface - provides motor and inverter status data.
/// </summary>
public interface IMotorController : IVehicleCapability
{
    /// <summary>
    ///     Gets current motor and inverter status.
    /// </summary>
    ValueTask<MotorStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    ///     Streams motor and inverter status as the underlying broadcast frames arrive (0x1DA/0x55A on the Leaf), re-emitting
    ///     on every contributing frame with the rest of the record taken from the shared monitor's
    ///     latest-frame cache.
    /// </summary>
    /// <param name="minInterval">
    ///     Minimum spacing between emissions; default (zero) emits on every contributing frame.
    ///     Emissions inside the interval are skipped rather than queued, so the next one always
    ///     carries the newest state.
    /// </param>
    /// <param name="ct">Stops the stream. The stream also completes when monitoring ends.</param>
    IAsyncEnumerable<MotorStatus> StreamStatusAsync(TimeSpan minInterval = default, CancellationToken ct = default);
}

/// <summary>
///     On-board charger interface - provides charging status and power information.
/// </summary>
public interface IOnboardCharger : IVehicleCapability
{
    /// <summary>
    ///     Gets current charging status if vehicle is plugged in.
    /// </summary>
    ValueTask<ChargingStatus?> GetChargingStatusAsync(CancellationToken ct = default);

    /// <summary>
    ///     Streams charging status as the underlying broadcast frames arrive (0x390 on the Leaf), re-emitting
    ///     on every contributing frame with the rest of the record taken from the shared monitor's
    ///     latest-frame cache. Nullable for the same reason the pull
    ///     method is: a frame that arrives but cannot be decoded yields null rather than an error.
    /// </summary>
    /// <param name="minInterval">
    ///     Minimum spacing between emissions; default (zero) emits on every contributing frame.
    ///     Emissions inside the interval are skipped rather than queued, so the next one always
    ///     carries the newest state.
    /// </param>
    /// <param name="ct">Stops the stream. The stream also completes when monitoring ends.</param>
    IAsyncEnumerable<ChargingStatus?> StreamChargingStatusAsync(TimeSpan minInterval = default,
        CancellationToken ct = default);
}

public interface ISteering : IVehicleCapability
{
    ValueTask<SteeringStatus> GetStatusAsync(CancellationToken ct = default);
}

public interface IVcm : IVehicleCapability
{
    /// <summary>
    ///     Reads the current gear selector position as reported by the Vehicle Control Module (VCM).
    /// </summary>
    ValueTask<GearPosition> GetGearPositionAsync(CancellationToken ct = default);

    /// <summary>
    ///     Gets comprehensive VCM status including power consumption, climate data, and eco indicators.
    ///     This data is typically transmitted on the CAR-CAN bus.
    /// </summary>
    ValueTask<VcmStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    ///     Streams VCM status as the underlying broadcast frames arrive (0x510/0x180/0x5A9 on the Leaf), re-emitting
    ///     on every contributing frame with the rest of the record taken from the shared monitor's
    ///     latest-frame cache.
    /// </summary>
    /// <param name="minInterval">
    ///     Minimum spacing between emissions; default (zero) emits on every contributing frame.
    ///     Emissions inside the interval are skipped rather than queued, so the next one always
    ///     carries the newest state.
    /// </param>
    /// <param name="ct">Stops the stream. The stream also completes when monitoring ends.</param>
    IAsyncEnumerable<VcmStatus> StreamStatusAsync(TimeSpan minInterval = default, CancellationToken ct = default);
}

/// <summary>
///     Marker for vehicle capabilities.
///     <para>
///         <b>Degradation contract (all capabilities):</b> data absence — a silent ECU,
///         an unreachable bus, an adapter error, or an unparseable response — yields a null
///         result or a result record whose fields are null/default. Capability methods never
///         throw for missing data; the only exception that propagates is
///         <see cref="OperationCanceledException" /> on cancellation. Consumers can therefore
///         bind results directly without try/catch.
///     </para>
/// </summary>
public interface IVehicleCapability
{
}

/// <summary>
///     Vehicle identification interface - provides access to vehicle identification data.
/// </summary>
public interface IVehicleIdentification : IVehicleCapability
{
    /// <summary>
    ///     Gets the Vehicle Identification Number.
    /// </summary>
    ValueTask<string?> GetVinAsync(CancellationToken ct = default);
}

/// <summary>
///     Diagnostic trouble code access (OBD-II Mode 03 stored / Mode 07 pending).
/// </summary>
public interface IDiagnosticTroubleCodes : IVehicleCapability
{
    /// <summary>
    ///     Reads stored and pending DTCs with independent outcomes and responder evidence.
    ///     Successful empty results cover only responding ECUs, not the whole vehicle.
    ///     Caller cancellation propagates; programming and lifecycle errors are not
    ///     converted to diagnostic results.
    /// </summary>
    ValueTask<DtcReadResult> GetDtcsAsync(CancellationToken ct = default);
}

/// <summary>
///     Independent stored (Mode 03) and pending (Mode 07) diagnostic outcomes.
/// </summary>
public sealed record DtcReadResult
{
    public required DtcModeResult Stored { get; init; }
    public required DtcModeResult Pending { get; init; }
}

/// <summary>
///     Generic battery status - applicable to any EV/hybrid.
/// </summary>
public sealed record BatteryStatus
{
    public Protocols.ObservationMetadata SocObservation { get; init; }
    public Protocols.ObservationMetadata VoltageObservation { get; init; }
    public Protocols.ObservationMetadata CurrentObservation { get; init; }
    public Protocols.ObservationMetadata TemperatureObservation { get; init; }
    public Protocols.ObservationMetadata StateOfHealthObservation { get; init; }
    public Protocols.ObservationMetadata PowerObservation => VoltageObservation.Combine(CurrentObservation);
    /// <summary>State of Charge (0-100%)</summary>
    public double? SocPercent { get; init; }

    /// <summary>Battery pack voltage (volts)</summary>
    public double? VoltageVolts { get; init; }

    /// <summary>Current draw/charge (amps, positive = discharge, negative = charge)</summary>
    public double? CurrentAmps { get; init; }

    /// <summary>Remaining capacity (amp-hours)</summary>
    public double? CapacityAh { get; init; }

    /// <summary>
    ///     Battery state of health (%) from a supported SOH source, or null when unavailable.
    ///     Manufacturer-specific metrics such as Nissan Hx are not SOH substitutes.
    /// </summary>
    public double? StateOfHealthPercent { get; init; }

    /// <summary>Average battery temperature (°C)</summary>
    public double? TemperatureC { get; init; }

    /// <summary>Minimum cell temperature (°C)</summary>
    public double? MinTemperatureC { get; init; }

    /// <summary>Maximum cell temperature (°C)</summary>
    public double? MaxTemperatureC { get; init; }

    /// <summary>Power being drawn/supplied (watts, positive = discharge, negative = charge)</summary>
    public double? PowerWatts =>
        VoltageVolts.HasValue && CurrentAmps.HasValue
            ? VoltageVolts.Value * CurrentAmps.Value
            : null;
}

/// <summary>
///     Individual cell voltage data - not all vehicles support this.
/// </summary>
public sealed class CellVoltageData
{
    public Protocols.ObservationMetadata Observation { get; init; }
    public CellVoltageData(IEnumerable<int?> cellVoltagesMv, IEnumerable<bool>? balancingCells = null)
    {
        ArgumentNullException.ThrowIfNull(cellVoltagesMv);
        var cells = cellVoltagesMv.ToArray();
        var balancing = balancingCells?.ToArray();
        if (balancing is not null && balancing.Length != cells.Length)
            throw new ArgumentException("Balancing flags must match the physical cell count.", nameof(balancingCells));
        CellVoltagesMv = Array.AsReadOnly(cells);
        BalancingCells = balancing is null ? null : Array.AsReadOnly(balancing);
    }

    /// <summary>Physical cell order; a null slot is invalid/missing, never removed.</summary>
    public IReadOnlyList<int?> CellVoltagesMv { get; }
    public int CellCount => CellVoltagesMv.Count;
    public int ValidCellCount => CellVoltagesMv.Count(v => v.HasValue);
    /// <summary>All slots were supplied as non-null; producers remain responsible for measurement validity.</summary>
    public bool IsComplete => CellCount > 0 && ValidCellCount == CellCount;
    /// <summary>Pack-wide statistics are unavailable for incomplete cell sets.</summary>
    public int? MinVoltageMv => IsComplete ? CellVoltagesMv.Min() : null;
    public int? MaxVoltageMv => IsComplete ? CellVoltagesMv.Max() : null;
    public int? AvgVoltageMv => IsComplete ? (int?)CellVoltagesMv.Average() : null;
    public int? DeltaVoltageMv => MaxVoltageMv - MinVoltageMv;

    /// <summary>Balancing flags in the same physical order, or null when unreported.</summary>
    public IReadOnlyList<bool>? BalancingCells { get; }
    public int? BalancingCellCount => BalancingCells?.Count(b => b);
}

/// <summary>
///     Charging status information.
/// </summary>
public sealed record ChargingStatus
{
    public bool IsPluggedIn { get; init; }
    public bool IsCharging { get; init; }
    public double? ChargePowerKw { get; init; }
    public TimeSpan? EstimatedTimeToFull { get; init; }
    public double? ChargerVoltage { get; init; }
    public double? ChargerCurrent { get; init; }
}

/// <summary>
///     Motor and inverter status information.
/// </summary>
public sealed record MotorStatus
{
    /// <summary>Motor/generator input voltage (volts)</summary>
    public double? InputVoltageV { get; init; }

    /// <summary>Effective motor torque (Nm, positive = drive, negative = regen)</summary>
    public double? EffectiveTorqueNm { get; init; }

    /// <summary>Motor output RPM (negative for reverse)</summary>
    public int? OutputRevolutionRpm { get; init; }

    /// <summary>Motor temperature (°C)</summary>
    public double? MotorTempC { get; init; }

    /// <summary>Inverter communications board temperature (°C)</summary>
    public double? InverterComBoardTempC { get; init; }

    /// <summary>IGBT (power transistor) temperature (°C)</summary>
    public double? IgbtTempC { get; init; }

    /// <summary>IGBT driver board temperature (°C)</summary>
    public double? IgbtDriverBoardTempC { get; init; }

    /// <summary>Error codes blocking inverter operation</summary>
    public int? ErrorCodes { get; init; }

    /// <summary>Calculated motor power (watts) if voltage, torque, and RPM are available</summary>
    public double? PowerWatts =>
        EffectiveTorqueNm.HasValue && OutputRevolutionRpm.HasValue
            ? EffectiveTorqueNm.Value * OutputRevolutionRpm.Value * (2.0 * Math.PI / 60.0)
            : null;
}

public readonly record struct HvbatStatus(
    double Voltage, // in Volts
    double Current, // in Amperes
    double StateOfCharge // in Percentage
);

public readonly record struct SteeringStatus(
    double AngleDegrees,
    double TorqueNm
);

public readonly record struct BodyControlStatus(
    bool DoorsLocked,
    bool HeadlightsOn,
    bool HazardLightsOn
);

/// <summary>
///     ABS (Anti-lock Braking System) status information.
/// </summary>
public sealed record AbsStatus
{
    public Protocols.ObservationMetadata VehicleSpeedObservation { get; init; }
    /// <summary>Front right wheel speed (km/h)</summary>
    public double? WheelSpeedFrKmh { get; init; }

    /// <summary>Front left wheel speed (km/h)</summary>
    public double? WheelSpeedFlKmh { get; init; }

    /// <summary>Rear right wheel speed (km/h)</summary>
    public double? WheelSpeedRrKmh { get; init; }

    /// <summary>Rear left wheel speed (km/h)</summary>
    public double? WheelSpeedRlKmh { get; init; }

    /// <summary>Vehicle speed from ABS (km/h)</summary>
    public double? VehicleSpeedKmh { get; init; }

    /// <summary>Vehicle speed in pulses</summary>
    public int? VehicleSpeedPulses { get; init; }

    /// <summary>ESP/Traction control disabled flag</summary>
    public bool? EspDisabled { get; init; }

    /// <summary>12V lead-acid battery voltage (volts)</summary>
    public double? LeadAcidBatteryVoltage { get; init; }

    /// <summary>Friction brake pressure (raw value)</summary>
    public int? FrictionBrakePressure { get; init; }

    /// <summary>VDC torque down request 1 (Nm)</summary>
    public double? VdcTorqueDownRequest1Nm { get; init; }

    /// <summary>VDC torque down request 2 (Nm)</summary>
    public double? VdcTorqueDownRequest2Nm { get; init; }

    /// <summary>Motor torque request from ABS (Nm)</summary>
    public double? MotorTorqueRequestNm { get; init; }

    /// <summary>ABS bitmask status byte</summary>
    public int? BitmaskAbs { get; init; }
}

/// <summary>
///     VCM (Vehicle Control Module) status information including power consumption and climate data.
/// </summary>
public sealed record VcmStatus
{
    public Protocols.ObservationMetadata RangeObservation { get; init; }
    /// <summary>Climate control active flag</summary>
    public bool? ClimateControlActive { get; init; }

    /// <summary>Climate control power consumption (kW)</summary>
    public double? ClimateControlPowerKw { get; init; }

    /// <summary>Outside ambient temperature (°C)</summary>
    public double? OutsideAmbientTempC { get; init; }

    /// <summary>Integrated motor power consumption (raw value, 0-255)</summary>
    public int? IntegratedMotorPowerConsumption { get; init; }

    /// <summary>Integrated A/C power consumption (raw value, 0-31)</summary>
    public int? IntegratedAcPowerConsumption { get; init; }

    /// <summary>Integrated auxiliary power consumption (raw value, 0-15)</summary>
    public int? IntegratedAuxPowerConsumption { get; init; }

    /// <summary>Instantaneous auxiliary power consumption (raw value, 0-31)</summary>
    public int? PowerConsumptionAux { get; init; }

    /// <summary>Eco indicator level (0-15)</summary>
    public int? EcoIndicator { get; init; }

    /// <summary>Eco tree growth level (0-31)</summary>
    public int? EcoTree { get; init; }

    /// <summary>Charge mode (0-3)</summary>
    public int? ChargeMode { get; init; }

    /// <summary>Motor current (amperes) - from frame 0x180</summary>
    public int? MotorCurrentAmps { get; init; }

    /// <summary>Throttle position (0-100%) - from frame 0x180</summary>
    public double? ThrottlePositionPercent { get; init; }

    /// <summary>
    ///     Remaining range estimate as displayed on the instrument cluster (km).
    ///     Null when the source frame is absent or reports the "charging" sentinel.
    /// </summary>
    public double? RangeKm { get; init; }
}
