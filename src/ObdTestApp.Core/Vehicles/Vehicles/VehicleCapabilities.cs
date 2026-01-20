using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ObdTestApp.Core.Vehicles;

public interface IAntilockBrakingSystem : IVehicleCapability
{
    /// <summary>
    /// Gets current ABS status including wheel speeds and vehicle speed.
    /// </summary>
    ValueTask<AbsStatus> GetStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// Battery Management System interface - generic across all vehicle makes/models.
/// </summary>
public interface IBatteryManagementSystem : IVehicleCapability
{
    /// <summary>
    /// Gets detailed cell voltage information if available.
    /// Returns null if vehicle doesn't support individual cell monitoring.
    /// </summary>
    ValueTask<CellVoltageData?> GetCellVoltagesAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets comprehensive battery status (SOC, voltage, current, capacity, health).
    /// </summary>
    ValueTask<BatteryStatus> GetStatusAsync(CancellationToken ct = default);
}

public interface IBodyControl : IVehicleCapability
{
    ValueTask<BodyControlStatus> GetStatusAsync(CancellationToken ct = default);
}

public interface IBrake : IVehicleCapability
{
    ValueTask<BrakeStatus> GetStatusAsync(CancellationToken ct = default);
}

public interface IHvac : IVehicleCapability
{
    ValueTask<HvacStatus> GetStatusAsync(CancellationToken ct = default);
}

public interface IHvbat : IVehicleCapability
{
    ValueTask<HvbatStatus> GetStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// Motor/Inverter interface - provides motor and inverter status data.
/// </summary>
public interface IMotorController : IVehicleCapability
{
    /// <summary>
    /// Gets current motor and inverter status.
    /// </summary>
    ValueTask<MotorStatus> GetStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// On-board charger interface - provides charging status and power information.
/// </summary>
public interface IOnboardCharger : IVehicleCapability
{
    /// <summary>
    /// Gets current charging status if vehicle is plugged in.
    /// </summary>
    ValueTask<ChargingStatus?> GetChargingStatusAsync(CancellationToken ct = default);
}

public interface ISteering : IVehicleCapability
{
    ValueTask<SteeringStatus> GetStatusAsync(CancellationToken ct = default);
}

public interface IVcm : IVehicleCapability
{
    /// <summary>
    /// Reads the current gear selector position as reported by the Vehicle Control Module (VCM).
    /// </summary>
    ValueTask<GearPosition> GetGearPositionAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets comprehensive VCM status including power consumption, climate data, and eco indicators.
    /// This data is typically transmitted on the CAR-CAN bus.
    /// </summary>
    ValueTask<VcmStatus> GetStatusAsync(CancellationToken ct = default);
}

public interface IVehicleCapability
{ }

/// <summary>
/// Vehicle identification interface - provides access to vehicle identification data.
/// </summary>
public interface IVehicleIdentification : IVehicleCapability
{
    /// <summary>
    /// Gets the Vehicle Identification Number.
    /// </summary>
    ValueTask<string?> GetVinAsync(CancellationToken ct = default);
}

/// <summary>
/// Generic battery status - applicable to any EV/hybrid.
/// </summary>
public sealed record BatteryStatus
{
    /// <summary>State of Charge (0-100%)</summary>
    public double? SocPercent { get; init; }

    /// <summary>Battery pack voltage (volts)</summary>
    public double? VoltageVolts { get; init; }

    /// <summary>Current draw/charge (amps, positive = discharge, negative = charge)</summary>
    public double? CurrentAmps { get; init; }

    /// <summary>Remaining capacity (amp-hours)</summary>
    public double? CapacityAh { get; init; }

    /// <summary>Battery health/State of Health (0-100%)</summary>
    public double? HealthPercent { get; init; }

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
/// Individual cell voltage data - not all vehicles support this.
/// </summary>
public sealed record CellVoltageData
{
    public required int[] CellVoltagesMv { get; init; }
    public int CellCount => CellVoltagesMv.Length;
    public int MinVoltageMv { get; init; }
    public int MaxVoltageMv { get; init; }
    public int AvgVoltageMv { get; init; }
    public int DeltaVoltageMv => MaxVoltageMv - MinVoltageMv;
}

/// <summary>
/// Charging status information.
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
/// Motor and inverter status information.
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
    double Voltage,      // in Volts
    double Current,      // in Amperes
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
/// ABS (Anti-lock Braking System) status information.
/// </summary>
public sealed record AbsStatus
{
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
/// VCM (Vehicle Control Module) status information including power consumption and climate data.
/// </summary>
public sealed record VcmStatus
{
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
}

