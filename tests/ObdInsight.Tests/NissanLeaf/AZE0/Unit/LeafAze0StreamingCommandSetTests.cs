using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
/// Whole-model test for the streaming migration (design P2/P3): broadcast capabilities read
/// the shared CanMonitor while UDS capabilities transparently suspend/resume it around
/// queries — both through the production LeafAze0CommandSet over replay.
/// </summary>
[Timeout(30_000)]
public class LeafAze0StreamingCommandSetTests
{
    [Test]
    public async Task Hvac_And_BmsQuery_Interleave_OverSharedMonitor(CancellationToken token)
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var commands = new LeafAze0CommandSet(session);
        commands.Monitor.RestartDelay = TimeSpan.Zero;

        // The command-set monitor rotates filter windows, so ATMA repeats unboundedly.
        transport.AutoRespond("ATMA", "");
        // BMS query suspends the monitor (exit + query) and the rotation resumes after.
        transport.Expect("2101", LeafGoldenData.GoldenGroup01Lines.AsElmResponse());

        commands.TryGet<IHvac>(out var hvac);
        commands.TryGet<IBatteryManagementSystem>(out var bms);

        // --- HVAC via the running monitor ---
        var hvacTask = hvac.GetStatusAsync(token);
        // Enter clears residual buffer bytes — wait until monitoring is live, and re-enqueue
        // while polling so a rotation window transition cannot eat the frames.
        while (!transport.SentCommands.Contains("ATMA"))
            await Task.Delay(10, token);
        while (!(commands.Monitor.TryGetLatest(0x54C, out _) &&
                 commands.Monitor.TryGetLatest(0x54B, out _) &&
                 commands.Monitor.TryGetLatest(0x54F, out _) &&
                 commands.Monitor.TryGetLatest(0x54A, out _)))
        {
            // 0x54C: OutsideAmbientTemp bits 48-55, factor 0.5, offset -40 => raw 150 = 35.0 °C
            transport.EnqueueIncoming($"54C {Bytes(150ul << 48)}\r");
            // 0x54B: FanSpeed bits 35-39 => 3
            transport.EnqueueIncoming($"54B {Bytes(3ul << 35)}\r");
            // 0x54F: zeros => InteriorIntakeTemp raw 0 = -14.0 °C, AC power 0 W
            transport.EnqueueIncoming($"54F {Bytes(0)}\r");
            transport.EnqueueIncoming($"54A {Bytes(0)}\r");
            await Task.Delay(20, token);
        }

        var hvacStatus = await hvacTask;
        await Assert.That(hvacStatus.OutsideAmbientTempC).IsEqualTo(35.0);
        await Assert.That(hvacStatus.FanSpeed).IsEqualTo(3);
        await Assert.That(hvacStatus.AcPowerWatts).IsEqualTo(0);
        await Assert.That(commands.Monitor.IsRunning).IsTrue();

        // --- BMS UDS query: must suspend the monitor, run, and resume it ---
        var battery = await bms.GetStatusAsync(token);
        await Assert.That(battery.VoltageVolts).IsNotNull();
        await Assert.That(Math.Abs(battery.VoltageVolts!.Value - 361.78)).IsLessThan(0.01);
        // Monitor resumed after the query. (No mode assert: with filter rotation the session
        // is legitimately in RequestResponse for an instant between windows.)
        await Assert.That(commands.Monitor.IsRunning).IsTrue();

        // --- HVAC again: warm cache, no adapter round-trip, updated by fresh frames ---
        // Re-enqueue while polling: a window transition's buffer clear can eat a frame
        // that lands exactly during the Enter sequence.
        while (!(commands.Monitor.TryGetLatest(0x54C, out var f) && f.Data.Span[6] == 120))
        {
            transport.EnqueueIncoming($"54C {Bytes(120ul << 48)}\r"); // raw 120 => 20.0 °C
            await Task.Delay(20, token);
        }

        var updated = await hvac.GetStatusAsync(token);
        await Assert.That(updated.OutsideAmbientTempC).IsEqualTo(20.0);

        await commands.Monitor.StopAsync(token);
    }

    private static string Bytes(ulong raw) =>
        string.Join(" ", BitConverter.GetBytes(raw).Select(b => b.ToString("X2")));
}
