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
        /// Reads current gear position — preferring EV-CAN broadcast frame 0x11A
        /// (JoystickGearPosition), falling back to the CAR-CAN 0x421 dashboard shifter relay.
        /// </summary>
        /// <remarks>
        /// 0x11A is an EV-CAN broadcast frame: stock ELM327 adapters wire OBD pins 6/14
        /// (CAR-CAN); EV-CAN sits on pins 12/13 and needs a rewired adapter
        /// (hardware-confirmed 2026-07-18). On stock adapters the 0x421 fallback is the
        /// path that actually fires. 0x421 is 1 byte on the wire, so it's decoded from the
        /// raw cache (typed decode skips short frames); value map per OVMS
        /// vehicle_nissanleaf.cpp: 0/1=Park, 2=Reverse, 3=Neutral, 4=Drive, 7=Drive/B.
        /// </remarks>
        public async ValueTask<GearPosition> GetGearPositionAsync(CancellationToken ct = default)
        {
            await _monitor.StartAsync(ct);

            // Wait until either source shows up; on stock adapters only 0x421 ever will.
            var deadline = Environment.TickCount64 + (long)GearWarmupTimeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline &&
                   !_monitor.TryGetLatest(0x11A, out _) &&
                   !_monitor.TryGetLatest(0x421, out _))
            {
                await Task.Delay(10, ct);
            }

            if (_monitor.TryGetLatest<VcmFrame_11A_AZE0>(out var frame11A))
            {
                return frame11A.JoystickGearPosition switch
                {
                    0 => GearPosition.Park,
                    2 => GearPosition.Reverse,
                    3 => GearPosition.Neutral,
                    4 => GearPosition.Drive,
                    _ => GearPosition.Unknown
                };
            }

            if (_monitor.TryGetLatest(0x421, out var raw421) && raw421.Data.Length >= 1)
            {
                return VcmFrame_421_AZE0.ShifterPositionFromByte0(raw421.Data.Span[0]) switch
                {
                    0 or 1 => GearPosition.Park,
                    2 => GearPosition.Reverse,
                    3 => GearPosition.Neutral,
                    4 => GearPosition.Drive,
                    7 => GearPosition.Eco,
                    _ => GearPosition.Unknown
                };
            }

            return GearPosition.Unknown;
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
