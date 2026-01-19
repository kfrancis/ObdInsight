using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Protocols;
using ObdTestApp.Core.Vehicles;
using ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    /// On-board charger implementation for Nissan Leaf AZE0.
    /// Monitors OBCpd (On-Board Charger power distribution) broadcast frames.
    /// </summary>
    internal sealed class LeafAze0Charger : IOnboardCharger
    {
        private readonly IElmSession _session;
        private readonly EcuContext _context;

        public LeafAze0Charger(IElmSession session, EcuContext context)
        {
            _session = session;
            _context = context;
        }

        /// <summary>
        /// Gets current charging status by monitoring OBCpd broadcast frames:
        /// - 0x390: Charge power, charge status, max power out, AC voltage status
        /// - 0x393: Secondary status information
        /// 
        /// These frames transmit every 100ms.
        /// </summary>
        public async ValueTask<ChargingStatus?> GetChargingStatusAsync(CancellationToken ct = default)
        {
            var timeout = TimeSpan.FromMilliseconds(300); // 100ms frame rate
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await _session.EnterMonitoringModeAsync(_context, ct);

            ObcPdFrame_390_AZE0? frame390 = null;

            try
            {
                await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
                {
                    if (frame.Data.Length != 8)
                        continue;

                    // Use generated router for type-safe frame parsing
                    if (CanFrameRouter.TryParseObcPdFrame_390_AZE0(frame.CanId, frame.Data.Span, out var parsed390))
                    {
                        frame390 = parsed390;
                        break; // Got what we need for basic charging status
                    }
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Timeout - no frame received
                Log("[Charger Status] Timeout waiting for OBCpd frames");
            }
            finally
            {
                await _session.ExitMonitoringModeAsync(ct);
            }

            // If we didn't receive the frame, return null
            if (frame390 == null)
            {
                Log("[Charger Status] No OBCpd frame received");
                return null;
            }

            // Parse charging status from frame
            // ChargeStatus values: 1=Idle/QC, 2=Finished, 4=Charging/interrupted, 8/9=Idle, 12=Waiting on timer
            var isCharging = frame390.ChargeStatus == 4;
            var isPluggedIn = frame390.ChargeStatus is 2 or 4 or 12;

            Log($"[Charger Status] ChargeStatus={frame390.ChargeStatus}, ChargePower={frame390.ChargePower}kW, " +
                $"MaxPower={frame390.MaximumChargePowerOut}kW, ACVoltage={frame390.StatusAcVoltage}");

            return new ChargingStatus
            {
                IsPluggedIn = isPluggedIn,
                IsCharging = isCharging,
                ChargePowerKw = frame390.ChargePower,
                EstimatedTimeToFull = null, // Not available in these frames
                ChargerVoltage = MapAcVoltageStatus(frame390.StatusAcVoltage),
                ChargerCurrent = frame390.ChargePower > 0 && frame390.StatusAcVoltage > 0
                    ? frame390.ChargePower * 1000.0 / MapAcVoltageStatus(frame390.StatusAcVoltage)
                    : null
            };
        }

        /// <summary>
        /// Maps AC voltage status enum to actual voltage value.
        /// 0=No Signal, 1=100V, 2=200V, 3=Abnormal Wave
        /// </summary>
        private static double? MapAcVoltageStatus(int status) => status switch
        {
            1 => 100.0,
            2 => 200.0,
            _ => null
        };

        private static void Log(string message)
        {
            Serilog.Log.Debug(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}


