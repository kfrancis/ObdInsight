using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.AZE0;

/// <summary>
/// Battery status broadcast frame for Nissan Leaf AZE0 platform (0x1DB)
/// </summary>
/// <remarks>
/// Current/Voltage layouts fixed 2026-07-18 against OVMS vehicle_nissanleaf.cpp
/// (case 0x1db) — the previous Intel transcriptions of Motorola DBC start bits read the
/// wrong bytes. Both fields cross byte boundaries, so they're raw-part signals recombined
/// in computed properties. No hardware capture exists (EV-CAN, not visible on stock ELM327
/// adapters); OVMS is the reference. The remaining flag/status signals in this frame are
/// unverified transcriptions — treat with suspicion until checked against a reference.
/// </remarks>
[CanFrame(0x1DB, Description = "Real-time battery voltage, current, and SOC")]
public partial class BatteryFrame_1DB_AZE0
{
    [CanSignal(56, 8,
        Description = "CRC checksum",
        MinValue = 0, MaxValue = 255)]
    public partial int Crc { get; init; }

    [CanSignal(0, 8,
        Description = "Battery current, raw high 8 bits (byte 0; bit 7 = sign)",
        MinValue = 0, MaxValue = 255)]
    public partial int CurrentRawHigh { get; init; }

    [CanSignal(13, 3,
        Description = "Battery current, raw low 3 bits (byte 1 bits 7-5)",
        MinValue = 0, MaxValue = 7)]
    public partial int CurrentRawLow { get; init; }

    /// <summary>
    /// Battery current in A. 11-bit two's complement (byte0 + byte1[7..5]), 0.5 A/bit,
    /// per OVMS. Wire sign convention is unverified on hardware: OVMS negates this raw
    /// value to report discharge as positive — cross-check against the BMS UDS current
    /// (known-good: negative while charging) before relying on the sign.
    /// </summary>
    public double Current
    {
        get
        {
            var raw = (CurrentRawHigh << 3) | CurrentRawLow;
            if ((raw & 0x400) != 0) raw -= 0x800;
            return raw * 0.5;
        }
    }

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
        Description = "Usable SOC for dash display (byte 4 bits 6-0; 0x7F = invalid; confirmed vs OVMS)",
        MinValue = 0, MaxValue = 100)]
    public partial int UsableSoc { get; init; }

    /// <summary>False while <see cref="UsableSoc"/> holds the 0x7F invalid sentinel (always the case on ZE1).</summary>
    public bool UsableSocValid => UsableSoc != 0x7F;

    [CanSignal(16, 8,
        Description = "Battery pack voltage, raw high 8 bits (byte 2)",
        MinValue = 0, MaxValue = 255)]
    public partial int VoltageRawHigh { get; init; }

    [CanSignal(30, 2,
        Description = "Battery pack voltage, raw low 2 bits (byte 3 bits 7-6)",
        MinValue = 0, MaxValue = 3)]
    public partial int VoltageRawLow { get; init; }

    /// <summary>
    /// Battery pack total voltage in V. 10-bit unsigned (byte2 + byte3[7..6]), 0.5 V/bit,
    /// per OVMS.
    /// </summary>
    public double Voltage => ((VoltageRawHigh << 2) | VoltageRawLow) * 0.5;

    [CanSignal(24, 1,
                Description = "Cell voltage latch flag (0->1: Cell Voltage Latch1->0: Cell Voltage)",
        MinValue = 0, MaxValue = 1)]
    public partial bool VoltageLatchFlag { get; init; }
}

