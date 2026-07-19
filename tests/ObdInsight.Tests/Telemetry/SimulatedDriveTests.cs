using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;
using ObdInsight.Telemetry;

namespace OdbTestApp.Tests.Telemetry;

/// <summary>
/// Roadmap B2 acceptance: a full pre-check → live drive → post-check flow through the
/// B1 <see cref="ITelemetrySession"/> API against the simulated Leaf — zero hardware,
/// zero scripted expectations; the simulator answers the real init/protocol sequence,
/// UDS queries, and streams evolving CAR-CAN broadcast data.
/// </summary>
[Timeout(60_000)]
public class SimulatedDriveTests
{
    [Test]
    public async Task PreCheck_Drive_PostCheck_AgainstSimulatedLeaf(CancellationToken token)
    {
        // 120× time compression: 1 wall-second ≈ 2 simulated minutes of driving.
        var transport = new SimulatedLeafAze0Transport(timeScale: 120);
        var session = new ElmSession(new ElmFramer(transport));
        await session.InitializeAndLockAsync(token);

        var commands = new LeafAze0CommandSet(session);
        commands.Monitor.RestartDelay = TimeSpan.Zero;

        var options = new TelemetrySessionOptions
        {
            HighPeriod = TimeSpan.FromMilliseconds(150),
            MediumPeriod = TimeSpan.FromMilliseconds(400),
            LowPeriod = TimeSpan.FromSeconds(2),
            CacheReadTimeout = TimeSpan.FromMilliseconds(500),
        };

        // Each UDS read costs a monitor suspend/resume cycle — keep the heavy 96-cell
        // read at Low so High-tier ticks stay fast (mirrors real-adapter usage guidance).
        var subscription = new TelemetrySubscription(new Dictionary<TelemetrySignal, CadenceTier>
        {
            [TelemetrySignal.StateOfCharge] = CadenceTier.High,
            [TelemetrySignal.PackVoltage] = CadenceTier.High,
            [TelemetrySignal.PackCurrent] = CadenceTier.High,
            [TelemetrySignal.VehicleSpeed] = CadenceTier.High,
            [TelemetrySignal.RemainingRange] = CadenceTier.Medium,
            [TelemetrySignal.CabinTemperature] = CadenceTier.Medium,
            [TelemetrySignal.CellVoltages] = CadenceTier.Low,
        });
        await using var telemetry = TelemetrySession.Create(commands, subscription, options);

        // --- Pre-check: standstill snapshot with VIN ---
        var pre = await telemetry.GetSnapshotAsync(token);
        await Assert.That(pre.Vin).IsEqualTo(SimulatedLeafAze0Transport.SimulatedVin);
        await Assert.That(pre.SocPercent).IsNotNull();
        await Assert.That(pre.SocPercent!.Value).IsGreaterThan(80m);
        await Assert.That(pre.PackVoltageV).IsNotNull();
        await Assert.That(pre.CellVoltagesV!.Count).IsEqualTo(96);
        await Assert.That(pre.PackTemperatureC).IsNotNull();
        await Assert.That(pre.StateOfHealthPercent).IsNotNull();

        // --- Live drive: collect batches until speed shows movement and SOC streams ---
        await telemetry.StartAsync(token);
        var sawMovingSpeed = false;
        var sawSoc = false;
        var sawRange = false;
        using var driveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        driveCts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await foreach (var batch in telemetry.Batches(driveCts.Token))
            {
                foreach (var sample in batch.Samples.Where(s => !s.Value.IsEmpty))
                {
                    switch (sample.Signal)
                    {
                        case TelemetrySignal.VehicleSpeed when sample.Value.Scalar > 5m:
                            sawMovingSpeed = true;
                            break;
                        case TelemetrySignal.StateOfCharge:
                            sawSoc = true;
                            break;
                        case TelemetrySignal.RemainingRange:
                            sawRange = true;
                            break;
                    }
                }

                if (sawMovingSpeed && sawSoc && sawRange)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Drive window elapsed — the assert below reports which signals were missing.
        }

        // If the 20 s drive window elapsed without all three signals, fail with detail
        // instead of hanging into the test timeout.
        await Assert.That(
                new { sawMovingSpeed, sawSoc, sawRange })
            .IsEquivalentTo(new { sawMovingSpeed = true, sawSoc = true, sawRange = true });

        await telemetry.StopAsync(token);

        // --- Post-check: SOC drained, pack warmed, range shrank ---
        var post = await telemetry.GetSnapshotAsync(token);
        await Assert.That(post.SocPercent!.Value).IsLessThan(pre.SocPercent.Value - 0.5m);
        await Assert.That(post.PackTemperatureC!.Value).IsGreaterThan(pre.PackTemperatureC!.Value);
        await Assert.That(post.RemainingRangeKm!.Value).IsLessThan(pre.SocPercent.Value * 1.6m);
        await Assert.That(post.Vin).IsEqualTo(pre.Vin);

        await commands.Monitor.StopAsync(token);
    }
}
