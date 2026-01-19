using ObdInsight.SourceGeneration.Attributes;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

/// <summary>
/// Inverter/Motor Controller motor status frame for Nissan Leaf AZE0 platform (0x1DA)
/// </summary>
[CanFrame(0x1DA, Description = "Inverter motor voltage, torque, and RPM (10ms)")]
public partial class InvMcFrame_1DA_AZE0
{
    [CanSignal(48, 2,
        Description = "Message PRUN counter (detection of frozen data)",
        MinValue = 0, MaxValue = 3)]
    public partial int Clock { get; init; }

    [CanSignal(56, 8,
        Description = "CRC checksum",
        MinValue = 0, MaxValue = 255)]
    public partial int Crc { get; init; }

    [CanSignal(18, 11, IsSigned = true, Factor = 0.5, Unit = "Nm",
        Description = "Motor generator effective torque (response from inverter)",
        MinValue = -274, MaxValue = 274)]
    public partial double EffectiveTorque { get; init; }

    [CanSignal(50, 6,
        Description = "Error codes blocking inverter operation",
        MinValue = 0, MaxValue = 63)]
    public partial int ErrorCodes { get; init; }

    [CanSignal(0, 8, Factor = 2.0, Unit = "V",
                        Description = "Motor generator input voltage (inverter output voltage)",
        MinValue = 0, MaxValue = 508)]
    public partial int InputVoltage { get; init; }

    [CanSignal(39, 15, IsSigned = true, Unit = "rpm",
        Description = "Motor generator output revolution (negative for reverse)",
        MinValue = -16382, MaxValue = 16382)]
    public partial int OutputRevolution { get; init; }
}

/// <summary>
/// Inverter/Motor Controller temperature frame for Nissan Leaf AZE0 platform (0x55A)
/// </summary>
[CanFrame(0x55A, Description = "Inverter and motor temperature sensors (100ms)")]
public partial class InvMcFrame_55A_AZE0
{
    /// <summary>
    /// IGBT driver board temperature in °C
    /// </summary>
    public double IgbtDriverBoardTempC => IgbtDriverBoardTempRaw / 2.0;

    [CanSignal(29, 6, Unit = "°C",
        Description = "IGBT driver board temperature (active during drive, divide by 2 for °C)",
        MinValue = 0, MaxValue = 63)]
    public partial int IgbtDriverBoardTempRaw { get; init; }

    /// <summary>
    /// IGBT temperature in °C
    /// </summary>
    public double IgbtTemperatureC => IgbtTemperatureRaw / 2.0;

    [CanSignal(16, 8, Unit = "°C",
        Description = "IGBT (power transistor) temperature (divide by 2 for °C)",
        MinValue = 0, MaxValue = 255)]
    public partial int IgbtTemperatureRaw { get; init; }

    /// <summary>
    /// Inverter communications board temperature in °C
    /// </summary>
    public double InverterComBoardTempC => InverterComBoardTempRaw / 2.0;

    [CanSignal(8, 8, Unit = "°C",
                            Description = "Inverter communications board temperature (divide by 2 for °C)",
        MinValue = 0, MaxValue = 255)]
    public partial int InverterComBoardTempRaw { get; init; }

    /// <summary>
    /// Motor temperature in °C
    /// </summary>
    public double MotorTemperatureC => MotorTemperatureRaw / 2.0;

    [CanSignal(32, 8, Unit = "°C",
            Description = "Motor temperature (divide by 2 for °C)",
        MinValue = 0, MaxValue = 255)]
    public partial int MotorTemperatureRaw { get; init; }

    [CanSignal(60, 2,
        Description = "Inverter sleep enabled status",
        MinValue = 0, MaxValue = 3)]
    public partial int SleepEnabled { get; init; }
}
