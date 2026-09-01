using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    ///     Motor controller/inverter capability as a view over the shared <see cref="CanMonitor" />
    ///     (streaming design P2). Reads INVmc broadcast frames — 0x1DA (10ms: voltage, torque, RPM,
    ///     error codes) and 0x55A (100ms: temperatures) — from the monitor's latest-frame cache.
    /// </summary>
    /// <remarks>
    ///     LIMITATION (hardware-confirmed 2026-07-18): 0x1DA and 0x55A are EV-CAN broadcast
    ///     frames. Stock ELM327 adapters wire OBD pins 6/14 (CAR-CAN); EV-CAN sits on pins
    ///     12/13 and needs a rewired/modified adapter to monitor. On stock adapters this
    ///     capability's warmup times out and every field returns null. Inverter data would need
    ///     a UDS query alternative (works over CAR-CAN, like the BMS capability) or a modified
    ///     adapter; see docs/FRAME_LAYOUT_AUDIT.md.
    /// </remarks>
    internal sealed class LeafAze0MotorController : IMotorController
    {
        /// <summary>How long a cold cache is given for the first frames to arrive.</summary>
        private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(4);

        /// <summary>The frames that feed <see cref="MotorStatus" />.</summary>
        private static readonly int[] StatusFrameIds = [0x1DA, 0x55A];

        private readonly CanMonitor _monitor;

        public LeafAze0MotorController(CanMonitor monitor)
        {
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        }

        public async ValueTask<MotorStatus> GetStatusAsync(CancellationToken ct = default)
        {
            await _monitor.StartAsync(ct);

            var deadline = Environment.TickCount64 + (long)WarmupTimeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline &&
                   !(_monitor.TryGetLatest(0x1DA, out _) && _monitor.TryGetLatest(0x55A, out _)))
            {
                await Task.Delay(10, ct);
            }

            return BuildStatus();
        }

        public IAsyncEnumerable<MotorStatus> StreamStatusAsync(
            TimeSpan minInterval = default,
            CancellationToken ct = default)
        {
            return _monitor.StreamSnapshots(StatusFrameIds, BuildStatus, minInterval, ct);
        }

        private MotorStatus BuildStatus()
        {
            _monitor.TryGetLatest<InvMcFrame_1DA_AZE0>(out var frame1DA);
            _monitor.TryGetLatest<InvMcFrame_55A_AZE0>(out var frame55A);

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
    }
}
