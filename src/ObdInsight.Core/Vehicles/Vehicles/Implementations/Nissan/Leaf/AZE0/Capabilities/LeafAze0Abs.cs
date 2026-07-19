using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

/// <summary>
/// ABS capability as a view over the shared <see cref="CanMonitor"/> (streaming design P3).
/// Reads ABS broadcast frames (0x130/0x245/0x284/0x285/0x292/0x354, 20ms cadence) from the
/// monitor's latest-frame cache.
/// </summary>
internal sealed class LeafAze0Abs : IAntilockBrakingSystem
{
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(4);

    private readonly CanMonitor _monitor;

    public LeafAze0Abs(CanMonitor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    public async ValueTask<AbsStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _monitor.StartAsync(ct);
        await _monitor.WaitForCacheAsync(WarmupTimeout, ct, 0x130, 0x284, 0x285, 0x354);

        _monitor.TryGetLatest<AbsFrame_130_AZE0>(out var frame130);
        _monitor.TryGetLatest<AbsFrame_245_AZE0>(out var frame245);
        _monitor.TryGetLatest<AbsFrame_284_AZE0>(out var frame284);
        _monitor.TryGetLatest<AbsFrame_285_AZE0>(out var frame285);
        _monitor.TryGetLatest<AbsFrame_292_AZE0>(out var frame292);
        _monitor.TryGetLatest<AbsFrame_354_AZE0>(out var frame354);

        return new AbsStatus
        {
            WheelSpeedFrKmh = frame284?.WheelSpeedFr,
            WheelSpeedFlKmh = frame284?.WheelSpeedFl,
            VehicleSpeedKmh = frame284?.VehicleSpeedFromAbs,
            WheelSpeedRrKmh = frame285?.WheelSpeedRr,
            WheelSpeedRlKmh = frame285?.WheelSpeedRl,
            VehicleSpeedPulses = frame354?.VehicleSpeedAbs,
            EspDisabled = frame354?.EspDisabled,
            LeadAcidBatteryVoltage = frame292?.LeadAcidBatteryVoltage,
            FrictionBrakePressure = frame292?.FrictionBrakePressure,
            VdcTorqueDownRequest1Nm = frame245?.VdcTorqueDownRequest1,
            VdcTorqueDownRequest2Nm = frame245?.VdcTorqueDownRequest2,
            MotorTorqueRequestNm = frame245?.MotorTorqueRequestAbs,
            BitmaskAbs = frame130?.BitmaskAbs
        };
    }
}
