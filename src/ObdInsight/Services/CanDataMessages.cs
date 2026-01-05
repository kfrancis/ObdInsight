namespace ObdInsight.Services;

/// <summary>
/// CAN frame data messages for passive monitoring of broadcast frames.
/// These messages correspond to frames broadcast by the vehicle when in READY mode.
/// </summary>

/// <summary>
/// Message for CAN frame 0x1DB - Battery current, voltage, and dashboard SOC.
/// Broadcast at 10ms cycle when car is in READY mode.
/// </summary>
/// <param name="CurrentAmps">Battery current in Amps (positive = discharge, negative = charge)</param>
/// <param name="VoltageVolts">Battery voltage in Volts</param>
/// <param name="DashSocPercent">Dashboard SOC as displayed to driver (0-100%)</param>
public record BatteryStatusMessage(double CurrentAmps, double VoltageVolts, int DashSocPercent);

/// <summary>
/// Message for CAN frame 0x1DC - Power limits.
/// Broadcast at 10ms cycle when car is in READY mode.
/// </summary>
/// <param name="DischargeLimitRaw">Raw discharge limit value</param>
/// <param name="RegenLimitRaw">Raw regen limit value</param>
/// <param name="ChargeLimitRaw">Raw charge limit value</param>
public record PowerLimitsMessage(byte DischargeLimitRaw, byte RegenLimitRaw, byte ChargeLimitRaw);

/// <summary>
/// Message for CAN frame 0x5BC - GIDs, SOH, and charge time.
/// Broadcast at 100ms cycle when car is in READY mode.
/// </summary>
/// <param name="Gids">GIDs value (energy units, multiply by 80 for Wh)</param>
/// <param name="EnergyKwh">Calculated energy in kWh</param>
/// <param name="SohPercent">State of Health percentage</param>
/// <param name="HxPercent">Hx percentage (battery health indicator)</param>
public record GidsDataMessage(int Gids, double EnergyKwh, double SohPercent, double HxPercent);

/// <summary>
/// Message for CAN frame 0x55B - High-resolution SOC.
/// Broadcast at 100ms cycle when car is in READY mode.
/// </summary>
/// <param name="SocPercent">High-resolution SOC percentage</param>
/// <param name="SocRaw10Bits">Raw 10-bit SOC value</param>
public record HighResSocMessage(double SocPercent, int SocRaw10Bits);

/// <summary>
/// Message indicating monitor service state changed.
/// </summary>
/// <param name="IsMonitoring">Whether the service is actively monitoring CAN traffic</param>
/// <param name="Message">Optional status message</param>
public record MonitorStateChangedMessage(bool IsMonitoring, string? Message = null);
