using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.AZE0;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    /// VCM capability as a view over the shared <see cref="CanMonitor"/> (streaming design P3).
    /// Replaces the former EV-CAN/CAR-CAN helper split — the shared monitor's cache holds
    /// frames from both buses, demuxed by CAN ID:
    /// 0x11A (gear position, 10ms), 0x510 (power/climate, primary status) with 0x180
    /// (motor current/throttle) as fallback.
    /// </summary>
    internal sealed class LeafAze0Vcm : IVcm
    {
        private static readonly TimeSpan GearWarmupTimeout = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan StatusWarmupTimeout = TimeSpan.FromSeconds(4);

        private readonly CanMonitor _monitor;

        public LeafAze0Vcm(CanMonitor monitor)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        }

        /// <summary>
        /// Reads current gear position from EV-CAN broadcast frame 0x11A
        /// (JoystickGearPosition: 0=Park, 2=Reverse, 3=Neutral, 4=Drive/B).
        /// </summary>
        public async ValueTask<GearPosition> GetGearPositionAsync(CancellationToken ct = default)
        {
            await _monitor.StartAsync(ct);
            await _monitor.WaitForCacheAsync(GearWarmupTimeout, ct, 0x11A);

            if (!_monitor.TryGetLatest<VcmFrame_11A_AZE0>(out var frame11A))
            {
                return GearPosition.Unknown;
            }

            return frame11A.JoystickGearPosition switch
            {
                0 => GearPosition.Park,
                2 => GearPosition.Reverse,
                3 => GearPosition.Neutral,
                4 => GearPosition.Drive,
                _ => GearPosition.Unknown
            };
        }

        /// <summary>
        /// Gets comprehensive VCM status. Prefers frame 0x510 (power consumption, climate,
        /// ambient temp); falls back to 0x180 (motor current, throttle) which broadcasts
        /// even when stationary.
        /// </summary>
        public async ValueTask<VcmStatus> GetStatusAsync(CancellationToken ct = default)
        {
            await _monitor.StartAsync(ct);
            await _monitor.WaitForCacheAsync(StatusWarmupTimeout, ct, 0x510);

            if (_monitor.TryGetLatest<VcmFrame_510_AZE0>(out var frame510))
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

            if (_monitor.TryGetLatest<VcmFrame_180_AZE0>(out var frame180))
            {
                return new VcmStatus
                {
                    MotorCurrentAmps = frame180.MotorAmp,
                    ThrottlePositionPercent = frame180.ThrottlePosition
                };
            }

            return new VcmStatus();
        }
    }
}
