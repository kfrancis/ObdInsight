using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

/// <summary>
///     On-Board Charger power distribution status frame for Nissan Leaf AZE0 platform (0x390)
/// </summary>
[CanFrame(0x390, Description = "On-Board Charger status and power output (100ms)")]
public partial class ObcPdFrame_390_AZE0
{
    [CanSignal(0, 9, Factor = 0.1, Unit = "kW",
        Description = "Actual charger power output (from OVMS code)",
        MinValue = 0, MaxValue = 50)]
    public partial double ChargePower { get; init; }

    [CanSignal(46, 6,
        Description = "Charge status (1=Idle/QC, 2=Finished, 4=Charging/interrupted, 8/9=Idle, 12=Waiting on timer)",
        MinValue = 0, MaxValue = 63)]
    public partial int ChargeStatus { get; init; }

    [CanSignal(56, 4,
        Description = "Checksum (sum of all nibbles - 4)",
        MinValue = 0, MaxValue = 15)]
    public partial int Csum { get; init; }

    [CanSignal(26, 2,
        Description = "DC-DC converter status",
        MinValue = 0, MaxValue = 3)]
    public partial int DcdcConvStatus { get; init; }

    [CanSignal(47, 1,
        Description = "Quick charge IR sensor flag (0=Without, 1=With)",
        MinValue = 0, MaxValue = 1)]
    public partial bool FlagQcIrSensor { get; init; }

    [CanSignal(38, 1,
        Description = "Quick charge relay announcement flag (1=Announce OFF, 2=Announce ON)",
        MinValue = 0, MaxValue = 1)]
    public partial bool FlagQcRelayOnAnnouncement { get; init; }

    [CanSignal(40, 9, Factor = 0.1, Unit = "kW",
        Description = "Maximum charge power output from charger",
        MinValue = 0, MaxValue = 50)]
    public partial double MaximumChargePowerOut { get; init; }

    [CanSignal(60, 2,
        Description = "PRUN counter (detection of frozen data)",
        MinValue = 0, MaxValue = 3)]
    public partial int Prun { get; init; }

    [CanSignal(3, 2,
        Description = "OBC sleep enabled status",
        MinValue = 0, MaxValue = 3)]
    public partial int SleepEnabled { get; init; }

    [CanSignal(27, 2,
        Description = "AC voltage status (0=No Signal, 1=100V, 2=200V, 3=Abnormal Wave)",
        MinValue = 0, MaxValue = 3)]
    public partial int StatusAcVoltage { get; init; }
}

/// <summary>
///     On-Board Charger power distribution secondary frame for Nissan Leaf AZE0 platform (0x393)
/// </summary>
/// <remarks>
///     Unknown/undecoded signals in this frame:
///     - Byte 1 (bits 8-15): Unknown data (0x20 while idle, 0x53 while slow charging)
///     - Byte 4 (bits 32-39): Unknown data (always 0x20 in logs)
/// </remarks>
[CanFrame(0x393, Description = "On-Board Charger secondary status (100ms)")]
public partial class ObcPdFrame_393_AZE0
{
    [CanSignal(56, 4,
        Description = "Checksum (deviates from standard: all nibbles summed together - 1)",
        MinValue = 0, MaxValue = 15)]
    public partial int Csum { get; init; }

    [CanSignal(60, 2,
        Description = "PRUN counter (detection of frozen data)",
        MinValue = 0, MaxValue = 3)]
    public partial int Prun { get; init; }
}
