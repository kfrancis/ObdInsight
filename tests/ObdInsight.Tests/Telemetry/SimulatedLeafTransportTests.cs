using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;

namespace OdbTestApp.Tests.Telemetry;

/// <summary>
/// Regression tests for <see cref="SimulatedLeafAze0Transport"/> at the session level:
/// full init/protocol sequence, cold UDS queries, and UDS-under-running-monitor
/// (suspend/resume) — the building blocks the B2 drive harness composes.
/// </summary>
[Timeout(15_000)]
public class SimulatedLeafTransportTests
{
    [Test]
    public async Task Init_Works(CancellationToken token)
    {
        var transport = new SimulatedLeafAze0Transport(timeScale: 120);
        var session = new ElmSession(new ElmFramer(transport));
        await session.InitializeAndLockAsync(token);
    }

    [Test]
    public async Task BmsQuery_ColdMonitor_Works(CancellationToken token)
    {
        var transport = new SimulatedLeafAze0Transport(timeScale: 120);
        var session = new ElmSession(new ElmFramer(transport));
        await session.InitializeAndLockAsync(token);
        var commands = new LeafAze0CommandSet(session);
        commands.TryGet<IBatteryManagementSystem>(out var bms);
        var status = await bms.GetStatusAsync(token);
        await Assert.That(status.SocPercent).IsNotNull();
    }

    [Test]
    public async Task CellVoltages_ColdMonitor_Works(CancellationToken token)
    {
        var transport = new SimulatedLeafAze0Transport(timeScale: 120);
        var session = new ElmSession(new ElmFramer(transport));
        await session.InitializeAndLockAsync(token);
        var commands = new LeafAze0CommandSet(session);
        commands.TryGet<IBatteryManagementSystem>(out var bms);
        var cells = await bms.GetCellVoltagesAsync(token);
        await Assert.That(cells).IsNotNull();
        await Assert.That(cells!.CellVoltagesMv.Length).IsEqualTo(96);
    }

    [Test]
    public async Task Vin_ColdMonitor_Works(CancellationToken token)
    {
        var transport = new SimulatedLeafAze0Transport(timeScale: 120);
        var session = new ElmSession(new ElmFramer(transport));
        await session.InitializeAndLockAsync(token);
        var commands = new LeafAze0CommandSet(session);
        commands.TryGet<IVehicleIdentification>(out var ident);
        var vin = await ident.GetVinAsync(token);
        await Assert.That(vin).IsEqualTo(SimulatedLeafAze0Transport.SimulatedVin);
    }

    [Test]
    public async Task TelemetrySnapshot_Alone_Works(CancellationToken token)
    {
        var transport = new SimulatedLeafAze0Transport(timeScale: 120);
        var session = new ElmSession(new ElmFramer(transport));
        await session.InitializeAndLockAsync(token);
        var commands = new LeafAze0CommandSet(session);
        commands.Monitor.RestartDelay = TimeSpan.Zero;
        await using var telemetry = ObdInsight.Telemetry.TelemetrySession.Create(
            commands,
            options: new ObdInsight.Telemetry.TelemetrySessionOptions
            {
                CacheReadTimeout = TimeSpan.FromMilliseconds(500),
            });

        var snapshot = await telemetry.GetSnapshotAsync(token);
        await Assert.That(snapshot.SocPercent).IsNotNull();

        await commands.Monitor.StopAsync(token);
    }

    [Test]
    public async Task TelemetryScheduler_ProducesBatches(CancellationToken token)
    {
        var transport = new SimulatedLeafAze0Transport(timeScale: 120);
        var session = new ElmSession(new ElmFramer(transport));
        await session.InitializeAndLockAsync(token);
        var commands = new LeafAze0CommandSet(session);
        commands.Monitor.RestartDelay = TimeSpan.Zero;
        await using var telemetry = ObdInsight.Telemetry.TelemetrySession.Create(
            commands,
            new ObdInsight.Telemetry.TelemetrySubscription(
                new Dictionary<ObdInsight.Telemetry.TelemetrySignal, ObdInsight.Telemetry.CadenceTier>
                {
                    [ObdInsight.Telemetry.TelemetrySignal.StateOfCharge] = ObdInsight.Telemetry.CadenceTier.High,
                    [ObdInsight.Telemetry.TelemetrySignal.VehicleSpeed] = ObdInsight.Telemetry.CadenceTier.High,
                }),
            new ObdInsight.Telemetry.TelemetrySessionOptions
            {
                HighPeriod = TimeSpan.FromMilliseconds(200),
                CacheReadTimeout = TimeSpan.FromMilliseconds(500),
            });

        await telemetry.StartAsync(token);
        var count = 0;
        await foreach (var batch in telemetry.Batches(token))
        {
            if (batch.Samples.Any(s => s.Signal == ObdInsight.Telemetry.TelemetrySignal.StateOfCharge && !s.Value.IsEmpty))
            {
                count++;
            }

            if (count >= 3)
            {
                break;
            }
        }

        await telemetry.StopAsync(token);

        // Post-check after stopping mid-cadence: the suspend/resume machinery must not
        // be left wedged by the loop cancellation.
        var post = await telemetry.GetSnapshotAsync(token);
        await Assert.That(post.SocPercent).IsNotNull();

        await commands.Monitor.StopAsync(token);
    }

    [Test]
    public async Task BmsQuery_WithRunningMonitor_Works(CancellationToken token)
    {
        var transport = new SimulatedLeafAze0Transport(timeScale: 120);
        var session = new ElmSession(new ElmFramer(transport));
        await session.InitializeAndLockAsync(token);
        var commands = new LeafAze0CommandSet(session);
        commands.Monitor.RestartDelay = TimeSpan.Zero;
        await commands.Monitor.StartAsync(token);

        // Let broadcast data land, then do a UDS query through the suspend cycle.
        commands.TryGet<IAntilockBrakingSystem>(out var abs);
        var absStatus = await abs.GetStatusAsync(token);
        await Assert.That(absStatus.VehicleSpeedKmh).IsNotNull();

        commands.TryGet<IBatteryManagementSystem>(out var bms);
        var status = await bms.GetStatusAsync(token);
        await Assert.That(status.SocPercent).IsNotNull();

        // And again — resume must have restored monitoring cleanly.
        var status2 = await bms.GetStatusAsync(token);
        await Assert.That(status2.SocPercent).IsNotNull();

        await commands.Monitor.StopAsync(token);
    }
}
