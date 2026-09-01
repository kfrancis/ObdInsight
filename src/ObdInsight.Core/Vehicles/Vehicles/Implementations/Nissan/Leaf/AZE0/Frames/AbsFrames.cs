using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

/// <summary>
/// ABS status frame for Nissan Leaf AZE0 platform (0x130)
/// </summary>
[CanFrame(0x130, Description = "ABS status and bitmask (20ms)")]
public partial class AbsFrame_130_AZE0
{
    [CanSignal(0, 8,
        Description = "Unknown ABS byte 0",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown0 { get; init; }

    [CanSignal(8, 8,
        Description = "ABS bitmask status (Bit5=1 indicates traction control off)",
        MinValue = 0, MaxValue = 255)]
    public partial int BitmaskAbs { get; init; }

    //[CanSignal(16, 8,
    //    Description = "Unknown ABS byte 2",
    //    MinValue = 0, MaxValue = 255)]
    //public partial int Unknown2 { get; init; }
}

/// <summary>
/// ABS torque control frame for Nissan Leaf AZE0 platform (0x245)
/// </summary>
/// <remarks>
/// The torque fields are Motorola-order 12-bit values that straddle byte boundaries
/// (DBC start bits 7/11/55), so they are declared as byte-aligned raw signals and
/// recombined in computed properties (generator supports Intel bit order only).
/// Raw values are center-offset: 0x800 (2048) = 0 Nm. Hardware capture 2026-07-18
/// (parked, raw 7FE8021835007FE1): raw 0x7FE/0x802 → −1.0/+1.0 Nm ≈ neutral.
/// The 0.5 Nm/bit factor is an estimate consistent with the neutral capture; only the
/// near-zero point is hardware-verified.
/// </remarks>
[CanFrame(0x245, Description = "ABS VDC torque down request and motor torque (20ms)")]
public partial class AbsFrame_245_AZE0
{
    [CanSignal(0, 8,
        Description = "VDC torque down request 1, raw high 8 bits (byte 0)",
        MinValue = 0, MaxValue = 255)]
    public partial int VdcTorqueDownRequest1RawHigh { get; init; }

    [CanSignal(12, 4,
        Description = "VDC torque down request 1, raw low 4 bits (byte 1 bits 7-4)",
        MinValue = 0, MaxValue = 15)]
    public partial int VdcTorqueDownRequest1RawLow { get; init; }

    [CanSignal(8, 4,
        Description = "Motor torque request from ABS, raw high 4 bits (byte 1 bits 3-0)",
        MinValue = 0, MaxValue = 15)]
    public partial int MotorTorqueRequestAbsRawHigh { get; init; }

    [CanSignal(16, 8,
        Description = "Motor torque request from ABS, raw low 8 bits (byte 2)",
        MinValue = 0, MaxValue = 255)]
    public partial int MotorTorqueRequestAbsRawLow { get; init; }

    [CanSignal(29, 3,
        Description = "Torque down request type (byte 3 bits 7-5; Motorola DBC start-bit 31)",
        MinValue = 0, MaxValue = 7)]
    public partial int TorqueDownRequestType { get; init; }

    [CanSignal(32, 8,
        Description = "Message counter (byte 4, increments every frame)",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown4 { get; init; }

    [CanSignal(40, 8,
        Description = "Unknown byte 5",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown5 { get; init; }

    [CanSignal(48, 8,
        Description = "VDC torque down request 2, raw high 8 bits (byte 6)",
        MinValue = 0, MaxValue = 255)]
    public partial int VdcTorqueDownRequest2RawHigh { get; init; }

    [CanSignal(60, 4,
        Description = "VDC torque down request 2, raw low 4 bits (byte 7 bits 7-4)",
        MinValue = 0, MaxValue = 15)]
    public partial int VdcTorqueDownRequest2RawLow { get; init; }

    [CanSignal(56, 4,
        Description = "Message counter (byte 7 bits 3-0, increments every frame)",
        MinValue = 0, MaxValue = 15)]
    public partial int Unknown7 { get; init; }

    /// <summary>VDC torque down request 1 in Nm (byte0[7..0]+byte1[7..4], 0x800 = 0 Nm,
    /// est. 0.5 Nm/bit). ≈0 when parked.</summary>
    public double VdcTorqueDownRequest1 =>
        (((VdcTorqueDownRequest1RawHigh << 4) | VdcTorqueDownRequest1RawLow) - 2048) * 0.5;

    /// <summary>Motor torque request from ABS in Nm (byte1[3..0]+byte2[7..0], 0x800 = 0 Nm,
    /// est. 0.5 Nm/bit). ≈0 when parked.</summary>
    public double MotorTorqueRequestAbs =>
        (((MotorTorqueRequestAbsRawHigh << 8) | MotorTorqueRequestAbsRawLow) - 2048) * 0.5;

    /// <summary>VDC torque down request 2 in Nm (byte6[7..0]+byte7[7..4], 0x800 = 0 Nm,
    /// est. 0.5 Nm/bit). ≈0 when parked.</summary>
    public double VdcTorqueDownRequest2 =>
        (((VdcTorqueDownRequest2RawHigh << 4) | VdcTorqueDownRequest2RawLow) - 2048) * 0.5;
}

/// <summary>
/// ABS front wheel speed frame for Nissan Leaf AZE0 platform (0x284)
/// </summary>
/// <remarks>
/// Canonical decoder for CAN 0x284 (the duplicate <c>VcmFrame_284_AZE0</c> definition was
/// removed in the 2026-07-18 frame-layout audit). The multi-byte fields are big-endian on
/// the wire (Motorola-order DBC source); the generator only supports Intel bit order, so
/// each field is declared as byte-aligned raw signals recombined in computed properties.
/// Hardware capture 2026-07-18 (parked): bytes 0-5 all zero (speeds 0), bytes 6-7 are
/// free-running per-frame counters (0x34BA → 0x35BB), NOT speed or distance — the old
/// definition decoded them as 61-496 km/h while stationary.
/// </remarks>
[CanFrame(0x284, Description = "ABS front wheel speeds and vehicle speed (20ms)")]
public partial class AbsFrame_284_AZE0
{
    // Three 16-bit big-endian fields, matching CAR-can_AZE0.dbc exactly:
    //   Wheel_Speed_FR       7|16@0+ (0.005,0) km/h
    //   Wheel_Speed_FL      23|16@0+ (0.005,0) km/h
    //   VehicleSpeedFromABS 39|16@0+ (0.01,0)
    // Each was previously declared as two Intel byte halves and shifted together in a computed
    // property, because a Motorola field could not be expressed directly. Same workaround 0x55B
    // Soc used, and the recombination produced identical values - this is a simplification, not
    // a behaviour change.
    [CanSignal(7, 16, ByteOrder = CanByteOrder.Motorola, Factor = 0.005, Unit = "km/h",
        Description = "Front right wheel speed",
        MinValue = 0, MaxValue = 327)]
    public partial double WheelSpeedFr { get; init; }

    [CanSignal(23, 16, ByteOrder = CanByteOrder.Motorola, Factor = 0.005, Unit = "km/h",
        Description = "Front left wheel speed",
        MinValue = 0, MaxValue = 327)]
    public partial double WheelSpeedFl { get; init; }

    [CanSignal(39, 16, ByteOrder = CanByteOrder.Motorola, Factor = 0.01, Unit = "km/h",
        Description = "Vehicle speed from ABS")]
    public partial double VehicleSpeedFromAbs { get; init; }

    [CanSignal(48, 8,
        Description = "Free-running message counter (byte 6, increments every frame)",
        MinValue = 0, MaxValue = 255)]
    public partial int MessageCounter1 { get; init; }

    [CanSignal(56, 8,
        Description = "Free-running message counter (byte 7, increments every frame)",
        MinValue = 0, MaxValue = 255)]
    public partial int MessageCounter2 { get; init; }

    // Factors come from the DBC and remain unverified beyond the parked zero capture: a
    // stationary vehicle reads 0 whatever the scaling. Confirming them needs a drive.
}

/// <summary>
/// ABS rear wheel speed frame for Nissan Leaf AZE0 platform (0x285)
/// </summary>
/// <remarks>
/// Same big-endian layout as <see cref="AbsFrame_284_AZE0"/> (fixed together in the
/// 2026-07-18 audit): wheel speeds are big-endian byte pairs, bytes 6-7 are free-running
/// per-frame counters (capture: …34BA → …35BC while parked).
/// </remarks>
[CanFrame(0x285, Description = "ABS rear wheel speeds (20ms)")]
public partial class AbsFrame_285_AZE0
{
    // Same layout as 0x284, per CAR-can_AZE0.dbc:
    //   Wheel_Speed_RR   7|16@0+ (0.005,0) km/h
    //   Wheel_Speed_RL  23|16@0+ (0.005,0) km/h
    //   NotUsed_285_4_5 39|16@0+ (1,0)
    [CanSignal(7, 16, ByteOrder = CanByteOrder.Motorola, Factor = 0.005, Unit = "km/h",
        Description = "Rear right wheel speed",
        MinValue = 0, MaxValue = 327)]
    public partial double WheelSpeedRr { get; init; }

    [CanSignal(23, 16, ByteOrder = CanByteOrder.Motorola, Factor = 0.005, Unit = "km/h",
        Description = "Rear left wheel speed",
        MinValue = 0, MaxValue = 327)]
    public partial double WheelSpeedRl { get; init; }

    [CanSignal(39, 16, ByteOrder = CanByteOrder.Motorola,
        Description = "Not used (bytes 4-5, reserved)",
        MinValue = 0, MaxValue = 65535)]
    public partial int NotUsed { get; init; }

    [CanSignal(48, 8,
        Description = "Free-running message counter (byte 6, increments every frame)",
        MinValue = 0, MaxValue = 255)]
    public partial int MessageCounter1 { get; init; }

    [CanSignal(56, 8,
        Description = "Free-running message counter (byte 7, increments every frame)",
        MinValue = 0, MaxValue = 255)]
    public partial int MessageCounter2 { get; init; }

    // Factors come from the DBC and remain unverified beyond the parked zero capture.
}

/// <summary>
/// ABS battery voltage and brake pressure frame for Nissan Leaf AZE0 platform (0x292)
/// </summary>
[CanFrame(0x292, Description = "ABS lead-acid battery voltage and friction brake pressure (20ms)")]
public partial class AbsFrame_292_AZE0
{
    [CanSignal(0, 8,
        Description = "Unknown byte 0",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown0 { get; init; }

    [CanSignal(8, 8,
        Description = "Unknown byte 1",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown1 { get; init; }

    [CanSignal(16, 8,
        Description = "Unknown byte 2",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown2 { get; init; }

    [CanSignal(24, 8, Factor = 0.1, Unit = "V",
        Description = "Lead-acid (12V) battery voltage (e.g., 0x7F = 12.7V)",
        MinValue = 0, MaxValue = 25.5)]
    public partial double LeadAcidBatteryVoltage { get; init; }

    [CanSignal(32, 8,
        Description = "Unknown byte 4",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown4 { get; init; }

    [CanSignal(40, 8,
        Description = "Unknown byte 5",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown5 { get; init; }

    [CanSignal(48, 8,
        Description = "Friction brake pressure (55 motor amps equivalent per tick)",
        MinValue = 0, MaxValue = 255)]
    public partial int FrictionBrakePressure { get; init; }

    [CanSignal(56, 8,
        Description = "Unknown byte 7",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown7 { get; init; }
}

/// <summary>
/// ABS vehicle speed and ESP status frame for Nissan Leaf AZE0 platform (0x354)
/// </summary>
[CanFrame(0x354, Description = "ABS vehicle speed pulses and ESP status (20ms)")]
public partial class AbsFrame_354_AZE0
{
    [CanSignal(7, 16, Unit = "pulses",
        Description = "Vehicle speed from ABS in pulses",
        MinValue = 0, MaxValue = 65535)]
    public partial int VehicleSpeedAbs { get; init; }

    [CanSignal(16, 8,
        Description = "Unknown byte 2",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown2 { get; init; }

    [CanSignal(24, 8,
        Description = "Unknown byte 3",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown3 { get; init; }

    [CanSignal(38, 1,
        Description = "ESP/Traction control disabled status (1=disabled)",
        MinValue = 0, MaxValue = 1)]
    public partial bool EspDisabled { get; init; }

    [CanSignal(40, 8,
        Description = "Unknown byte 5",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown5 { get; init; }

    [CanSignal(48, 8,
        Description = "Unknown byte 6",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown6 { get; init; }

    [CanSignal(56, 8,
        Description = "Unknown byte 7",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown7 { get; init; }
}



