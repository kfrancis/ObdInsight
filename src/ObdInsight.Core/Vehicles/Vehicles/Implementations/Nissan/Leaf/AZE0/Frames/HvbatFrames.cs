using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.AZE0;

/// <summary>
/// Battery status broadcast frame for Nissan Leaf AZE0 platform (0x1DB)
/// </summary>
[CanFrame(0x1DB, Description = "Real-time battery voltage, current, and SOC")]
public partial class BatteryFrame_1DB_AZE0
{
    [CanSignal(56, 8,
        Description = "CRC checksum",
        MinValue = 0, MaxValue = 255)]
    public partial int Crc { get; init; }

    [CanSignal(13, 11, IsSigned = true, Factor = 0.5, Unit = "A",
        Description = "Battery current (positive=discharge, negative=charge)",
        MinValue = -400, MaxValue = 500)]
    public partial double Current { get; init; }

    [CanSignal(25, 2,
        Description = "Discharge power status (00b=Reserved, 01b=Normal limit PO, 10b=Below -20degC, 11b=)",
        MinValue = 0, MaxValue = 3)]
    public partial int DischargePowerStatus { get; init; }

    [CanSignal(8, 3,
                    Description = "Failsafe status indicator",
        MinValue = 0, MaxValue = 7)]
    public partial int FailsafeStatus { get; init; }

    [CanSignal(28, 1,
        Description = "Full charge flag",
        MinValue = 0, MaxValue = 1)]
    public partial bool FullChargeFlag { get; init; }

    [CanSignal(27, 1,
        Description = "Interlock status (0h=Not Inter Lock connected, 1h=Inter Lock connected)",
        MinValue = 0, MaxValue = 1)]
    public partial bool InterLock { get; init; }

    [CanSignal(29, 1,
        Description = "Main relay on flag (0h=No-Permission, 1h=Main Relay ON permission)",
        MinValue = 0, MaxValue = 1)]
    public partial bool MainRelayOnFlag { get; init; }

    [CanSignal(48, 2,
        Description = "MPRIDE detection of frozen data message",
        MinValue = 0, MaxValue = 3)]
    public partial int Prun { get; init; }

    [CanSignal(11, 2,
                        Description = "Relay cut request (00=No-Request, 01=Main Relay OFF request)",
        MinValue = 0, MaxValue = 3)]
    public partial int RelayCutRequest { get; init; }
    [CanSignal(32, 7,
        Description = "Usable SOC for dash display (LB_USABLE_SOC)",
        MinValue = 0, MaxValue = 100)]
    public partial int UsableSoc { get; init; }

    [CanSignal(30, 10, Factor = 0.5, Unit = "V",
        Description = "Battery pack total voltage (0.5V/bit)",
        MinValue = 0, MaxValue = 450)]
    public partial double Voltage { get; init; }

    [CanSignal(24, 1,
                Description = "Cell voltage latch flag (0->1: Cell Voltage Latch1->0: Cell Voltage)",
        MinValue = 0, MaxValue = 1)]
    public partial bool VoltageLatchFlag { get; init; }
}

/// <summary>
/// Battery power limits frame for Nissan Leaf AZE0 platform (0x1DC)
/// </summary>
[CanFrame(0x1DC, Description = "Battery charge/discharge power limits and status codes")]
public partial class BatteryFrame_1DC_AZE0
{
    [CanSignal(37, 3,
        Description = "BPC MAX Uprate Level 1-8 (controls how quickly VCM follows requested power)",
        MinValue = 0, MaxValue = 7)]
    public partial int BpcMaxUprate { get; init; }

    [CanSignal(20, 10, Factor = 0.25, Unit = "kW",
        Description = "Max power that battery can be charged with",
        MinValue = 0, MaxValue = 254)]
    public partial double ChargePowerLimit { get; init; }

    [CanSignal(24, 2,
        Description = "Charge power status (00b=Reserved, 01b=Normal limit PIN, 10b=High rate limit PIN, 11b=Immediate limit PIN)",
        MinValue = 0, MaxValue = 3)]
    public partial int ChargePowerStatus { get; init; }

    [CanSignal(42, 8,
        Description = "Diagnostic code 1",
        MinValue = 0, MaxValue = 255)]
    public partial int Code1 { get; init; }

    [CanSignal(34, 3,
        Description = "Code condition",
        MinValue = 0, MaxValue = 7)]
    public partial int CodeCondition { get; init; }

