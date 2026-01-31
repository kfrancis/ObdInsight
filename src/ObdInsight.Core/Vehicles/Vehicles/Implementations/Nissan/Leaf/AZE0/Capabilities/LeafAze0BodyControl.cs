using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

/// <summary>
/// Body Control Module (BCM) capability implementation for Nissan Leaf AZE0 platform.
/// Monitors BCM broadcast frames to gather door lock status, lights, and hazard status.
/// </summary>
internal sealed class LeafAze0BodyControl : IBodyControl
{
    private readonly EcuContext _context;
    private readonly IElmSession _session;

    public LeafAze0BodyControl(IElmSession session, EcuContext context)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (_context.CommunicationMode != EcuCommunicationMode.PassiveMonitoring)
            throw new ArgumentException("BCM status requires PassiveMonitoring context for broadcast frames.", nameof(context));
    }

    /// <summary>
    /// Reads BCM status by monitoring Leaf AZE0 BCM broadcast frames:
    /// - 0x60D: Main BCM status including door locks, door open states, and light status
    /// - 0x625: Headlight and foglight status
    /// These frames transmit every 20ms.
    /// </summary>
    public async ValueTask<BodyControlStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromMilliseconds(300); // 20ms frame rate, collect for 300ms
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        await _session.EnterMonitoringModeAsync(_context, ct);

        BcmFrame_60D_AZE0? frame60D = null;
        BcmFrame_625_AZE0? frame625 = null;

        try
        {
            await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
            {
                if (frame.Data.Length < 6)
                    continue;

                // Use generated router for type-safe frame parsing
                if (CanFrameRouter.TryParseBcmFrame_60D_AZE0(frame.CanId, frame.Data.Span, out var parsed60D))
                {
                    frame60D = parsed60D;
                }
                else if (CanFrameRouter.TryParseBcmFrame_625_AZE0(frame.CanId, frame.Data.Span, out var parsed625))
                {
                    frame625 = parsed625;
                }

                // If we have collected both frames, we can exit early
                if (frame60D != null && frame625 != null)
                    break;
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timeout - return whatever data we collected
        }
        finally
        {
            await _session.ExitMonitoringModeAsync(ct);
        }

        // Build the status from collected frames
        // DoorsLocked: Consider doors locked if both driver door and other doors are locked
        var doorsLocked = frame60D?.DoorLockStatusDriverDoor == true
                          && frame60D?.DoorLockStatusOtherDoors == true;

        // HeadlightsOn: Check multiple sources
        // - frame60D.HighBeamLights or MainBeam for high beams
        // - frame625.HeadlightFoglightStatus: 0x60=headlights, 0x68=headlights+fog
        var headlightsOn = frame60D?.HighBeamLights == true
                          || frame60D?.MainBeam == true
                          || (frame625?.HeadlightFoglightStatus & 0x60) == 0x60;

        // HazardLightsOn: Both left and right turn signals are on simultaneously
        var hazardLightsOn = frame60D?.LeftTurnSignalFeedback == true
                            && frame60D?.RightTurnSignalFeedback == true;

        return new BodyControlStatus(
            DoorsLocked: doorsLocked,
            HeadlightsOn: headlightsOn,
            HazardLightsOn: hazardLightsOn
        );
    }
}
