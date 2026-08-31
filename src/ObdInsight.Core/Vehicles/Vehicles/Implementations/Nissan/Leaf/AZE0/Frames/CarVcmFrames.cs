using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.AZE0;

/// <summary>
/// VCM power consumption and climate data frame for Nissan Leaf AZE0 platform (0x510)
/// Transmitted on CAR-CAN bus
/// </summary>
/// <remarks>
/// This frame contains integrated power consumption, climate control status, and ambient temperature.
/// VCM relays this data from A/C Auto Amp to eyebrow display and A/V unit.
/// </remarks>
[CanFrame(0x510, Description = "VCM power consumption, climate, and eco data (CAR-CAN)")]
public partial class VcmFrame_510_AZE0
{
    [CanSignal(10, 3,
        Description = "Charge mode indicator (0-3)",
        MinValue = 0, MaxValue = 3)]
    public partial int ChargeMode { get; init; }

    [CanSignal(31, 1,
        Description = "Climate control active flag",
        MinValue = 0, MaxValue = 1)]
    public partial bool ClimateControlActive { get; init; }

    [CanSignal(25, 6, Factor = 0.25, Unit = "kW",
        Description = "Climate control power consumption",
        MinValue = 0, MaxValue = 15.75)]
    public partial double ClimateControlPowerConsumption { get; init; }

    [CanSignal(19, 4,
        Description = "Eco indicator (0-15 scale)",
        MinValue = 0, MaxValue = 15)]
    public partial int EcoIndicator { get; init; }

    [CanSignal(47, 5,
        Description = "Eco tree growth level (0-31)",
        MinValue = 0, MaxValue = 31)]
    public partial int EcoTree { get; init; }

    [CanSignal(15, 5,
        Description = "Integrated A/C power consumption",
        MinValue = 0, MaxValue = 31)]
    public partial int IntegratedPowerConsumptionAc { get; init; }

    [CanSignal(23, 4,
        Description = "Integrated auxiliary power consumption",
        MinValue = 0, MaxValue = 15)]
    public partial int IntegratedPowerConsumptionAux { get; init; }

    [CanSignal(7, 8,
        Description = "Integrated motor power consumption (raw value)",
        MinValue = 0, MaxValue = 255)]
    public partial int IntegratedPowerConsumptionMotor { get; init; }

    [CanSignal(56, 8, Factor = 0.5, Offset = -40.0, Unit = "°C",
        Description = "Outside ambient temperature",
        MinValue = -40, MaxValue = 87.5)]
    public partial double OutsideAmbientTemperature { get; init; }

    [CanSignal(39, 5,
        Description = "Instantaneous auxiliary power consumption",
        MinValue = 0, MaxValue = 31)]
    public partial int PowerConsumptionAux { get; init; }
}

/// <summary>
/// VCM shifter relay frame for Nissan Leaf AZE0 platform (0x174)
/// Transmitted on CAR-CAN bus
/// </summary>
/// <remarks>
/// VCM relays shifter position data from E-Shift on EV-CAN to instrument panel and VSP.
/// Most signals are unknown/undecoded.
/// </remarks>
[CanFrame(0x174, Description = "VCM shifter relay data (CAR-CAN)")]
public partial class VcmFrame_174_AZE0
{
    [CanSignal(24, 8,
        Description = "Shifter position relay",
        MinValue = 0, MaxValue = 255)]
    public partial int ShifterPosition { get; init; }

    //[CanSignal(0, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 0")]
    //public partial int Unknown0 { get; init; }

    //[CanSignal(8, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 1")]
    //public partial int Unknown1 { get; init; }

    //[CanSignal(16, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 2")]
    //public partial int Unknown2 { get; init; }

    //[CanSignal(32, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 4")]
    //public partial int Unknown4 { get; init; }

    //[CanSignal(40, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 5")]
    //public partial int Unknown5 { get; init; }

    //[CanSignal(48, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 6")]
    //public partial int Unknown6 { get; init; }

    //[CanSignal(56, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 7")]
    //public partial int Unknown7 { get; init; }
}