    [CanSignal(56, 8,
        Description = "CRC checksum",
        MinValue = 0, MaxValue = 255)]
    public partial int Crc { get; init; }

    [CanSignal(14, 10, Factor = 0.25, Unit = "kW",
                                Description = "Max available power that can be pulled from battery",
        MinValue = 0, MaxValue = 254)]
    public partial double DischargePowerLimit { get; init; }
    [CanSignal(26, 10, Factor = 0.1, Offset = -10.0, Unit = "kW",
        Description = "Maximum power for charger (LB_BPCMAX)",
        MinValue = -10, MaxValue = 90)]
    public partial double MaxPowerForCharger { get; init; }
    [CanSignal(48, 2,
        Description = "Detection of frozen data (Message-PRUN-Diag)",
        MinValue = 0, MaxValue = 3)]
    public partial int Prun { get; init; }
}

/// <summary>
/// Battery SOC and sensor data frame for Nissan Leaf AZE0 platform (0x55B)
/// </summary>
[CanFrame(0x55B, Description = "Battery state of charge and internal resistance sensor data")]
public partial class BatteryFrame_55B_AZE0
{
    [CanSignal(16, 8,
        Description = "HCM:B2h LBC:AAh HCM:5Dh LBC:55h - Backwards compatibility to 2011/2013 models",
        MinValue = 85, MaxValue = 170)]
    public partial int AluAnswer { get; init; }

    [CanSignal(55, 1,
        Description = "Battery capacity empty flag (0=Not Empty, 1=Battery Empty)",
        MinValue = 0, MaxValue = 1)]
    public partial bool CapacityEmpty { get; init; }

    [CanSignal(56, 8,
        Description = "CRC checksum",
        MinValue = 0, MaxValue = 255)]
    public partial int Crc { get; init; }

    [CanSignal(40, 1,
        Description = "IR sensor malfunction flag (0=Normal, 1=Malfunction)",
        MinValue = 0, MaxValue = 1)]
    public partial bool IrSensorMalfunction { get; init; }

    [CanSignal(39, 10, Unit = "mV",
        Description = "Internal resistance sensor wave voltage (5000/1024)",
        MinValue = 0, MaxValue = 4990)]
    public partial int IrSensorWaveVoltage { get; init; }

    [CanSignal(48, 2,
        Description = "Detection of frozen data (Message-PRUN-Diag)",
        MinValue = 0, MaxValue = 3)]
    public partial int Prun { get; init; }

    [CanSignal(53, 2,
        Description = "Sleep enabled status (0=Reserved, 1=RefuseToSleep, 2=ReadyToSleep, 3=Reserved)",
        MinValue = 0, MaxValue = 3)]
    public partial int SleepEnabled { get; init; }

    [CanSignal(0, 8,
        Description = "State of charge, raw high 8 bits (byte 0; Motorola DBC start-bit 7)",
        MinValue = 0, MaxValue = 255)]
    public partial int SocRawHigh { get; init; }

    [CanSignal(14, 2,
        Description = "State of charge, raw low 2 bits (byte 1 bits 7-6)",
        MinValue = 0, MaxValue = 3)]
    public partial int SocRawLow { get; init; }

    /// <summary>
    /// State of charge in 0.1% units (e.g. 928 = 92.8%). The DBC source is a Motorola-order
    /// 10-bit field starting at bit 7 (byte0[7..0] + byte1[7..6]), which cannot be expressed
    /// as a single Intel signal — recombined from the two raw parts.
    /// Hardware-verified 2026-07-18: raw E8 00 → 928 with pack near full (~96%).
    /// </summary>
    public int Soc => (SocRawHigh << 2) | SocRawLow;
}

/// <summary>
/// Battery quick charge capacity frame for Nissan Leaf AZE0 platform (0x59E)
/// </summary>
[CanFrame(0x59E, Description = "Battery full and remaining capacity for quick charge")]
public partial class BatteryFrame_59E_AZE0
{
    [CanSignal(20, 9, Factor = 100, Unit = "Wh",
        Description = "Full capacity for quick charge",
        MinValue = 0, MaxValue = 50000)]
    public partial int FullCapacityForQc { get; init; }

    [CanSignal(27, 9, Factor = 100, Unit = "Wh",
        Description = "Remaining capacity for quick charge",
        MinValue = 0, MaxValue = 50000)]
    public partial int RemainCapacityForQc { get; init; }
}

