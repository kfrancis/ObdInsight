using ObdInsight.SourceGeneration.Attributes;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

/// <summary>
/// Steering angle sensor frame for Nissan Leaf AZE0 platform (0x002)
/// </summary>
[CanFrame(0x002, Description = "Steering angle sensor signal with angle and rate of change (10ms)")]
public partial class SteeringFrame_002_AZE0
{
    [CanSignal(7, 16, Factor = 0.1, Unit = "degrees",
        Description = "Steering angle in decidegrees (left is negative, divide by 10 for degrees, 3600 = 360.0°)",
        MinValue = 0, MaxValue = 6553.5)]
    public partial double SteeringAngle { get; init; }

    [CanSignal(23, 8,
        Description = "Steering angle change rate",
        MinValue = 0, MaxValue = 255)]
    public partial int SteeringAngleChangeRate { get; init; }

    [CanSignal(39, 8,
        Description = "Steering sensor heartbeat/CRC (very active 0x00-0xFF)",
        MinValue = 0, MaxValue = 255)]
    public partial int SteeringSensorHeartbeat { get; init; }

    [CanSignal(31, 8,
            Description = "Unknown byte 3 (typically 0x07)",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown3 { get; init; }
}

/// <summary>
/// Steering wheel force frame for Nissan Leaf AZE0 platform (0x300)
/// </summary>
[CanFrame(0x300, Description = "Steering wheel force applied (20ms)")]
public partial class SteeringFrame_300_AZE0
{
    [CanSignal(0, 8,
        Description = "Steering wheel force/torque applied (raw value 0-255)",
        MinValue = 0, MaxValue = 255)]
    public partial int SteeringWheelForce { get; init; }
}
