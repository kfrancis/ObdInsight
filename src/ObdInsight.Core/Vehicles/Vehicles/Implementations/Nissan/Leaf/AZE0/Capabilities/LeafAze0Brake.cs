using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

/// <summary>
/// Brake system capability implementation for Nissan Leaf AZE0 platform.
/// Monitors brake control module broadcast frame to gather brake pressure and regen status.
/// </summary>
internal sealed class LeafAze0Brake : IBrake
{
    private readonly EcuContext _context;
    private readonly IElmSession _session;

    public LeafAze0Brake(IElmSession session, EcuContext context)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (_context.CommunicationMode != EcuCommunicationMode.PassiveMonitoring)
            throw new ArgumentException("Brake status requires PassiveMonitoring context for broadcast frames.", nameof(context));
    }

    /// <summary>
    /// Reads brake status by monitoring Leaf AZE0 Brake broadcast frame 0x1CA.
    /// Frame 0x1CA contains brake pressure sensors and regenerative braking level.
    /// This frame transmits every 20ms.
    /// </summary>
    public async ValueTask<BrakeStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromMilliseconds(300); // 20ms frame rate, collect for 300ms
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        await _session.EnterMonitoringModeAsync(_context, ct);

        BrakeFrame_1CA_AZE0? frame1CA = null;

        try
        {
            await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
            {
                if (frame.Data.Length != 8)
                    continue;

                // Use generated router for type-safe frame parsing
                if (CanFrameRouter.TryParseBrakeFrame_1CA_AZE0(frame.CanId, frame.Data.Span, out var parsed1CA))
                {
                    frame1CA = parsed1CA;
                    break; // Got what we need
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timeout - no frame received
        }
        finally
        {
            await _session.ExitMonitoringModeAsync(ct);
        }

        // If we didn't receive the frame, return default status
        if (frame1CA == null)
            return new BrakeStatus(BrakePressed: false, AbsActive: false);

        // Determine if brake is pressed by checking if any brake pressure sensor shows a significant value
        // Threshold of 5 is arbitrary but should detect light brake application
        const int BrakeThreshold = 5;
        var brakePressed = frame1CA.BrakePressure1 > BrakeThreshold
                         || frame1CA.BrakePressure2 > BrakeThreshold
                         || frame1CA.BrakePressure3 > BrakeThreshold
                         || frame1CA.BrakePressure4 > BrakeThreshold;

        // Note: ABS active status would require monitoring ABS frames for intervention signals.
        // For now, we'll set this to false as the brake frame doesn't directly indicate ABS activity.
        // To properly detect ABS activity, we would need to cross-reference with ABS frame data
        // or detect rapid brake pressure fluctuations.
        var absActive = false;

        return new BrakeStatus(
            BrakePressed: brakePressed,
            AbsActive: absActive
        );
    }
}