/// <summary>
/// Battery capacity and charge status frame for Nissan Leaf AZE0 platform (0x5BC)
/// </summary>
/// <remarks>
/// Partially multiplexed on 30 kWh AZE0. Reviewed in the 2026-07-18 frame-layout audit
/// (single capture 5D C0 F0 64 82 12 BF FF, parked, charging, ~96%):
/// <list type="bullet">
/// <item>GIDS fixed from a Motorola transcription error (decoded 384; now 375). Mux
/// semantics confirmed against OVMS vehicle_nissanleaf.cpp: <see cref="MaxGids"/> (byte 5
/// bit 4) selects the gids content — 0 = remaining gids, 1 = maximum gids / pack capacity
/// (30 kWh+ only). The capture had it set, so 375 = full capacity (375 × 80 Wh = 30.0 kWh).
/// 1023 (0x3FF) = invalid-during-startup sentinel.</item>
/// <item>RemainChargeTime fixed from a 12-bit misread (decoded 4091; now the documented
/// 13-bit field whose 0x1FFF sentinel = unavailable, matching this capture).</item>
/// <item>CapacityDeteriorationRate decoded 65% — plausible for an aged 30 kWh pack, but
/// unconfirmed (dash bars are not SOH%). Note it overlaps Mux/RemainCapSegmentSwitchFlag
/// in byte 4; which bits are valid may depend on mux state.</item>
/// </list>
/// </remarks>
[CanFrame(0x5BC, Description = "Battery remaining capacity in GIDS, charge bars, and temperature")]
public partial class BatteryFrame_5BC_AZE0
{
    [CanSignal(20, 4,
        Description = "Capacity bars (0-15, displays 0-12 bars on dash)",
        MinValue = 0, MaxValue = 15)]
    public partial int CapacityBars { get; init; }

    [CanSignal(33, 7, Unit = "%",
        Description = "SOH - State of Health / Capacity deterioration rate (affects charge gauge)",
        MinValue = 0, MaxValue = 100)]
    public partial int CapacityDeteriorationRate { get; init; }

    [CanSignal(20, 4,
        Description = "Charge bars (0-15, displays 0-12 bars on dash)",
        MinValue = 0, MaxValue = 15)]
    public partial int ChargeBars { get; init; }

    [CanSignal(44, 1,
        Description = "GIDS mux selector (byte 5 bit 4): 0 = RemainCapacityGids holds remaining gids, " +
                      "1 = it holds maximum gids / pack capacity (only broadcast on 30kWh+; confirmed vs OVMS)",
        MinValue = 0, MaxValue = 1)]
    public partial bool MaxGids { get; init; }

    [CanSignal(32, 4,
        Description = "Multiplexor for charge/capacity bars",
        MinValue = 0, MaxValue = 15)]
    public partial int Mux { get; init; }

    [CanSignal(45, 3,
        Description = "Power limit reason (0=Normal, 1=Capacity drop, 2=LBC Malfunction, 3=High temp, 4=Low temp)",
        MinValue = 0, MaxValue = 7)]
    public partial int OutputPowerLimitReason { get; init; }

    [CanSignal(0, 8,
        Description = "Remaining capacity GIDS, raw high 8 bits (byte 0; Motorola DBC start-bit 7)",
        MinValue = 0, MaxValue = 255)]
    public partial int RemainCapacityGidsRawHigh { get; init; }

    [CanSignal(14, 2,
        Description = "Remaining capacity GIDS, raw low 2 bits (byte 1 bits 7-6)",
        MinValue = 0, MaxValue = 3)]
    public partial int RemainCapacityGidsRawLow { get; init; }

    /// <summary>
    /// Capacity in GIDS (80Wh per GID). Motorola-order 10-bit field
    /// (byte0[7..0] + byte1[7..6]) recombined from the raw parts.
    /// Muxed by <see cref="MaxGids"/>: false = remaining gids, true = maximum gids
    /// (pack capacity, 30kWh+ only). 1023 (0x3FF) = invalid (startup) — check
    /// <see cref="GidsValid"/>.
    /// </summary>
    public int RemainCapacityGids => (RemainCapacityGidsRawHigh << 2) | RemainCapacityGidsRawLow;

