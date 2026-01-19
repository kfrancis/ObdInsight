using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Protocols;
using ObdTestApp.Core.Vehicles;
using ObdTestApp.Core.Vehicles.Implementations.Nissan.AZE0;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    internal sealed class LeafAze0Vcm : IVcm
    {
        readonly IElmSession _session;
        readonly EcuContext _context;

        public LeafAze0Vcm(IElmSession session, EcuContext context)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            if (_context.CommunicationMode != EcuCommunicationMode.PassiveMonitoring)
                throw new ArgumentException("VCM status requires PassiveMonitoring context for broadcast frames.", nameof(context));
        }

        /// <summary>
        /// Reads current gear position by monitoring Leaf AZE0 VCM broadcast frame 0x11A.
        /// Frame 0x11A contains JoystickGearPosition in bits 4-7:
        /// - 0 = Park
        /// - 2 = Reverse
        /// - 3 = Neutral
        /// - 4 = Drive/B
        /// This frame transmits every 10ms.
        /// </summary>
        public async ValueTask<GearPosition> GetGearPositionAsync(CancellationToken ct = default)
        {
            var timeout = TimeSpan.FromMilliseconds(200); // 10ms frame rate, should get it quickly
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await _session.EnterMonitoringModeAsync(_context, ct);

            VcmFrame_11A_AZE0? frame11A = null;

            try
            {
                await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
                {
                    if (frame.Data.Length != 8)
                        continue;

                    // Use generated router for type-safe frame parsing
                    if (CanFrameRouter.TryParseVcmFrame_11A_AZE0(frame.CanId, frame.Data.Span, out var parsed11A))
                    {
                        frame11A = parsed11A;
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

            // If we didn't receive the frame, return Unknown
            if (frame11A == null)
                return GearPosition.Unknown;

            // Map JoystickGearPosition to GearPosition enum
            return frame11A.JoystickGearPosition switch
            {
                0 => GearPosition.Park,
                2 => GearPosition.Reverse,
                3 => GearPosition.Neutral,
                4 => GearPosition.Drive,
                _ => GearPosition.Unknown
            };
        }
    }
}
