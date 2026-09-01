using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.AZE0;

/// <summary>
///     VCM shift controller frame for Nissan Leaf AZE0 platform (0x11A)
/// </summary>
[CanFrame(0x11A, Description = "Vehicle Control Module shift controller and status (10ms)")]
public partial class VcmFrame_11A_AZE0
{
    [CanSignal(15, 2,
        Description = "Car on/off status (4=CarOff, 8=CarOn)",
        MinValue = 0, MaxValue = 3)]
    public partial int CarOnOffStatus { get; init; }

    [CanSignal(12, 1,
        Description = "ECO mode selected flag (1=ECO active, regen increased)",
        MinValue = 0, MaxValue = 1)]
    public partial bool EcoSelected { get; init; }

    [CanSignal(9, 2,
        Description = "Electric shift system status (0=NO_ERROR, 1=SBW_Trq_Down_Req, 3=ELECTRIC_SHIFT_SYSTEM)",
        MinValue = 0, MaxValue = 3)]
    public partial int ElectricShiftSystem { get; init; }

    [CanSignal(24, 8,
        Description = "Heartbeat from VCM (alternates between 0x55 and 0xAA)",
        MinValue = 85, MaxValue = 170)]
    public partial int HeartbeatVcm { get; init; }

    [CanSignal(4, 4,
        Description = "Joystick gear position (0=Parked, 2=Reverse, 3=Neutral, 4=Drive/B)",
        MinValue = 0, MaxValue = 15)]
    public partial int JoystickGearPosition { get; init; }

    [CanSignal(48, 8,
        Description = "Multiplexor for startup data",
        MinValue = 0, MaxValue = 255)]
    public partial int Multiplexor { get; init; }

    [CanSignal(56, 8,
        Description = "Startup data (varies by multiplexor value)",
        MinValue = 0, MaxValue = 255)]
    public partial int StartupData { get; init; }
}

/// <summary>
///     VCM motor control frame for Nissan Leaf AZE0 platform (0x1D4)
/// </summary>
[CanFrame(0x1D4, Description = "Vehicle Control Module motor torque control and status (10ms)")]
public partial class VcmFrame_1D4_AZE0
{
    [CanSignal(53, 1,
        Description = "Brake pedal pressed flag",
        MinValue = 0, MaxValue = 1)]
    public partial bool BrakePedalPressed { get; init; }

    [CanSignal(52, 5,
        Description = "Charge status (140=Charging interrupted, 224=Charging)",
        MinValue = 0, MaxValue = 31)]
    public partial int ChargeStatus { get; init; }

    [CanSignal(56, 8,
        Description = "CRC checksum",
        MinValue = 0, MaxValue = 255)]
    public partial int Crc { get; init; }

    [CanSignal(55, 2,
        Description = "Gear shift inhibitor request",
        MinValue = 0, MaxValue = 3)]
    public partial int GearShiftInhibitorReq { get; init; }

    [CanSignal(38, 2,
        Description = "HCM clock counter (PRUN detection)",
        MinValue = 0, MaxValue = 3)]
    public partial int HcmClock { get; init; }

    [CanSignal(42, 2,
        Description = "Inhibitor position",
        MinValue = 0, MaxValue = 3)]
    public partial int InhibitorPos { get; init; }

    [CanSignal(15, 8, Factor = -2.5, Unit = "Nm",
        Description = "Motor torque limit lower bound (regen)",
        MinValue = -637.5, MaxValue = 0)]
    public partial double MotorTqLimitLower { get; init; }

    [CanSignal(7, 8, Factor = 2.5, Unit = "Nm",
        Description = "Motor torque limit upper bound",
        MinValue = 0, MaxValue = 637.5)]
    public partial double MotorTqLimitUpper { get; init; }

    [CanSignal(45, 2,
        Description = "Motor vibration constant status",
        MinValue = 0, MaxValue = 3)]
    public partial int MotorVibrationConstStat { get; init; }

    [CanSignal(46, 1,
        Description = "Relay plus output status (0=not output, 1=Main Relay Plus ON)",
        MinValue = 0, MaxValue = 1)]
    public partial bool RelayPlusOutputStatus { get; init; }

    [CanSignal(34, 1,
        Description = "Status of high voltage power supply (0=not supplied, 1=supplied)",
        MinValue = 0, MaxValue = 1)]
    public partial bool StatusOfHighVoltagePowerSupply { get; init; }