    /// <summary>False while <see cref="RemainCapacityGids"/> holds the 0x3FF startup-invalid sentinel.</summary>
    public bool GidsValid => RemainCapacityGids != 0x3FF;

    [CanSignal(32, 1,
        Description = "Remaining capacity segment switch flag (0=Remaining capacity, 1=Full capacity)",
        MinValue = 0, MaxValue = 1)]
    public partial bool RemainCapSegmentSwitchFlag { get; init; }

    [CanSignal(48, 5,
        Description = "Remaining charge time, raw high 5 bits (byte 6 bits 4-0; Motorola DBC start-bit 52)",
        MinValue = 0, MaxValue = 31)]
    public partial int RemainChargeTimeRawHigh { get; init; }

    [CanSignal(56, 8,
        Description = "Remaining charge time, raw low 8 bits (byte 7)",
        MinValue = 0, MaxValue = 255)]
    public partial int RemainChargeTimeRawLow { get; init; }

    /// <summary>
    /// Remaining charge time in minutes. Motorola-order 13-bit field
    /// (byte6[4..0] + byte7[7..0]) recombined from the raw parts.
    /// 0x1FFF (8191) = unavailable sentinel — check <see cref="RemainChargeTimeAvailable"/>.
    /// </summary>
    public int RemainChargeTime => (RemainChargeTimeRawHigh << 8) | RemainChargeTimeRawLow;

    /// <summary>False when <see cref="RemainChargeTime"/> holds the 0x1FFF unavailable sentinel.</summary>
    public bool RemainChargeTimeAvailable => RemainChargeTime != 0x1FFF;

    [CanSignal(41, 5,
        Description = "Remaining charge time condition/mode (00000b=Quick charge, 01001b=Normal 200V SOC100%, etc.)",
        MinValue = 0, MaxValue = 30)]
    public partial int RemainChargeTimeCondition { get; init; }

    [CanSignal(16, 8,
                    Description = "Remaining capacity segments (contains charge bars and capacity bars, multiplexed)",
        MinValue = 0, MaxValue = 240)]
    public partial int RemainingCapacitySegments { get; init; }

    [CanSignal(24, 8, Factor = 0.4166666, Unit = "%",
        Description = "Temperature segment for instrumentation cluster (average of 3 battery sensors)",
        MinValue = 0, MaxValue = 100)]
    public partial double TemperatureSegmentForDash { get; init; }
}
/// <summary>
/// Battery historical data and heating control frame for Nissan Leaf AZE0 platform (0x5C0)
/// </summary>
[CanFrame(0x5C0, Description = "Battery historical data, heating control, and diagnostic trouble codes")]
public partial class BatteryFrame_5C0_AZE0
{
    [CanSignal(8, 1,
        Description = "Battery heater mail send request (0=No request, 1=Mail send request)",
        MinValue = 0, MaxValue = 1)]
    public partial bool BattHeaterMailSendRequest { get; init; }

    [CanSignal(56, 8, Unit = "DTC",
        Description = "Diagnosis trouble code (up to 2 error codes indicated concurrently, alternating every 500ms)",
        MinValue = 0, MaxValue = 255)]
    public partial int DiagnosisTroubleCode { get; init; }

    [CanSignal(32, 1,
        Description = "Battery heater exists flag (0=Without Battery Heating, 1=With Battery Heating)",
        MinValue = 0, MaxValue = 1)]
    public partial bool HeatExists { get; init; }

    [CanSignal(5, 1,
        Description = "Battery heating start send request (0->1 Heat start mail send request)",
        MinValue = 0, MaxValue = 1)]
    public partial bool HeatingStartSendRequest { get; init; }

    [CanSignal(4, 1,
        Description = "Battery heating stop send request (0->1 Heat stop mail send request)",
        MinValue = 0, MaxValue = 1)]
    public partial bool HeatingStopSendRequest { get; init; }

    [CanSignal(42, 6, Factor = 40, Offset = 1900, Unit = "mV",
        Description = "Historical data: Cell voltage (AVG when mux=2)",
        MinValue = 0, MaxValue = 4380)]
    public partial int HistDataCellVoltageAvg { get; init; }

    [CanSignal(42, 6, Factor = 40, Offset = 1900, Unit = "mV",
        Description = "Historical data: Cell voltage (MAX when mux=1)",
        MinValue = 0, MaxValue = 4380)]
    public partial int HistDataCellVoltageMax { get; init; }