/// <summary>
/// Battery power limits frame for Nissan Leaf AZE0 platform (0x1DC)
/// </summary>
/// <remarks>
/// Power-limit layouts fixed 2026-07-18 against OVMS vehicle_nissanleaf.cpp (case 0x1dc):
/// discharge = (byte0&lt;&lt;2 | byte1&gt;&gt;6)/4, charge = ((byte1&amp;0x3F)&lt;&lt;2 | byte2&gt;&gt;4)/4,
/// charger max = ((byte2&amp;0x0F)&lt;&lt;6 | byte3&gt;&gt;2)/10. All cross byte boundaries →
/// raw-part signals + computed properties. No hardware capture (EV-CAN). The −10 kW offset
/// on MaxPowerForCharger comes from the original DBC source; OVMS applies no offset —
/// unresolved, verify on hardware before trusting absolute values.
/// The remaining status signals are unverified transcriptions.
/// </remarks>
[CanFrame(0x1DC, Description = "Battery charge/discharge power limits and status codes")]
public partial class BatteryFrame_1DC_AZE0
{
    [CanSignal(37, 3,
        Description = "BPC MAX Uprate Level 1-8 (controls how quickly VCM follows requested power)",
        MinValue = 0, MaxValue = 7)]
    public partial int BpcMaxUprate { get; init; }

    [CanSignal(8, 6,
        Description = "Charge power limit, raw high 6 bits (byte 1 bits 5-0)",
        MinValue = 0, MaxValue = 63)]
    public partial int ChargePowerLimitRawHigh { get; init; }

    [CanSignal(20, 4,
        Description = "Charge power limit, raw low 4 bits (byte 2 bits 7-4)",
        MinValue = 0, MaxValue = 15)]
    public partial int ChargePowerLimitRawLow { get; init; }

    /// <summary>Max power the battery can be charged with, in kW
    /// (byte1[5..0]+byte2[7..4], 0.25 kW/bit, per OVMS).</summary>
    public double ChargePowerLimit =>
        ((ChargePowerLimitRawHigh << 4) | ChargePowerLimitRawLow) * 0.25;

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

    [CanSignal(0, 8,
        Description = "Discharge power limit, raw high 8 bits (byte 0)",
        MinValue = 0, MaxValue = 255)]
    public partial int DischargePowerLimitRawHigh { get; init; }

    [CanSignal(14, 2,
        Description = "Discharge power limit, raw low 2 bits (byte 1 bits 7-6)",
        MinValue = 0, MaxValue = 3)]
    public partial int DischargePowerLimitRawLow { get; init; }

    /// <summary>Max available power that can be pulled from the battery, in kW
    /// (byte0+byte1[7..6], 0.25 kW/bit, per OVMS).</summary>
    public double DischargePowerLimit =>
        ((DischargePowerLimitRawHigh << 2) | DischargePowerLimitRawLow) * 0.25;

    [CanSignal(16, 4,
        Description = "Max power for charger, raw high 4 bits (byte 2 bits 3-0)",
        MinValue = 0, MaxValue = 15)]
    public partial int MaxPowerForChargerRawHigh { get; init; }

    [CanSignal(26, 6,
        Description = "Max power for charger, raw low 6 bits (byte 3 bits 7-2)",
        MinValue = 0, MaxValue = 63)]
    public partial int MaxPowerForChargerRawLow { get; init; }

    /// <summary>Maximum power for charger (LB_BPCMAX) in kW
    /// (byte2[3..0]+byte3[7..2], 0.1 kW/bit, offset −10 per the DBC source; OVMS applies
    /// no offset — see class remarks).</summary>
    public double MaxPowerForCharger =>
        ((MaxPowerForChargerRawHigh << 6) | MaxPowerForChargerRawLow) * 0.1 - 10.0;
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

    // EV-can_AZE0.dbc: LB_IR_Sensor_Wave_Voltage : 39|10@0+ — Motorola. Decoded as Intel until
    // 2026-08-31, which read a different 10 bits and returned 769 instead of 910.
    [CanSignal(39, 10, ByteOrder = CanByteOrder.Motorola, Unit = "mV",
        Description = "Internal resistance sensor wave voltage (5000/1024)",
        MinValue = 0, MaxValue = 4990)]
    public partial int IrSensorWaveVoltage { get; init; }

    [CanSignal(48, 2,
        Description = "Detection of frozen data (Message-PRUN-Diag)",
        MinValue = 0, MaxValue = 3)]
    public partial int Prun { get; init; }

