using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
///     Range estimate (roadmap B8): VcmStatus.RangeKm fed from CAR-CAN 0x5A9 through the
///     shared monitor cache, with the 0xFFF "charging" sentinel mapped to null. Production
///     path over replay: LeafAze0CommandSet → LeafAze0Vcm → CanMonitor cache.
/// </summary>
[Timeout(30_000)]
public class LeafAze0VcmRangeTests
{
    [Test]
    public async Task GetStatus_FillsRangeKm_From5A9Capture(CancellationToken token)
    {
        var (transport, commands) = Setup();

        commands.TryGet<IVcm>(out var vcm);
        var statusTask = vcm.GetStatusAsync(token);

        // Hardware-locked 2026-07-18 capture: 179.2 km (dash ground truth ~179).
        // Re-enqueue while polling — a rotation window transition can eat frames.
        while (!(commands.Monitor.TryGetLatest(0x5A9, out _) &&
                 commands.Monitor.TryGetLatest(0x510, out _)))
        {
            transport.EnqueueIncoming("5A9 85 26 C0 11 04 10 00 00\r");
            transport.EnqueueIncoming("510 00 00 00 00 00 00 00 00\r");
            await Task.Delay(20, token);
        }

        var status = await statusTask;
        await Assert.That(status.RangeKm).IsNotNull();
        await Assert.That(status.RangeKm!.Value).IsEqualTo(179.2).Within(1e-9);

        await commands.Monitor.StopAsync(token);
    }

    [Test]
    public async Task GetStatus_RangeKmNull_OnChargingSentinel(CancellationToken token)
    {
        var (transport, commands) = Setup();

        commands.TryGet<IVcm>(out var vcm);
        var statusTask = vcm.GetStatusAsync(token);

        // Raw 0xFFF (bits 15-26 set) = "charging" sentinel → RangeKm must be null.
        while (!(commands.Monitor.TryGetLatest(0x5A9, out _) &&
                 commands.Monitor.TryGetLatest(0x510, out _)))
        {
            transport.EnqueueIncoming("5A9 00 80 FF 07 00 00 00 00\r");
            transport.EnqueueIncoming("510 00 00 00 00 00 00 00 00\r");
            await Task.Delay(20, token);
        }

        var status = await statusTask;
        await Assert.That(status.RangeKm).IsNull();

        await commands.Monitor.StopAsync(token);
    }

    [Test]
    public async Task GetStatus_RangeKmNull_WhenFrameAbsent(CancellationToken token)
    {
        var (transport, commands) = Setup();

        commands.TryGet<IVcm>(out var vcm);
        var statusTask = vcm.GetStatusAsync(token);

        while (!commands.Monitor.TryGetLatest(0x510, out _))
        {
            transport.EnqueueIncoming("510 00 00 00 00 00 00 00 00\r");
            await Task.Delay(20, token);
        }

        var status = await statusTask;
        await Assert.That(status.RangeKm).IsNull();

        await commands.Monitor.StopAsync(token);
    }

    private static (ReplayElmTransport transport, LeafAze0CommandSet commands) Setup()
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var commands = new LeafAze0CommandSet(session);
        commands.Monitor.RestartDelay = TimeSpan.Zero;
        transport.AutoRespond("ATMA", "");
        return (transport, commands);
    }
}
