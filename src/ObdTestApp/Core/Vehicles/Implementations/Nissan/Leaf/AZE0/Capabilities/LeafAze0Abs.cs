using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Protocols;
using ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

/// <summary>
/// ABS (Anti-lock Braking System) capability implementation for Nissan Leaf AZE0 platform.
/// Monitors multiple ABS broadcast frames to gather wheel speed, vehicle speed, and traction control status.
/// </summary>
internal sealed class LeafAze0Abs : IAntilockBrakingSystem
{
    readonly IElmSession _session;
    readonly EcuContext _context;

    public LeafAze0Abs(IElmSession session, EcuContext context)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (_context.CommunicationMode != EcuCommunicationMode.PassiveMonitoring)
            throw new ArgumentException("ABS status requires PassiveMonitoring context for broadcast frames.", nameof(context));
    }

    /// <summary>
    /// Reads ABS status by monitoring multiple Leaf AZE0 ABS broadcast frames:
    /// - 0x130: ABS status bitmask
    /// - 0x245: VDC torque control requests
    /// - 0x284: Front wheel speeds and vehicle speed
    /// - 0x285: Rear wheel speeds
    /// - 0x292: Lead-acid battery voltage and friction brake pressure
    /// - 0x354: Vehicle speed pulses and ESP status
    /// These frames transmit every 20ms.
    /// </summary>
    public async ValueTask<AbsStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromMilliseconds(300); // 20ms frame rate, collect for 300ms
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        await _session.EnterMonitoringModeAsync(_context, ct);

        AbsFrame_130_AZE0? frame130 = null;
        AbsFrame_245_AZE0? frame245 = null;
        AbsFrame_284_AZE0? frame284 = null;
        AbsFrame_285_AZE0? frame285 = null;
        AbsFrame_292_AZE0? frame292 = null;
        AbsFrame_354_AZE0? frame354 = null;

        try
        {
            await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
            {
                if (frame.Data.Length < 3)
                    continue;

                // Use generated router for type-safe frame parsing
                if (CanFrameRouter.TryParseAbsFrame_130_AZE0(frame.CanId, frame.Data.Span, out var parsed130))
                {
                    frame130 = parsed130;
                }
                else if (frame.Data.Length >= 8)
                {
                    if (CanFrameRouter.TryParseAbsFrame_245_AZE0(frame.CanId, frame.Data.Span, out var parsed245))
                    {
                        frame245 = parsed245;
                    }
                    else if (CanFrameRouter.TryParseAbsFrame_284_AZE0(frame.CanId, frame.Data.Span, out var parsed284))
                    {
                        frame284 = parsed284;
                    }
                    else if (CanFrameRouter.TryParseAbsFrame_285_AZE0(frame.CanId, frame.Data.Span, out var parsed285))
                    {
                        frame285 = parsed285;
                    }
                    else if (CanFrameRouter.TryParseAbsFrame_292_AZE0(frame.CanId, frame.Data.Span, out var parsed292))
                    {
                        frame292 = parsed292;
                    }
                    else if (CanFrameRouter.TryParseAbsFrame_354_AZE0(frame.CanId, frame.Data.Span, out var parsed354))
                    {
                        frame354 = parsed354;
                    }
                }

                // If we have collected all the main frames, we can exit early
                if (frame130 != null && frame284 != null && frame285 != null && frame354 != null)
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
        return new AbsStatus
        {
            // Frame 0x284: Front wheel speeds and vehicle speed
            WheelSpeedFrKmh = frame284?.WheelSpeedFr,
            WheelSpeedFlKmh = frame284?.WheelSpeedFl,
            VehicleSpeedKmh = frame284?.VehicleSpeedFromAbs,

            // Frame 0x285: Rear wheel speeds
            WheelSpeedRrKmh = frame285?.WheelSpeedRr,
            WheelSpeedRlKmh = frame285?.WheelSpeedRl,

            // Frame 0x354: Vehicle speed pulses and ESP status
            VehicleSpeedPulses = frame354?.VehicleSpeedAbs,
            EspDisabled = frame354?.EspDisabled,

            // Frame 0x292: Battery voltage and brake pressure
            LeadAcidBatteryVoltage = frame292?.LeadAcidBatteryVoltage,
            FrictionBrakePressure = frame292?.FrictionBrakePressure,

            // Frame 0x245: VDC torque control
            VdcTorqueDownRequest1Nm = frame245?.VdcTorqueDownRequest1,
            VdcTorqueDownRequest2Nm = frame245?.VdcTorqueDownRequest2,
            MotorTorqueRequestNm = frame245?.MotorTorqueRequestAbs,

            // Frame 0x130: ABS bitmask
            BitmaskAbs = frame130?.BitmaskAbs
        };
    }
}
