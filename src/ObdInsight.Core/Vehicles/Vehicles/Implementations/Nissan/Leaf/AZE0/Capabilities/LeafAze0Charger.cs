using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    ///     On-board charger capability as a view over the shared <see cref="CanMonitor" />
    ///     (streaming design P3). Reads OBCpd broadcast frame 0x390 (charge power/status,
    ///     100ms cadence) from the monitor's latest-frame cache.
    /// </summary>
    internal sealed class LeafAze0Charger : IOnboardCharger
    {
        private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(4);

        /// <summary>The frames that feed <see cref="ChargingStatus" />.</summary>
        private static readonly int[] StatusFrameIds = [0x390];

        private readonly CanMonitor _monitor;

        public LeafAze0Charger(CanMonitor monitor)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        }

        public async ValueTask<ChargingStatus?> GetChargingStatusAsync(CancellationToken ct = default)
        {
            await _monitor.StartAsync(ct);
            await _monitor.WaitForCacheAsync(WarmupTimeout, ct, 0x390);

            return BuildStatus();
        }

        public IAsyncEnumerable<ChargingStatus?> StreamChargingStatusAsync(
            TimeSpan minInterval = default,
            CancellationToken ct = default)
        {
            return _monitor.StreamSnapshots(StatusFrameIds, BuildStatus, minInterval, ct);
        }

        private ChargingStatus? BuildStatus()
        {
            if (!_monitor.TryGetLatest<ObcPdFrame_390_AZE0>(out var frame390))
            {
                return null;
            }

            // ChargeStatus values: 1=Idle/QC, 2=Finished, 4=Charging/interrupted, 8/9=Idle, 12=Waiting on timer
            var isCharging = frame390.ChargeStatus == 4;
            var isPluggedIn = frame390.ChargeStatus is 2 or 4 or 12;

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
        ///     Maps AC voltage status enum to actual voltage value.
        ///     0=No Signal, 1=100V, 2=200V, 3=Abnormal Wave
        /// </summary>
        private static double? MapAcVoltageStatus(int status) => status switch
        {
            1 => 100.0,
            2 => 200.0,
            _ => null
        };
    }
}
