using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

/// <summary>
/// Body Control Module capability as a view over the shared <see cref="CanMonitor"/>
/// (streaming design P3). Reads BCM broadcast frames — 0x60D (doors, locks, lights) and
/// 0x625 (headlights/foglights), 20ms cadence — from the monitor's latest-frame cache.
/// </summary>
internal sealed class LeafAze0BodyControl : IBodyControl
{
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromMilliseconds(300);

    private readonly CanMonitor _monitor;

    public LeafAze0BodyControl(CanMonitor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    public async ValueTask<BodyControlStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _monitor.StartAsync(ct);
        await _monitor.WaitForCacheAsync(WarmupTimeout, ct, 0x60D, 0x625);

        _monitor.TryGetLatest<BcmFrame_60D_AZE0>(out var frame60D);
        _monitor.TryGetLatest<BcmFrame_625_AZE0>(out var frame625);

        var doorsLocked = frame60D?.DoorLockStatusDriverDoor == true
                          && frame60D?.DoorLockStatusOtherDoors == true;

        // Headlights: high beams from 0x60D, or 0x625 status (0x60=headlights, 0x68=+fog).
        var headlightsOn = frame60D?.HighBeamLights == true
                           || frame60D?.MainBeam == true
                           || (frame625?.HeadlightFoglightStatus & 0x60) == 0x60;

        var hazardLightsOn = frame60D?.LeftTurnSignalFeedback == true
                             && frame60D?.RightTurnSignalFeedback == true;

        return new BodyControlStatus(
            DoorsLocked: doorsLocked,
            HeadlightsOn: headlightsOn,
            HazardLightsOn: hazardLightsOn);
    }
}
