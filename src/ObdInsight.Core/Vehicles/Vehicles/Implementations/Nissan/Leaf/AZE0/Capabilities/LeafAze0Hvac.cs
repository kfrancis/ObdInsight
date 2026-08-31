using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.AZE0;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    /// HVAC capability as a view over the shared <see cref="CanMonitor"/> (streaming design P2).
    /// Reads Leaf AZE0 HVAC broadcast frames (0x54A/0x54B/0x54C/0x54F, ~100ms cadence) from the
    /// monitor's latest-frame cache instead of entering/exiting monitoring mode per call.
    /// </summary>
    internal sealed class LeafAze0Hvac : IHvac
    {
        /// <summary>How long a cold cache is given for the first frames to arrive.</summary>
        private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(4);

        private readonly CanMonitor _monitor;

        public LeafAze0Hvac(CanMonitor monitor)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        }

        /// <summary>The frames that feed <see cref="HvacStatus" />.</summary>
        private static readonly int[] StatusFrameIds = [0x54A, 0x54B, 0x54C, 0x54F];

        public async ValueTask<HvacStatus> GetStatusAsync(CancellationToken ct = default)
        {
            await _monitor.StartAsync(ct);

            // Warm cache: instant. Cold: wait briefly for the broadcast frames to appear;
            // partial data is acceptable (mirrors the previous per-call collection window).
            var deadline = Environment.TickCount64 + (long)WarmupTimeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline &&
                   !(_monitor.TryGetLatest(0x54C, out _) &&
                     _monitor.TryGetLatest(0x54B, out _) &&
                     _monitor.TryGetLatest(0x54F, out _)))
            {
                await Task.Delay(10, ct);
            }

            return BuildStatus();
        }

        public IAsyncEnumerable<HvacStatus> StreamStatusAsync(
            TimeSpan minInterval = default,
            CancellationToken ct = default)
        {
            return _monitor.StreamSnapshots(StatusFrameIds, BuildStatus, minInterval, ct);
        }

        private HvacStatus BuildStatus()
        {
            _monitor.TryGetLatest<HvacFrame_54A_AZE0>(out var ambient);
            _monitor.TryGetLatest<HvacFrame_54B_AZE0>(out var fan);
            _monitor.TryGetLatest<HvacFrame_54C_AZE0>(out var status);
            _monitor.TryGetLatest<HvacFrame_54F_AZE0>(out var power);

            return new HvacStatus
            {
                ClimateControlOn = status?.ClimateControlOn ?? false,
                AcOn = status?.AcOn ?? false,
                RearDefrostOn = status?.RearDefrostOn ?? false,
                OutsideAmbientTempC = status?.OutsideAmbientTemp,
                EvaporatorTempC = status?.EvaporatorTemp,
                FanVoltageV = status?.FanVoltage,
                FanSpeed = fan?.FanSpeed,
                InteriorIntakeTempC = power?.InteriorIntakeTemp,
                AcPowerWatts = power?.AcPowerWatts,
                HeaterPowerWatts = power?.HeaterPowerWatts,
                ClimateSetpoint = ambient?.ClimateControlSetpoint,
                AmbientTempAc = ambient?.AmbientTempAc
            };
        }
    }
}
