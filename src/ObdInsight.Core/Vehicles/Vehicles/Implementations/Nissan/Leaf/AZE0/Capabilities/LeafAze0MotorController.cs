using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    /// Motor controller/inverter capability as a view over the shared <see cref="CanMonitor"/>
    /// (streaming design P2). Reads INVmc broadcast frames — 0x1DA (10ms: voltage, torque, RPM,
    /// error codes) and 0x55A (100ms: temperatures) — from the monitor's latest-frame cache.
    /// </summary>
    internal sealed class LeafAze0MotorController : IMotorController
    {
        /// <summary>How long a cold cache is given for the first frames to arrive.</summary>
        private static readonly TimeSpan WarmupTimeout = TimeSpan.FromMilliseconds(300);

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
