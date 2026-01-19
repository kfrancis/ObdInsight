using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Protocols;
using ObdTestApp.Core.Vehicles;
using ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    /// Motor controller/inverter implementation for Nissan Leaf AZE0.
    /// Monitors INVmc (Inverter Motor Controller) broadcast frames.
    /// </summary>
    internal sealed class LeafAze0MotorController : IMotorController
    {
        readonly IElmSession _session;
        readonly EcuContext _context;

        public LeafAze0MotorController(IElmSession session, EcuContext context)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            if (_context.CommunicationMode != EcuCommunicationMode.PassiveMonitoring)
                throw new ArgumentException("Motor controller status requires PassiveMonitoring context for broadcast frames.", nameof(context));
        }

        /// <summary>
        /// Reads current motor and inverter status by monitoring INVmc broadcast frames:
        /// - 0x1DA (10ms): Motor voltage, torque, RPM, error codes
        /// - 0x55A (100ms): Motor and inverter temperatures
        /// </summary>
        public async ValueTask<MotorStatus> GetStatusAsync(CancellationToken ct = default)
        {
            var timeout = TimeSpan.FromMilliseconds(250); // Should catch both frames
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await _session.EnterMonitoringModeAsync(_context, ct);

            InvMcFrame_1DA_AZE0? frame1DA = null;
            InvMcFrame_55A_AZE0? frame55A = null;

            try
            {
                await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
                {
                    if (frame.Data.Length != 8)
                        continue;

                    // Use generated router for type-safe frame parsing
                    if (CanFrameRouter.TryParseInvMcFrame_1DA_AZE0(frame.CanId, frame.Data.Span, out var parsed1DA))
                    {
                        frame1DA = parsed1DA;
                    }
                    else if (CanFrameRouter.TryParseInvMcFrame_55A_AZE0(frame.CanId, frame.Data.Span, out var parsed55A))
                    {
                        frame55A = parsed55A;
                    }

                    // Exit early if we have both frames
                    if (frame1DA != null && frame55A != null)
                        break;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Timeout - return partial data
                Log("[MotorController] Timeout waiting for INVmc frames");
            }
            finally
            {
                await _session.ExitMonitoringModeAsync(ct);
            }

            // Log what we received
            if (frame1DA != null)
            {
                Log($"[MotorController] 0x1DA: Voltage={frame1DA.InputVoltage}V, Torque={frame1DA.EffectiveTorque}Nm, " +
                    $"RPM={frame1DA.OutputRevolution}, Errors=0x{frame1DA.ErrorCodes:X2}");
            }
            if (frame55A != null)
            {
                Log($"[MotorController] 0x55A: Motor={frame55A.MotorTemperatureC:F1}°C, IGBT={frame55A.IgbtTemperatureC:F1}°C, " +
                    $"ComBoard={frame55A.InverterComBoardTempC:F1}°C, DriverBoard={frame55A.IgbtDriverBoardTempC:F1}°C");
            }

            // Map to generic interface
            return new MotorStatus
            {
                InputVoltageV = frame1DA?.InputVoltage,
                EffectiveTorqueNm = frame1DA?.EffectiveTorque,
                OutputRevolutionRpm = frame1DA?.OutputRevolution,
                ErrorCodes = frame1DA?.ErrorCodes,
                MotorTempC = frame55A?.MotorTemperatureC,
                InverterComBoardTempC = frame55A?.InverterComBoardTempC,
                IgbtTempC = frame55A?.IgbtTemperatureC,
                IgbtDriverBoardTempC = frame55A?.IgbtDriverBoardTempC
            };
        }

        private static void Log(string message)
        {
            Serilog.Log.Debug(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
