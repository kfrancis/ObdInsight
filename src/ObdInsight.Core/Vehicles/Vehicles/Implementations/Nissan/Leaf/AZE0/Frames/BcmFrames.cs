using ObdInsight.SourceGeneration;
using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

/// <summary>
/// BCM (Body Control Module) main status frame for Nissan Leaf AZE0 platform (0x60D)
/// </summary>
[CanFrame(0x60D, Description = "Body Control Module status including doors, locks, and lights (20ms)")]
public partial class BcmFrame_60D_AZE0
{
    [CanSignal(0, 1,
        Description = "Unknown bit 0",
        MinValue = 0, MaxValue = 1)]
    public partial bool UnknownBit0 { get; init; }

    [CanSignal(1, 2,
        Description = "Parking lights status (2=parking lights, 3=dim lights)",
        MinValue = 0, MaxValue = 3)]
    public partial int ParkingLights { get; init; }

    [CanSignal(3, 1,
        Description = "Passenger door open (1=open)",
        MinValue = 0, MaxValue = 1)]
    public partial bool PassengerDoorOpen { get; init; }

    [CanSignal(4, 1,
        Description = "Driver door open (1=open)",
        MinValue = 0, MaxValue = 1)]
    public partial bool DriverDoorOpen { get; init; }

    [CanSignal(5, 1,
        Description = "Rear left door open (1=open)",
        MinValue = 0, MaxValue = 1)]
    public partial bool RearLeftDoorOpen { get; init; }

    [CanSignal(6, 1,
        Description = "Rear right door open (1=open)",
        MinValue = 0, MaxValue = 1)]
    public partial bool RearRightDoorOpen { get; init; }

    [CanSignal(7, 1,
        Description = "Trunk/hatch open (1=open)",
        MinValue = 0, MaxValue = 1)]
    public partial bool TrunkOpen { get; init; }

    [CanSignal(8, 1,
        Description = "Fog lights on (1=on)",
        MinValue = 0, MaxValue = 1)]
    public partial bool FogLights { get; init; }

    [CanSignal(9, 2,
        Description = "Vehicle state (0=OFF, 1=ACC pressed once, 2=booting, 3=ON/Ready)",
        MinValue = 0, MaxValue = 3)]
    public partial int VehicleState { get; init; }

    [CanSignal(11, 1,
        Description = "Main beam/high beam lights (1=on)",
        MinValue = 0, MaxValue = 1)]
    public partial bool MainBeam { get; init; }

    [CanSignal(12, 1,
        Description = "Unknown bit 12",
        MinValue = 0, MaxValue = 1)]
    public partial bool UnknownBit12 { get; init; }

    [CanSignal(13, 1,
        Description = "Left turn signal feedback (1=on)",
        MinValue = 0, MaxValue = 1)]
    public partial bool LeftTurnSignalFeedback { get; init; }

    [CanSignal(14, 1,
        Description = "Right turn signal feedback (1=on)",
        MinValue = 0, MaxValue = 1)]
    public partial bool RightTurnSignalFeedback { get; init; }

    [CanSignal(15, 1,
        Description = "High beam lights on (1=on)",
        MinValue = 0, MaxValue = 1)]
    public partial bool HighBeamLights { get; init; }

    [CanSignal(19, 1,
        Description = "Door lock status - other doors (1=locked)",
        MinValue = 0, MaxValue = 1)]
    public partial bool DoorLockStatusOtherDoors { get; init; }

    [CanSignal(20, 1,
        Description = "Door lock status - driver door (1=locked)",
        MinValue = 0, MaxValue = 1)]
    public partial bool DoorLockStatusDriverDoor { get; init; }

    [CanSignal(21, 1,
        Description = "Right turn signal command (1=on)",
        MinValue = 0, MaxValue = 1)]
    public partial bool RightTurnSignalCommand { get; init; }

    [CanSignal(22, 1,
        Description = "Left turn signal command (1=on)",
        MinValue = 0, MaxValue = 1)]
    public partial bool LeftTurnSignalCommand { get; init; }

    [CanSignal(24, 8,
        Description = "Unknown byte 3",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown3 { get; init; }

    [CanSignal(32, 8,
        Description = "Unknown byte 4",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown4 { get; init; }

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

/// <summary>
/// BCM headlight and foglight status frame for Nissan Leaf AZE0 platform (0x625)
/// </summary>
[CanFrame(0x625, Description = "Body Control Module headlight and foglight status (20ms)")]
public partial class BcmFrame_625_AZE0
{
    [CanSignal(0, 8,
        Description = "Unknown byte 0",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown0 { get; init; }

    [CanSignal(8, 8,
        Description = "Headlight/foglight status (0x00=off, 0x40=parking, 0x60=headlights, 0x68=headlights+fog)",
        MinValue = 0, MaxValue = 255)]
    public partial int HeadlightFoglightStatus { get; init; }

    [CanSignal(16, 8,
        Description = "Unknown byte 2",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown2 { get; init; }

    [CanSignal(24, 8,
        Description = "Unknown byte 3",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown3 { get; init; }

    [CanSignal(32, 8,
        Description = "Unknown byte 4",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown4 { get; init; }

    [CanSignal(40, 8,
        Description = "Unknown byte 5",
        MinValue = 0, MaxValue = 255)]
    public partial int Unknown5 { get; init; }
}