/// <summary>
/// VCM motor RPM relay frame for Nissan Leaf AZE0 platform (0x176)
/// Transmitted on CAR-CAN bus
/// </summary>
/// <remarks>
/// VCM relays motor RPM data from inverter message on EV-CAN to instrument cluster.
/// Contains transmission input/output revolutions and ASCD (cruise control) speed request.
/// </remarks>
[CanFrame(0x176, Description = "VCM motor RPM relay (CAR-CAN, 7 bytes)")]
public partial class VcmFrame_176_AZE0
{
    [CanSignal(39, 8, Unit = "km/h",
        Description = "ASCD (cruise control) speed request",
        MinValue = 0, MaxValue = 250)]
    public partial int AscdSpeedRequest { get; init; }

    [CanSignal(48, 8,
        Description = "CRC checksum",
        MinValue = 0, MaxValue = 255)]
    public partial int Crc { get; init; }

    [CanSignal(7, 16, Unit = "rpm",
        Description = "Transmission output revolutions (absolute)",
        MinValue = 0, MaxValue = 11000)]
    public partial int TmOutputRevsAbs { get; init; }

    [CanSignal(23, 16, Unit = "rpm",
        Description = "Transmission input revolutions (absolute)",
        MinValue = 0, MaxValue = 11000)]
    public partial int TmInputRevsAbs { get; init; }

    //[CanSignal(40, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 6")]
    //public partial int Unknown6 { get; init; }
}

/// <summary>
/// VCM motor current and throttle frame for Nissan Leaf AZE0 platform (0x180)
/// Transmitted on CAR-CAN bus
/// </summary>
/// <remarks>
/// Contains motor current measurements and throttle position.
/// Motor current signals appear to be signed 12-bit values.
/// </remarks>
[CanFrame(0x180, Description = "VCM motor current and throttle (CAR-CAN)")]
public partial class VcmFrame_180_AZE0
{
    [CanSignal(23, 12, IsSigned = true,
        Description = "Motor current (amperes)",
        MinValue = -2048, MaxValue = 2047)]
    public partial int MotorAmp { get; init; }

    [CanSignal(27, 12, IsSigned = true,
        Description = "Alternative motor current measurement (amperes)",
        MinValue = -2048, MaxValue = 2047)]
    public partial int MotorAmpAlternative { get; init; }

    [CanSignal(40, 8, Factor = 0.5, Unit = "%",
        Description = "Throttle position",
        MinValue = 0, MaxValue = 100)]
    public partial double ThrottlePosition { get; init; }

    //[CanSignal(0, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 0")]
    //public partial int Unknown0 { get; init; }

    //[CanSignal(8, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 1")]
    //public partial int Unknown1 { get; init; }

    //[CanSignal(48, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 5")]
    //public partial int Unknown5 { get; init; }

    //[CanSignal(56, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 6")]
    //public partial int Unknown6 { get; init; }
}

/// <summary>
/// VCM motor power data frame for Nissan Leaf AZE0 platform (0x260)
/// Transmitted on CAR-CAN bus
/// </summary>
/// <remarks>
/// Contains motor power consumption and available power limits.
/// Power values include both drive and regeneration modes.
/// </remarks>
[CanFrame(0x260, Description = "VCM motor power data (CAR-CAN, 4 bytes)")]
public partial class VcmFrame_260_AZE0
{
    [CanSignal(6, 7, Unit = "kW",
        Description = "Available motor power",
        MinValue = 0, MaxValue = 90)]
    public partial int AvailableMotorPower { get; init; }

    [CanSignal(14, 7, Unit = "kW",
        Description = "Maximum motor regeneration power",
        MinValue = 0, MaxValue = 50)]
    public partial int MotorRegenerationPowerMax { get; init; }

    [CanSignal(23, 12, Factor = 0.05, Offset = -100.0, Unit = "kW",
        Description = "Motor power consumption (negative for regen)",
        MinValue = -100, MaxValue = 90)]
    public partial double PowerConsumptMotor { get; init; }
}

