using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.AZE0;

/// <summary>
/// HVAC setpoint and ambient temperature frame for Nissan Leaf AZE0 platform (0x54A)
/// </summary>
/// <remarks>
/// Unknown/undecoded signals in this frame:
/// - Byte 0 (bits 0-7): Climate control status plus unknown (12,3c=CC Off; a0,da=CC On)
/// - Byte 1 (bits 8-15): Unknown data field (00 for older models, 80 in 2013+)
/// - Byte 2 (bits 16-23): Unknown data field (typically 70)
/// - Byte 3 (bits 24-31): Unknown data field (values: 06,0a,0b,0f observed)
/// - Byte 5 (bits 40-47): Unknown data field (typically 00)
/// - Byte 6 (bits 48-55): Unknown data field (typically 00)
/// </remarks>
[CanFrame(0x54A, Description = "HVAC climate control setpoint and ambient temperature")]
public partial class HvacFrame_54A_AZE0
{
    [CanSignal(56, 8, Unit = "°C",
        Description = "Ambient temperature from A/C system (appears to track ambient +41, values: 4f,8c-90)",
        MinValue = 0, MaxValue = 255)]
    public partial int AmbientTempAc { get; init; }

    [CanSignal(32, 8, Unit = "°C",
            Description = "Climate control temperature setpoint (only valid while CC is active, 0x00 when off)",
        MinValue = 0, MaxValue = 255)]
    public partial int ClimateControlSetpoint { get; init; }
}

/// <summary>
/// HVAC fan control frame for Nissan Leaf AZE0 platform (0x54B)
/// </summary>
[CanFrame(0x54B, Description = "HVAC fan speed and ventilation mode")]
public partial class HvacFrame_54B_AZE0
{
    [CanSignal(56, 8,
        Description = "Climate control button press indicator (alternates after every CC button press)",
        MinValue = 0, MaxValue = 255)]
    public partial int CcButtonPress { get; init; }

    [CanSignal(0, 8,
            Description = "Climate control status (00 CC on, 01 CC off, 2013: 0x10 or 0x11)",
        MinValue = 0, MaxValue = 255)]
    public partial int ClimateControlStatus { get; init; }

    [CanSignal(24, 8,
        Description = "Climate vent mode intake (recirculating/fresh air/defrost)",
        MinValue = 0, MaxValue = 255)]
    public partial int ClimateVentModeIntake { get; init; }

    [CanSignal(16, 8,
            Description = "Climate vent mode target (face/feet/defrost)",
        MinValue = 0, MaxValue = 255)]
    public partial int ClimateVentModeTarget { get; init; }

    // Minimum is 0, not 1: 2460 captured frames include 0 with the fan off, which the previous
    // range declared impossible. 1-7 describes the manual speed settings, not the field.
    [CanSignal(35, 5,
        Description = "Fan speed level (0 = off, 1-7 for manual speed)",
        MinValue = 0, MaxValue = 7)]
    public partial int FanSpeed { get; init; }
}

/// <summary>
/// HVAC status frame for Nissan Leaf AZE0 platform (0x54C)
/// </summary>
[CanFrame(0x54C, Description = "HVAC environmental conditions and A/C status")]
public partial class HvacFrame_54C_AZE0
{
    [CanSignal(11, 1,
        Description = "A/C compressor active")]
    public partial bool AcOn { get; init; }

    [CanSignal(10, 1,
        Description = "Climate control system enabled")]
    public partial bool ClimateControlOn { get; init; }

    [CanSignal(0, 8, Factor = 0.25, Unit = "°C",
                Description = "A/C evaporator temperature")]
    public partial double EvaporatorTemp { get; init; }

    [CanSignal(40, 8, Factor = 0.05, Unit = "V",
        Description = "HVAC fan motor voltage",
        MinValue = 0, MaxValue = 12.75)]
    public partial double FanVoltage { get; init; }

    [CanSignal(48, 8, Factor = 0.5, Offset = -40.0, Unit = "°C",
        Description = "Outside ambient air temperature",
        MinValue = -40, MaxValue = 87.5)]
    public partial double OutsideAmbientTemp { get; init; }

    [CanSignal(9, 1,
                Description = "Rear window defrost active")]
    public partial bool RearDefrostOn { get; init; }
}

/// <summary>
/// HVAC power consumption frame for Nissan Leaf AZE0 platform (0x54F)
/// </summary>
[CanFrame(0x54F, Description = "HVAC power consumption and interior temperature")]
public partial class HvacFrame_54F_AZE0
{
    [CanSignal(46, 2,
        Description = "A/C automatic control mode (0=off, 1-3=various auto modes)")]
    public partial int AcAutoMode { get; init; }

    [CanSignal(8, 8, Factor = 50.0, Unit = "W",
        Description = "A/C compressor power consumption",
        MinValue = 0, MaxValue = 12750)]
    public partial int AcPowerWatts { get; init; }

    [CanSignal(40, 6, Factor = 300.0, Unit = "W",
        Description = "PTC heater power consumption",
        MinValue = 0, MaxValue = 18900)]
    public partial int HeaterPowerWatts { get; init; }

    [CanSignal(0, 8, Factor = 0.5, Offset = -14.0, Unit = "°C",
                    Description = "Interior air intake temperature",
        MinValue = -14, MaxValue = 113)]
    public partial double InteriorIntakeTemp { get; init; }
}