    [CanSignal(42, 6, Factor = 40, Offset = 1900, Unit = "mV",
        Description = "Historical data: Cell voltage (MIN when mux=3)",
        MinValue = 0, MaxValue = 4380)]
    public partial int HistDataCellVoltageMin { get; init; }

    [CanSignal(33, 7, Unit = "%",
        Description = "Historical data: Degradation internal resistance coefficient (AVG when mux=2)",
        MinValue = 0, MaxValue = 100)]
    public partial int HistDataDegrIntResCoeffAvg { get; init; }

    [CanSignal(33, 7, Unit = "%",
        Description = "Historical data: Degradation internal resistance coefficient (MAX when mux=1)",
        MinValue = 0, MaxValue = 100)]
    public partial int HistDataDegrIntResCoeffMax { get; init; }

    [CanSignal(33, 7, Unit = "%",
        Description = "Historical data: Degradation internal resistance coefficient (MIN when mux=3)",
        MinValue = 0, MaxValue = 100)]
    public partial int HistDataDegrIntResCoeffMin { get; init; }

    [CanSignal(0, 4,
        Description = "Historical data: High/Low voltage times (AVG when mux=2)",
        MinValue = 0, MaxValue = 10)]
    public partial int HistDataHighLowVoltageTimeAvg { get; init; }

    [CanSignal(0, 4,
        Description = "Historical data: High/Low voltage times (MAX when mux=1)",
        MinValue = 0, MaxValue = 10)]
    public partial int HistDataHighLowVoltageTimeMax { get; init; }

    // Historical data signals (multiplexed based on HistoricalDataSwitchFlag)
    [CanSignal(0, 4,
        Description = "Historical data: High/Low voltage times (MIN when mux=3)",
        MinValue = 0, MaxValue = 10)]
    public partial int HistDataHighLowVoltageTimeMin { get; init; }

    [CanSignal(24, 8, Factor = 0.6, Unit = "Ah",
        Description = "Historical data: Integrated current (AVG when mux=2)",
        MinValue = -76.2, MaxValue = 76.2)]
    public partial double HistDataIntegratedCurrentAvg { get; init; }

    [CanSignal(24, 8, Factor = 0.6, Unit = "Ah",
        Description = "Historical data: Integrated current (MAX when mux=1)",
        MinValue = -76.2, MaxValue = 76.2)]
    public partial double HistDataIntegratedCurrentMax { get; init; }

    [CanSignal(24, 8, Factor = 0.6, Unit = "Ah",
        Description = "Historical data: Integrated current (MIN when mux=3)",
        MinValue = -76.2, MaxValue = 76.2)]
    public partial double HistDataIntegratedCurrentMin { get; init; }

    [CanSignal(17, 7, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature (AVG when mux=2)",
        MinValue = -40, MaxValue = 86)]
    public partial double HistDataTemperatureAvg { get; init; }

    [CanSignal(17, 7, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature (MAX when mux=1)",
        MinValue = -40, MaxValue = 86)]
    public partial double HistDataTemperatureMax { get; init; }

    [CanSignal(17, 7, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature (MIN when mux=3)",
        MinValue = -40, MaxValue = 86)]
    public partial double HistDataTemperatureMin { get; init; }

    [CanSignal(9, 7, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature wakeup phase (AVG when mux=2)",
        MinValue = -40, MaxValue = 86)]
    public partial double HistDataTempWakeupPhaseAvg { get; init; }

    [CanSignal(9, 7, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature wakeup phase (MAX when mux=1)",
        MinValue = -40, MaxValue = 86)]
    public partial double HistDataTempWakeupPhaseMax { get; init; }

    [CanSignal(9, 7, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature wakeup phase (MIN when mux=3)",
        MinValue = -40, MaxValue = 86)]
    public partial double HistDataTempWakeupPhaseMin { get; init; }

    [CanSignal(6, 2,
                                                                                                    Description = "Historical data switch flag (0=Not Calculated, 1=Maximum Data, 2=Average Data, 3=Minimum Data)",
        MinValue = 0, MaxValue = 3)]
    public partial int HistoricalDataSwitchFlag { get; init; }
    [CanSignal(48, 5, Unit = "minutes",
        Description = "Next wakeup time for battery heater",
        MinValue = 0, MaxValue = 1800)]
    public partial int NextWakeupTimeForBatteryHeater { get; init; }
}
