using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

/// <summary>
/// Brake control module frame for Nissan Leaf AZE0 platform (0x1CA)
/// </summary>
[CanFrame(0x1CA, Description = "Brake control module pressure and regen braking status (20ms)")]
public partial class BrakeFrame_1CA_AZE0
{
    [CanSignal(0, 8,
        Description = "Brake pressure sensor 1",
        MinValue = 0, MaxValue = 255)]
    public partial int BrakePressure1 { get; init; }

    [CanSignal(8, 8,
        Description = "Brake pressure sensor 2",
        MinValue = 0, MaxValue = 255)]
    public partial int BrakePressure2 { get; init; }

    [CanSignal(16, 8,
        Description = "Brake pressure sensor 3",
        MinValue = 0, MaxValue = 255)]
    public partial int BrakePressure3 { get; init; }

    [CanSignal(24, 8,
        Description = "Brake pressure sensor 4",
        MinValue = 0, MaxValue = 255)]
    public partial int BrakePressure4 { get; init; }

    [CanSignal(42, 6,
        Description = "Regenerative braking level (0-63)",
        MinValue = 0, MaxValue = 63)]
    public partial int RegenBraking { get; init; }

    [CanSignal(32, 8,
            Description = "Unknown byte 4",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown4 { get; init; }

    [CanSignal(40, 2,
        Description = "Unknown bits (byte 5, bits 0-1)",
        MinValue = 0, MaxValue = 3)]
    public partial int Unknown5Low { get; init; }

    [CanSignal(48, 8,
        Description = "Unknown byte 6",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown6 { get; init; }

    [CanSignal(56, 8,
        Description = "Unknown byte 7",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown7 { get; init; }
}