/// <summary>
/// VCM dashboard shifter position frame for Nissan Leaf AZE0 platform (0x421)
/// Transmitted on CAR-CAN bus
/// </summary>
/// <remarks>
/// VCM relays shifter position to instrument panel and VSP for dashboard display.
/// Single-byte frame on the wire; the generated <c>Parse</c> handles it because the frame's
/// only signal lives in byte 0 (<c>MinimumLength</c> = 1). Value map confirmed against OVMS
/// vehicle_nissanleaf.cpp (case 0x421): 0/1=Park, 2=Reverse, 3=Neutral, 4=Drive,
/// 7=Drive/B (Eco), 5/6=undefined.
/// </remarks>
[CanFrame(0x421, Description = "VCM dashboard shifter position (CAR-CAN, 1 byte)")]
public partial class VcmFrame_421_AZE0
{
    [CanSignal(3, 3,
        Description = "Shifter position (byte 0 bits 3-5): 0/1=Park, 2=Reverse, 3=Neutral, 4=Drive, 7=Drive/B",
        MinValue = 0, MaxValue = 7)]
    public partial int DashShifterPosition { get; init; }
}

/// <summary>
/// Battery state-of-health relay frame for Nissan Leaf AZE0 platform (0x5B3)
/// Transmitted on CAR-CAN bus
/// </summary>
/// <remarks>
/// Layout from OVMS vehicle_nissanleaf.cpp (case 0x5b3): SOH% = byte1 &gt;&gt; 1, 0 = invalid.
/// OVMS treats this as the SOH source on non-ZE1 cars (it only trusts the 0x5BC byte-4 SOH
/// on 24 kWh ZE0). Hardware sample 2025-12-06 (third-party app log, same 2017 30 kWh AZE0):
/// 50 84 FF FB 20 B5 A1 8A → SOH 66%, consistent with the 0x5BC read of 65%.
/// </remarks>
[CanFrame(0x5B3, Description = "Battery SOH relay from LBC (CAR-CAN)")]
public partial class VcmFrame_5B3_AZE0
{
    [CanSignal(9, 7, Unit = "%",
        Description = "Battery state of health (byte 1 bits 1-7; 0 = invalid)",
        MinValue = 0, MaxValue = 100)]
    public partial int Soh { get; init; }

    /// <summary>False while <see cref="Soh"/> is 0 (sender has no valid SOH yet).</summary>
    public bool SohValid => Soh != 0;
}

/// <summary>
/// VCM dashboard indicator lights frame for Nissan Leaf AZE0 platform (0x50D)
/// Transmitted on CAR-CAN bus
/// </summary>
/// <remarks>
/// VCM relays indicator light status to eyebrow display and A/V unit.
/// Contains READY lamp, charge lamp, and EV system warning light signals.
/// </remarks>
[CanFrame(0x50D, Description = "VCM dashboard indicator lights (CAR-CAN)")]
public partial class VcmFrame_50D_AZE0
{
    [CanSignal(47, 2,
        Description = "Charge lamp signal (0-3 scale)",
        MinValue = 0, MaxValue = 3)]
    public partial int ChargeLampSignal { get; init; }

    [CanSignal(45, 2,
        Description = "EV system warning light (0-3 scale)",
        MinValue = 0, MaxValue = 3)]
    public partial int EvSystemWarningLight { get; init; }

    [CanSignal(23, 2,
        Description = "READY lamp signal (0-3 scale)",
        MinValue = 0, MaxValue = 3)]
    public partial int ReadyLampSignal { get; init; }

    //[CanSignal(0, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 0")]
    //public partial int Unknown0 { get; init; }

    //[CanSignal(8, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 1")]
    //public partial int Unknown1 { get; init; }

    //[CanSignal(24, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 3")]
    //public partial int Unknown3 { get; init; }

    //[CanSignal(32, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 4")]
    //public partial int Unknown4 { get; init; }

    //[CanSignal(48, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 6")]
    //public partial int Unknown6 { get; init; }

    //[CanSignal(56, 8, IncludeInGeneration = false,
    //    Description = "Unknown signal 7")]
    //public partial int Unknown7 { get; init; }
}
