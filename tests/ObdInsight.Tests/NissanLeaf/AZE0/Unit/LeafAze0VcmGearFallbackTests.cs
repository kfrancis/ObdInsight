using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
/// Gear position via the CAR-CAN 0x421 shifter relay — the path that actually fires on
/// stock ELM327 adapters, where EV-CAN 0x11A never appears (see CLAUDE.md gotcha).
/// Exercises the production path end-to-end: 1-byte frame through ElmSession/CanMonitor
/// raw cache into LeafAze0Vcm's fallback decode.
/// </summary>
[Timeout(30_000)]
public class LeafAze0VcmGearFallbackTests
{
    [Test]
    public async Task GetGearPosition_No11A_FallsBackToCarCan421(CancellationToken token)
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var commands = new LeafAze0CommandSet(session);
        commands.Monitor.RestartDelay = TimeSpan.Zero;
        transport.AutoRespond("ATMA", "");

        commands.TryGet<IVcm>(out var vcm);

        var gearTask = vcm!.GetGearPositionAsync(token);

        // Only the 1-byte CAR-CAN 0x421 frame arrives (0x20 => bits 3-5 = 4 = Drive).
        // Re-enqueue while polling: a rotation window transition can eat frames that land
        // during the Enter sequence (same pattern as the streaming command-set test).
        while (!transport.SentCommands.Contains("ATMA"))
            await Task.Delay(10, token);
        while (!commands.Monitor.TryGetLatest(0x421, out _))
        {
            transport.EnqueueIncoming("421 20\r");
            await Task.Delay(20, token);
        }

        var gear = await gearTask;
        await Assert.That(gear).IsEqualTo(GearPosition.Drive);

        await commands.Monitor.StopAsync(token);
    }
}