    // EV-can_AZE0.dbc: LB_SleepEnabled : 53|2@0+ — Motorola. Decoded as Intel until 2026-08-31,
    // which returned 0 on a captured frame. 0 is documented as Reserved, i.e. not a state the
    // controller reports; Motorola gives 1 = RefuseToSleep, correct for a vehicle that was awake.
    [CanSignal(53, 2, ByteOrder = CanByteOrder.Motorola,
        Description = "Sleep enabled status (0=Reserved, 1=RefuseToSleep, 2=ReadyToSleep, 3=Reserved)",
        MinValue = 0, MaxValue = 3)]
    public partial int SleepEnabled { get; init; }

    /// <summary>
    /// State of charge in 0.1% units (e.g. 928 = 92.8%).
    /// </summary>
    /// <remarks>
    /// EV-can_AZE0.dbc: <c>LB_SOC : 7|10@0+</c>. This was previously split into two Intel
    /// signals (byte 0, plus byte 1 bits 7-6) and recombined by hand, because a Motorola field
    /// could not be expressed directly. It now maps straight onto the DBC position.
    /// Hardware-verified: raw E8 00 -> 928 (pack ~96% full, 2026-07-18) and F3 00 -> 972
    /// (2026-08-31). Reading the same bits as Intel returns 1.
    /// </remarks>
    [CanSignal(7, 10, ByteOrder = CanByteOrder.Motorola, Unit = "0.1%",
        Description = "Battery state of charge",
        MinValue = 0, MaxValue = 1000)]
    public partial int Soc { get; init; }
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
        MinValue = 0, MaxValue = 4380, MuxValue = 2)]
    public partial int? HistDataCellVoltageAvg { get; init; }

    [CanSignal(42, 6, Factor = 40, Offset = 1900, Unit = "mV",
        Description = "Historical data: Cell voltage (MAX when mux=1)",
        MinValue = 0, MaxValue = 4380, MuxValue = 1)]
    public partial int? HistDataCellVoltageMax { get; init; }

    [CanSignal(42, 6, Factor = 40, Offset = 1900, Unit = "mV",
        Description = "Historical data: Cell voltage (MIN when mux=3)",
        MinValue = 0, MaxValue = 4380, MuxValue = 3)]
    public partial int? HistDataCellVoltageMin { get; init; }

    [CanSignal(33, 7, Unit = "%",
        Description = "Historical data: Degradation internal resistance coefficient (AVG when mux=2)",
        MinValue = 0, MaxValue = 100, MuxValue = 2)]
    public partial int? HistDataDegrIntResCoeffAvg { get; init; }

    [CanSignal(33, 7, Unit = "%",
        Description = "Historical data: Degradation internal resistance coefficient (MAX when mux=1)",
        MinValue = 0, MaxValue = 100, MuxValue = 1)]
    public partial int? HistDataDegrIntResCoeffMax { get; init; }

    [CanSignal(33, 7, Unit = "%",
        Description = "Historical data: Degradation internal resistance coefficient (MIN when mux=3)",
        MinValue = 0, MaxValue = 100, MuxValue = 3)]
    public partial int? HistDataDegrIntResCoeffMin { get; init; }

    [CanSignal(0, 4,
        Description = "Historical data: High/Low voltage times (AVG when mux=2)",
        MinValue = 0, MaxValue = 10, MuxValue = 2)]
    public partial int? HistDataHighLowVoltageTimeAvg { get; init; }

    [CanSignal(0, 4,
        Description = "Historical data: High/Low voltage times (MAX when mux=1)",
        MinValue = 0, MaxValue = 10, MuxValue = 1)]
    public partial int? HistDataHighLowVoltageTimeMax { get; init; }

    // Historical data signals (multiplexed based on HistoricalDataSwitchFlag)
    [CanSignal(0, 4,
        Description = "Historical data: High/Low voltage times (MIN when mux=3)",
        MinValue = 0, MaxValue = 10, MuxValue = 3)]
    public partial int? HistDataHighLowVoltageTimeMin { get; init; }

    // 8-bit SIGNED: byte 3 is only ever 0x00 or 0xFF across 447 captured frames. Unsigned that
    // reads 0 or 153 Ah, more than the pack holds; signed it is 0 or -0.6 Ah. The DBC declares
    // the field "+" but gives its range as [-76.2..76.2], which is exactly +/-127 x 0.6 - the
    // range is right and the sign marker is wrong.
    [CanSignal(24, 8, Factor = 0.6, IsSigned = true, Unit = "Ah",
        Description = "Historical data: Integrated current (AVG when mux=2)",
        MinValue = -76.2, MaxValue = 76.2, MuxValue = 2)]
    public partial double? HistDataIntegratedCurrentAvg { get; init; }

    [CanSignal(24, 8, Factor = 0.6, IsSigned = true, Unit = "Ah",
        Description = "Historical data: Integrated current (MAX when mux=1)",
        MinValue = -76.2, MaxValue = 76.2, MuxValue = 1)]
    public partial double? HistDataIntegratedCurrentMax { get; init; }

    [CanSignal(24, 8, Factor = 0.6, IsSigned = true, Unit = "Ah",
        Description = "Historical data: Integrated current (MIN when mux=3)",
        MinValue = -76.2, MaxValue = 76.2, MuxValue = 3)]
    public partial double? HistDataIntegratedCurrentMin { get; init; }

    // Temperature is the full byte 2 at 0.5 °C/bit − 40 (bottom bit always 0, so effective
    // 7-bit precision) — confirmed vs OVMS (case 0x5c0: d[2]/2 − 40). The previous (17,7)
    // definition halved the value a second time.
    [CanSignal(16, 8, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature (AVG when mux=2)",
        MinValue = -40, MaxValue = 87.5, MuxValue = 2)]
    public partial double? HistDataTemperatureAvg { get; init; }

    [CanSignal(16, 8, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature (MAX when mux=1)",
        MinValue = -40, MaxValue = 87.5, MuxValue = 1)]
    public partial double? HistDataTemperatureMax { get; init; }

    [CanSignal(16, 8, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature (MIN when mux=3)",
        MinValue = -40, MaxValue = 87.5, MuxValue = 3)]
    public partial double? HistDataTemperatureMin { get; init; }

    [CanSignal(9, 7, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature wakeup phase (AVG when mux=2)",
        MinValue = -40, MaxValue = 86, MuxValue = 2)]
    public partial double? HistDataTempWakeupPhaseAvg { get; init; }

    [CanSignal(9, 7, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature wakeup phase (MAX when mux=1)",
        MinValue = -40, MaxValue = 86, MuxValue = 1)]
    public partial double? HistDataTempWakeupPhaseMax { get; init; }

    [CanSignal(9, 7, Factor = 0.5, Offset = -40, Unit = "degC",
        Description = "Historical data: Temperature wakeup phase (MIN when mux=3)",
        MinValue = -40, MaxValue = 86, MuxValue = 3)]
    public partial double? HistDataTempWakeupPhaseMin { get; init; }

    // The multiplexor. Every HistData* signal above shares its bit positions with two siblings
    // and is distinguished only by this flag: the same bytes carry the maximum, average or
    // minimum of the battery's recorded history depending on its value. Before mux support
    // existed all three variants of each group decoded the same bits and returned identical
    // values, so two thirds of them were wrong on every frame.
    [CanSignal(6, 2, IsMultiplexor = true,
        Description = "Historical data switch flag (0=Not Calculated, 1=Maximum Data, 2=Average Data, 3=Minimum Data)",
        MinValue = 0, MaxValue = 3)]
    public partial int HistoricalDataSwitchFlag { get; init; }
    [CanSignal(48, 5, Unit = "minutes",
        Description = "Next wakeup time for battery heater",
        MinValue = 0, MaxValue = 1800)]
    public partial int NextWakeupTimeForBatteryHeater { get; init; }
}
