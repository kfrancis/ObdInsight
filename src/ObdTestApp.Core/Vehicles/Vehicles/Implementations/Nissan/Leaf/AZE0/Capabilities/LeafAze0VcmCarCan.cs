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
    /// Gets comprehensive VCM status by monitoring frames on CAR-CAN.
    /// Prioritizes frame 0x510 (power consumption, climate, ambient temp) but falls back to
    /// frame 0x180 (motor current, throttle) which broadcasts even when stationary.
    /// </summary>
    public async ValueTask<VcmStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromMilliseconds(300); // Allow time to collect frames
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        await _session.EnterMonitoringModeAsync(_context, ct);

        VcmFrame_510_AZE0? frame510 = null;
        VcmFrame_180_AZE0? frame180 = null;

        try
        {
            await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
            {
                if (frame.Data.Length != 8)
                    continue;

                // Try to parse both frames - 0x510 (primary) and 0x180 (fallback)
                if (CanFrameRouter.TryParseVcmFrame_510_AZE0(frame.CanId, frame.Data.Span, out var parsed510))
                {
                    frame510 = parsed510;
                    break; // Got the primary frame, we're done
                }
                else if (CanFrameRouter.TryParseVcmFrame_180_AZE0(frame.CanId, frame.Data.Span, out var parsed180))
                {
                    frame180 = parsed180;
                    // Don't break - keep looking for 0x510 which has more data
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timeout - use whatever frames we collected
        }
        finally
        {
            await _session.ExitMonitoringModeAsync(ct);
        }

        // Prefer frame 0x510 if available, otherwise use 0x180
        if (frame510 != null)
        {
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
        else if (frame180 != null)
        {
            // Frame 0x180 has limited data (motor current, throttle) but better than nothing
            return new VcmStatus
            {
                MotorCurrentAmps = frame180.MotorAmp,
                ThrottlePositionPercent = frame180.ThrottlePosition
            };
        }

        // No data available
        return new VcmStatus();
    }
}
