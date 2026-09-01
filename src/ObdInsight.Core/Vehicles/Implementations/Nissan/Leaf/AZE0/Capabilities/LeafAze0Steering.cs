using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

/// <summary>
///     Steering capability implementation for Nissan Leaf AZE0 platform.
///     Monitors steering angle sensor and steering wheel force broadcast frames.
/// </summary>
internal sealed class LeafAze0Steering : ISteering
{
    private readonly EcuContext _context;
    private readonly IElmSession _session;

    public LeafAze0Steering(IElmSession session, EcuContext context)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (_context.CommunicationMode != EcuCommunicationMode.PassiveMonitoring &&
            _context.CommunicationMode != EcuCommunicationMode.ActiveMonitoring &&
            _context.CommunicationMode != EcuCommunicationMode.FilteredMonitoring)
            throw new ArgumentException("Steering status requires monitoring mode context for broadcast frames.",
                nameof(context));
    }

    /// <summary>
    ///     Reads steering status by monitoring Leaf AZE0 Steering broadcast frames:
    ///     - 0x002: Steering angle sensor (10ms) - provides steering angle in decidegrees
    ///     - 0x300: Steering wheel force (20ms) - provides force/torque applied to wheel
    /// </summary>
    public async ValueTask<SteeringStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromMilliseconds(300); // 10ms frame rate for 0x002, collect for 300ms
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // If context requires session activation, do it first
        if (_context.RequiresSessionActivation)
        {
            var sessionActivated = await _session.ActivateSessionAsync(_context, ct);
            if (!sessionActivated)
            {
                // Session activation failed - continue anyway as data may still be available
            }
        }

        await _session.EnterMonitoringModeAsync(_context, ct);

        SteeringFrame_002_AZE0? frame002 = null;
        SteeringFrame_300_AZE0? frame300 = null;

        try
        {
            await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
            {
                // Use generated router for type-safe frame parsing
                if (frame.Data.Length >= 5 &&
                    CanFrameRouter.TryParseSteeringFrame_002_AZE0(frame.CanId, frame.Data.Span, out var parsed002))
                {
                    frame002 = parsed002;
                }
                else if (frame.Data.Length >= 1 &&
                         CanFrameRouter.TryParseSteeringFrame_300_AZE0(frame.CanId, frame.Data.Span, out var parsed300))
                {
                    frame300 = parsed300;
                }

                // If we have collected both frames, we can exit early
                if (frame002 != null && frame300 != null)
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
        // SteeringAngle is already in degrees (factor 0.1 applied in frame parsing)
        var angleDegrees = frame002?.SteeringAngle ?? 0.0;

        // Normalize angle to -180 to +180 range
        // The raw value goes from 0-6553.5 degrees (0-65535 decidegrees)
        // Left turns are typically represented as values > 3276.75 (> 32767 decidegrees)
        if (angleDegrees > 3276.75)
        {
            angleDegrees -= 6553.5; // Convert to negative for left turns
        }

        // SteeringWheelForce is a raw value (0-255)
        // Without calibration data, we can estimate torque in Nm
        // Typical steering torque range is about 0-10 Nm for normal driving
        // Using a simple linear mapping: force * 0.04 ≈ Nm (255 * 0.04 = 10.2 Nm max)
        var torqueNm = (frame300?.SteeringWheelForce ?? 0) * 0.04;

        return new SteeringStatus(
            angleDegrees,
            torqueNm
        );
    }
}