    [CanSignal(23, 12, IsSigned = true, Factor = 0.25, Unit = "Nm",
        Description = "Target motor torque sent to inverter",
        MinValue = -512, MaxValue = 511.75)]
    public partial double TargetMotorTorque { get; init; }
}

/// <summary>
///     VCM charging control frame for Nissan Leaf AZE0 platform (0x1F2)
/// </summary>
[CanFrame(0x1F2, Description = "Vehicle Control Module charging control and DC-DC converter (10ms)")]
public partial class VcmFrame_1F2_AZE0
{
    [CanSignal(21, 2,
        Description = "Charge status transition request (0=other, 1=Normal Charge, 2=Quick Charge, 3=Stop Request)",
        MinValue = 0, MaxValue = 3)]
    public partial int ChargeStatusTransitionRequest { get; init; }

    [CanSignal(56, 4,
        Description = "Checksum (all nibbles summed + 2, masked with 0xF)",
        MinValue = 0, MaxValue = 15)]
    public partial int Csum { get; init; }

    [CanSignal(31, 6, Factor = 0.1, Offset = -10.0, Unit = "V",
        Description = "DC-DC converter requested voltage",
        MinValue = -10, MaxValue = 53)]
    public partial double DcDcConverterReqVoltage { get; init; }

    [CanSignal(1, 10, Factor = 0.1, Offset = -10.0, Unit = "kW",
        Description = "HV battery chargeable power",
        MinValue = -10, MaxValue = 90)]
    public partial double HvBatChargeablePower { get; init; }

    [CanSignal(20, 1,
        Description = "Keep SOC request (0=Normal charge, 1=Keep SOC charge mode for battery heating)",
        MinValue = 0, MaxValue = 1)]
    public partial bool KeepSocRequest { get; init; }

    [CanSignal(48, 2,
        Description = "Message PRUN counter",
        MinValue = 0, MaxValue = 3)]
    public partial int Mprun { get; init; }

    [CanSignal(17, 1,
        Description = "PCS connector detection (0=other, 1=Vehicle-to-Home mode)",
        MinValue = 0, MaxValue = 1)]
    public partial bool PcsConnectorDetection { get; init; }

    [CanSignal(7, 1,
        Description = "Target charge SOC (0=100%, 1=80% deterioration restraint)",
        MinValue = 0, MaxValue = 1)]
    public partial bool TargetChargeSoc { get; init; }

    [CanSignal(63, 1,
        Description = "Unknown bit (may indicate charging)",
        MinValue = 0, MaxValue = 1)]
    public partial bool UnknownBit { get; init; }

    [CanSignal(47, 8,
        Description = "VCM mode",
        MinValue = 0, MaxValue = 255)]
    public partial int VcmMode { get; init; }
}

// NOTE: VcmFrame_284_AZE0 was removed in the 2026-07-18 frame-layout audit. It duplicated
// CAN 0x284 with a conflicting layout; AbsFrame_284_AZE0 (Leaf.AZE0.Frames namespace) is
// the canonical decoder for 0x284.

/// <summary>
///     VCM A/C and climate sensor relay frame for Nissan Leaf AZE0 platform (0x50A)
/// </summary>
/// <remarks>
///     This frame is identical on EV-CAN and CAR-CAN. It relays data from A/C Auto Amp and AC Pressure Sensor.
///     Unknown/undecoded signals:
///     - Byte 0: Unknown data (values: 04, 84, 85)
///     - Byte 1: Unknown data (values: 02, 13, 33, 40, 42, 53, 72, 73)
///     - Byte 2: Unknown data (values: 00, a0)
///     - Byte 4: Status bits (00, 80 - BIT1 = Rear defrost on/off)
///     - Byte 5: Unknown data (a0)
///     - Byte 6: Unknown data (04 - only present in 2013+)
///     - Byte 7: Unknown data (00 - only present in 2013+)
/// </remarks>
[CanFrame(0x50A, Description = "VCM relay from A/C Auto Amp and AC Pressure Sensor (100ms)")]
public partial class VcmFrame_50A_AZE0
{
    [CanSignal(24, 8, Unit = "pressure",
        Description = "A/C compressor pressure/temperature (rises with AC on, slow decay when off)",
        MinValue = 44, MaxValue = 68)]
    public partial int AcCompressorPressure { get; init; }
}

