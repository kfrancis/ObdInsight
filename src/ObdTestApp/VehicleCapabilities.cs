using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ObdTestApp;

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

public interface IOnboardCharger : IVehicleCapability
{
    ValueTask<OnboardChargerStatus> GetStatusAsync(CancellationToken ct = default);
}

public interface ISteering : IVehicleCapability
{
    ValueTask<SteeringStatus> GetStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// Charger/charging system interface - generic across all vehicles.
/// </summary>
public interface ICharger : IVehicleCapability
{
    /// <summary>
    /// Gets the Vehicle Identification Number.
    /// </summary>
    ValueTask<string?> GetVinAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets current charging status if vehicle is plugged in.
    /// </summary>
    ValueTask<ChargingStatus?> GetChargingStatusAsync(CancellationToken ct = default);
}

public interface IVcm : IVehicleCapability
{
    /// <summary>
    /// Reads the current gear selector position as reported by the Vehicle Control Module (VCM).
    /// </summary>

    ValueTask<GearPosition> GetGearPositionAsync(CancellationToken ct = default);
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
/// Battery Management System interface - generic across all vehicle makes/models.
/// </summary>
public interface IBatteryManagementSystem : IVehicleCapability
{
    /// <summary>
    /// Gets comprehensive battery status (SOC, voltage, current, capacity, health).
    /// </summary>
    ValueTask<BatteryStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets detailed cell voltage information if available.
    /// Returns null if vehicle doesn't support individual cell monitoring.
    /// </summary>
    ValueTask<CellVoltageData?> GetCellVoltagesAsync(CancellationToken ct = default);
}

public interface IVehicleCapability
{ }

public readonly record struct HvbatStatus(
    double Voltage,      // in Volts
    double Current,      // in Amperes
    double StateOfCharge // in Percentage
);
public readonly record struct OnboardChargerStatus(
    bool IsCharging,
    double ChargePowerKw,
    double ChargeVoltage,
    double ChargeCurrent
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
