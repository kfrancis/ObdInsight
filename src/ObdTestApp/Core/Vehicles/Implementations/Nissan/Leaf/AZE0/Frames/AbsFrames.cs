using ObdInsight.SourceGeneration.Attributes;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

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
[CanFrame(0x245, Description = "ABS VDC torque down request and motor torque (20ms)")]
public partial class AbsFrame_245_AZE0
{
    [CanSignal(7, 12, Factor = 2.5, Unit = "Nm",
        Description = "VDC torque down request 1",
        MinValue = 0, MaxValue = 10230)]
    public partial double VdcTorqueDownRequest1 { get; init; }

    [CanSignal(11, 12, Factor = 2.5, Unit = "Nm",
        Description = "Motor torque request from ABS",
        MinValue = 0, MaxValue = 10230)]
    public partial double MotorTorqueRequestAbs { get; init; }

    [CanSignal(31, 3,
        Description = "Torque down request type",
        MinValue = 0, MaxValue = 7)]
    public partial int TorqueDownRequestType { get; init; }

    [CanSignal(32, 8,
        Description = "Unknown byte 4 (counter)",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown4 { get; init; }

    [CanSignal(40, 8,
        Description = "Unknown byte 5",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown5 { get; init; }

    [CanSignal(55, 12, Factor = 2.5, Unit = "Nm",
        Description = "VDC torque down request 2",
        MinValue = 0, MaxValue = 10230)]
    public partial double VdcTorqueDownRequest2 { get; init; }

    [CanSignal(56, 4,
        Description = "Unknown nibble (byte 7, bits 0-3)",
        MinValue = 0, MaxValue = 15)]
    public partial int Unknown7 { get; init; }
}

/// <summary>
/// ABS front wheel speed frame for Nissan Leaf AZE0 platform (0x284)
/// </summary>
[CanFrame(0x284, Description = "ABS front wheel speeds and vehicle speed (20ms)")]
public partial class AbsFrame_284_AZE0
{
    [CanSignal(7, 16, Factor = 0.005, Unit = "km/h",
        Description = "Front right wheel speed",
        MinValue = 0, MaxValue = 327)]
    public partial double WheelSpeedFr { get; init; }

    [CanSignal(23, 16, Factor = 0.005, Unit = "km/h",
        Description = "Front left wheel speed",
        MinValue = 0, MaxValue = 327)]
    public partial double WheelSpeedFl { get; init; }

    [CanSignal(39, 16, Factor = 0.01, Unit = "km/h",
        Description = "Vehicle speed from ABS",
        MinValue = 0, MaxValue = 655.35)]
    public partial double VehicleSpeedFromAbs { get; init; }

    [CanSignal(48, 8,
        Description = "Distance traveled counter 1",
        MinValue = 0, MaxValue = 255)]
    public partial int DistanceTraveled1 { get; init; }

    [CanSignal(56, 8,
        Description = "Distance traveled counter 2",
        MinValue = 0, MaxValue = 255)]
    public partial int DistanceTraveled2 { get; init; }
}

/// <summary>
/// ABS rear wheel speed frame for Nissan Leaf AZE0 platform (0x285)
/// </summary>
[CanFrame(0x285, Description = "ABS rear wheel speeds (20ms)")]
public partial class AbsFrame_285_AZE0
{
    [CanSignal(7, 16, Factor = 0.005, Unit = "km/h",
        Description = "Rear right wheel speed",
        MinValue = 0, MaxValue = 327)]
    public partial double WheelSpeedRr { get; init; }

    [CanSignal(23, 16, Factor = 0.005, Unit = "km/h",
        Description = "Rear left wheel speed",
        MinValue = 0, MaxValue = 327)]
    public partial double WheelSpeedRl { get; init; }

    [CanSignal(39, 16,
        Description = "Not used (reserved)",
        MinValue = 0, MaxValue = 65535)]
    public partial int NotUsed { get; init; }

    [CanSignal(48, 8,
        Description = "Unknown byte 6",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown6 { get; init; }

    [CanSignal(56, 8,
        Description = "Unknown byte 7",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown7 { get; init; }
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



