using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

/// <summary>
/// Brake capability as a view over the shared <see cref="CanMonitor"/> (streaming design P3).
/// Reads brake broadcast frame 0x1CA (pressure sensors + regen level, 20ms cadence) from the
/// monitor's latest-frame cache.
/// </summary>
internal sealed class LeafAze0Brake : IBrake
{
    private static readonly TimeSpan WarmupTimeout = TimeSpan.FromSeconds(4);

    private readonly CanMonitor _monitor;

    public LeafAze0Brake(CanMonitor monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    public async ValueTask<BrakeStatus> GetStatusAsync(CancellationToken ct = default)
    {
        await _monitor.StartAsync(ct);
        await _monitor.WaitForCacheAsync(WarmupTimeout, ct, 0x1CA);

        if (!_monitor.TryGetLatest<BrakeFrame_1CA_AZE0>(out var frame1CA))
        {
            return new BrakeStatus(BrakePressed: false, AbsActive: false);
        }

        // Threshold of 5 is arbitrary but should detect light brake application.
        const int BrakeThreshold = 5;
        var brakePressed = frame1CA.BrakePressure1 > BrakeThreshold
                           || frame1CA.BrakePressure2 > BrakeThreshold
                           || frame1CA.BrakePressure3 > BrakeThreshold
                           || frame1CA.BrakePressure4 > BrakeThreshold;

        // ABS activity would require cross-referencing ABS frames; not indicated by 0x1CA.
        return new BrakeStatus(BrakePressed: brakePressed, AbsActive: false);
    }
}
