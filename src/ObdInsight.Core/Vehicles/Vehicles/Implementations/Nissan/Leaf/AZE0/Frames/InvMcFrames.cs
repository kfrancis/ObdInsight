using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

/// <summary>
/// Inverter/Motor Controller motor status frame for Nissan Leaf AZE0 platform (0x1DA)
/// </summary>
/// <remarks>
/// Torque/RPM layouts fixed 2026-07-18 against OVMS vehicle_nissanleaf.cpp (case 0x1da) —
/// the previous Intel transcriptions of Motorola DBC start bits read the wrong bytes.
/// Both fields cross byte boundaries, so they're declared as byte-aligned raw signals and
/// recombined in computed properties. No hardware capture exists (EV-CAN, not visible on
/// stock ELM327 adapters); OVMS is the reference. Values 0x7FFE/0x7FFF appear on the RPM
/// field during power-on per OVMS.
/// </remarks>
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

    [CanSignal(16, 3,
        Description = "Effective torque, raw high 3 bits (byte 2 bits 2-0; bit 2 = sign)",
        MinValue = 0, MaxValue = 7)]
    public partial int EffectiveTorqueRawHigh { get; init; }

    [CanSignal(24, 8,
        Description = "Effective torque, raw low 8 bits (byte 3)",
        MinValue = 0, MaxValue = 255)]
    public partial int EffectiveTorqueRawLow { get; init; }

    [CanSignal(50, 6,
        Description = "Error codes blocking inverter operation",
        MinValue = 0, MaxValue = 63)]
    public partial int ErrorCodes { get; init; }

    [CanSignal(0, 8, Factor = 2.0, Unit = "V",
        Description = "Motor generator input voltage (inverter output voltage)",
        MinValue = 0, MaxValue = 508)]
    public partial int InputVoltage { get; init; }

    [CanSignal(32, 8,
        Description = "Output revolution, raw high byte (byte 4; bit 6 = sign, bit 7 undocumented)",
        MinValue = 0, MaxValue = 255)]
    public partial int OutputRevolutionRawHigh { get; init; }

    [CanSignal(40, 8,
        Description = "Output revolution, raw low byte (byte 5)",
        MinValue = 0, MaxValue = 255)]
    public partial int OutputRevolutionRawLow { get; init; }

    /// <summary>
    /// Motor generator effective torque in Nm (negative = regen). 11-bit two's complement
    /// (byte2[2..0] + byte3), 0.5 Nm/bit, per OVMS.
    /// </summary>
    public double EffectiveTorque
    {
        get
        {
            var raw = (EffectiveTorqueRawHigh << 8) | EffectiveTorqueRawLow;
            if ((raw & 0x400) != 0) raw -= 0x800;
            return raw * 0.5;
        }
    }

    /// <summary>
    /// Motor generator output revolution in rpm (negative = reverse). 15-bit two's
    /// complement (byte4[6..0] + byte5) divided by 2, per OVMS (byte 4 bit 7 is
    /// undocumented and excluded).
    /// </summary>
    public int OutputRevolution
    {
        get
        {
            var raw = ((OutputRevolutionRawHigh & 0x7F) << 8) | OutputRevolutionRawLow;
            if ((raw & 0x4000) != 0) raw -= 0x8000;
            return raw / 2;
        }
    }
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