/// <summary>
///     VCM diagnostic and sleep control frame for Nissan Leaf AZE0 platform (0x50B)
/// </summary>
[CanFrame(0x50B, Description = "Vehicle Control Module diagnostic and sleep control (100ms)")]
public partial class VcmFrame_50B_AZE0
{
    [CanSignal(53, 1,
        Description = "Battery heater mail send OK flag (0=Mail send NG, 1=Mail send OK)",
        MinValue = 0, MaxValue = 1)]
    public partial bool BattHeaterMailSendOk { get; init; }

    [CanSignal(18, 1,
        Description = "Diagnostic mux on VCM (0=not authorized, 1=authorized for CAN mute/absent failures)",
        MinValue = 0, MaxValue = 1)]
    public partial bool DiagMuxOnVcm { get; init; }

    [CanSignal(30, 2,
        Description = "HCM wake/sleep command (0=GoToSleep, 3=WakeUp)",
        MinValue = 0, MaxValue = 3)]
    public partial int HcmWakeUpSleepCmd { get; init; }

    [CanSignal(17, 2,
        Description = "VCM activation status (0=NON, 2=READY)",
        MinValue = 0, MaxValue = 3)]
    public partial int VcmActivation { get; init; }
}

/// <summary>
///     VCM authentication frame for Nissan Leaf AZE0 platform (0x50C)
/// </summary>
[CanFrame(0x50C, Description = "Vehicle Control Module authentication question for LBC (100ms)")]
public partial class VcmFrame_50C_AZE0
{
    [CanSignal(32, 8,
        Description = "ALU question for LBC (0xB2=first question, 0x5D=second question)",
        MinValue = 0, MaxValue = 255)]
    public partial int AluQuestionForLbc { get; init; }

    [CanSignal(40, 8,
        Description = "CRC checksum",
        MinValue = 0, MaxValue = 255)]
    public partial int Crc { get; init; }

    [CanSignal(24, 2,
        Description = "HCM clock counter (PRUN detection)",
        MinValue = 0, MaxValue = 3)]
    public partial int HcmClock { get; init; }
}

/// <summary>
///     VCM range and warning status frame for Nissan Leaf AZE0 platform (0x5A9)
/// </summary>
[CanFrame(0x5A9, Description = "Vehicle Control Module range estimate and battery warnings (100ms)")]
public partial class VcmFrame_5A9_AZE0
{
    [CanSignal(57, 1,
        Description = "Charging disabled (battery lease contract)",
        MinValue = 0, MaxValue = 1)]
    public partial bool ChargingDisabled { get; init; }

    [CanSignal(43, 1,
        Description = "Critical battery warning (GOM flash)",
        MinValue = 0, MaxValue = 1)]
    public partial bool CriticalBattery { get; init; }

    [CanSignal(0, 2,
        Description = "ECO mode active status (1=ECO Off, 2=ECO On, 255=Charging)",
        MinValue = 0, MaxValue = 3)]
    public partial int EcoModeActive { get; init; }

    [CanSignal(16, 1,
        Description = "Low battery warning (message and tell-tale)",
        MinValue = 0, MaxValue = 1)]
    public partial bool LowBattery { get; init; }

    [CanSignal(15, 12, Factor = 0.2, Unit = "km",
        Description = "Range displayed on instrument cluster (0xFFF when charging)",
        MinValue = 0, MaxValue = 819)]
    public partial double RangeInstrumentCluster { get; init; }
}

/// <summary>
///     VCM bootup message frame for Nissan Leaf AZE0 platform (0x603)
/// </summary>
[CanFrame(0x603, Description = "VCM bootup message (appears once during power on after a few seconds)")]
public class VcmFrame_603_AZE0
{
    // This frame appears to have no decoded signals yet
    // It's a bootup message with unknown structure
}

/// <summary>
///     VCM charge time estimate frame for Nissan Leaf AZE0 platform (0x5B9)
/// </summary>
/// <remarks>
///     Only present on env200 and USDM LEAF models
/// </remarks>
[CanFrame(0x5B9, Description = "Vehicle Control Module charge time estimates (500ms)")]
public partial class VcmFrame_5B9_AZE0
{
    [CanSignal(3, 5,
        Description = "Active fuel bars (0-12)",
        MinValue = 0, MaxValue = 12)]
    public partial int ActiveFuelBars { get; init; }

    [CanSignal(2, 11, Unit = "minutes",
        Description = "Charge minutes remaining estimate",
        MinValue = 0, MaxValue = 2047)]
    public partial int ChargeMinutesRemaining { get; init; }

    [CanSignal(18, 11,
        Description = "Charge time estimate for 100V charging",
        MinValue = 0, MaxValue = 2047)]
    public partial int ChargeTime100V { get; init; }
}
