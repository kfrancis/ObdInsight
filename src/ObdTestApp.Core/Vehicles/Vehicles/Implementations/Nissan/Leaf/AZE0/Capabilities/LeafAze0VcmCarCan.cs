using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Protocols;
using ObdTestApp.Core.Vehicles.Implementations.Nissan.AZE0;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

/// <summary>
/// Internal helper for VCM operations on the CAR-CAN bus.
/// Handles frames 0x174, 0x176, 0x180, 0x260, 0x421, 0x50D, 0x510, etc.
/// </summary>
internal sealed class LeafAze0VcmCarCan
{
    private readonly EcuContext _context;
    private readonly IElmSession _session;

    public LeafAze0VcmCarCan(IElmSession session, EcuContext context)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (_context.CommunicationMode != EcuCommunicationMode.PassiveMonitoring)
            throw new ArgumentException("VCM CAR-CAN requires PassiveMonitoring context for broadcast frames.", nameof(context));
    }

    /// <summary>
    /// Gets comprehensive VCM status by monitoring frame 0x510 on CAR-CAN.
    /// Frame 0x510 contains power consumption, climate control status, and ambient temperature.
    /// This frame is transmitted at approximately 100ms intervals.
    /// </summary>
    public async ValueTask<VcmStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromMilliseconds(300); // 100ms frame rate, allow extra time
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        await _session.EnterMonitoringModeAsync(_context, ct);

        VcmFrame_510_AZE0? frame510 = null;

        try
        {
            await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
            {
                if (frame.Data.Length != 8)
                    continue;

                // Use generated router for type-safe frame parsing
                if (CanFrameRouter.TryParseVcmFrame_510_AZE0(frame.CanId, frame.Data.Span, out var parsed510))
                {
                    frame510 = parsed510;
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

        // If we didn't receive the frame, return empty status
        if (frame510 == null)
        {
            return new VcmStatus();
        }

        // Map frame data to VcmStatus
        return new VcmStatus
        {
            ClimateControlActive = frame510.ClimateControlActive,
            ClimateControlPowerKw = frame510.ClimateControlPowerConsumption,
            OutsideAmbientTempC = frame510.OutsideAmbientTemperature,
            IntegratedMotorPowerConsumption = frame510.IntegratedPowerConsumptionMotor,
            IntegratedAcPowerConsumption = frame510.IntegratedPowerConsumptionAc,
            IntegratedAuxPowerConsumption = frame510.IntegratedPowerConsumptionAux,
            PowerConsumptionAux = frame510.PowerConsumptionAux,
            EcoIndicator = frame510.EcoIndicator,
            EcoTree = frame510.EcoTree,
            ChargeMode = frame510.ChargeMode
        };
    }
}
