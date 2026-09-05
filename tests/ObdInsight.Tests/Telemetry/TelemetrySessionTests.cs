using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;
using ObdInsight.Telemetry;

namespace ObdInsight.Tests.Telemetry;

/// <summary>
///     Roadmap B1 acceptance: a three-tier telemetry session over scripted Leaf data
///     end-to-end — cache-served broadcast signals and UDS signals interleave while the
///     shared monitor keeps running, and the consumer only ever touches
///     <see cref="ITelemetrySession" /> (never ElmSession/CanMonitor).
/// </summary>
[Timeout(30_000)]
public class TelemetrySessionTests
{
    private static readonly TelemetrySessionOptions FastOptions = new()
    {
        HighPeriod = TimeSpan.FromMilliseconds(100),
        MediumPeriod = TimeSpan.FromMilliseconds(250),
        LowPeriod = TimeSpan.FromMilliseconds(500),
        CacheReadTimeout = TimeSpan.FromMilliseconds(150)
    };

    private static readonly TelemetrySubscription TestSubscription = new(
        new Dictionary<TelemetrySignal, CadenceTier>
        {
            [TelemetrySignal.StateOfCharge] = CadenceTier.High,
            [TelemetrySignal.PackVoltage] = CadenceTier.High,
            [TelemetrySignal.VehicleSpeed] = CadenceTier.High,
            [TelemetrySignal.CabinTemperature] = CadenceTier.Medium,
            [TelemetrySignal.RemainingRange] = CadenceTier.Medium,
            [TelemetrySignal.CellVoltages] = CadenceTier.Low,
            [TelemetrySignal.Odometer] = CadenceTier.Low // no provider — must degrade, not throw
        });

    [Test]
    public async Task ThreeTierSession_InterleavesCacheAndUdsSignals(CancellationToken token)
    {
        var (transport, commands) = Setup();
        await using var session = TelemetrySession.Create(commands, TestSubscription, FastOptions);

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pump = PumpBroadcastFramesAsync(transport, commands, pumpCts.Token);

        await session.StartAsync(token);

        // Collect until every tier has delivered its expected values (or the test times out).
        TelemetrySample? soc = null, voltage = null, speed = null, cabin = null, range = null, cells = null;
        await foreach (var batch in session.Batches(token))
        {
            foreach (var sample in batch.Samples.Where(s => !s.Value.IsEmpty))
            {
                switch (sample.Signal)
                {
                    case TelemetrySignal.StateOfCharge: soc = sample; break;
                    case TelemetrySignal.PackVoltage: voltage = sample; break;
                    case TelemetrySignal.VehicleSpeed: speed = sample; break;
                    case TelemetrySignal.CabinTemperature: cabin = sample; break;
                    case TelemetrySignal.RemainingRange: range = sample; break;
                    case TelemetrySignal.CellVoltages: cells = sample; break;
                }
            }

            if (soc is not null && voltage is not null && speed is not null &&
                cabin is not null && range is not null && cells is not null)
            {
                break;
            }
        }

        pumpCts.Cancel();
        try { await pump; }
        catch (OperationCanceledException) { }

        // UDS-sourced (BMS Group 01, golden capture), decimal-normalized:
        await Assert.That(soc!.Value.Scalar!.Value).IsEqualTo(41.921m);
        await Assert.That(voltage!.Value.Scalar!.Value).IsEqualTo(361.78m);
        await Assert.That(soc.Tier).IsEqualTo(CadenceTier.High);

        // Cache-sourced, same session, monitor never stopped:
        await Assert.That(speed!.Value.Scalar!.Value).IsEqualTo(25.6m); // 0x284 bytes 4-5 ×0.01
        await Assert.That(cabin!.Value.Scalar!.Value).IsEqualTo(-14.0m); // 0x54F zeros
        await Assert.That(range!.Value.Scalar!.Value).IsEqualTo(179.2m); // 0x5A9 capture
        await Assert.That(cabin.Tier).IsEqualTo(CadenceTier.Medium);

        // Full cell set normalized mV → V:
        await Assert.That(cells!.Value.Vector!.Count).IsEqualTo(96);
        await Assert.That(cells.Value.Vector.All(v => v is > 3.5m and < 4.5m)).IsTrue();
        await Assert.That(cells.Tier).IsEqualTo(CadenceTier.Low);

        await Assert.That(commands.Monitor.IsRunning).IsTrue();

        // Availability: served signals Available; provider-less Odometer Unavailable.
        var availability = session.Availability;
        await Assert.That(availability[TelemetrySignal.StateOfCharge]).IsEqualTo(SignalAvailability.Available);
        await Assert.That(availability[TelemetrySignal.VehicleSpeed]).IsEqualTo(SignalAvailability.Available);
        await Assert.That(availability[TelemetrySignal.Odometer]).IsEqualTo(SignalAvailability.Unavailable);

        await session.StopAsync(token);
        await commands.Monitor.StopAsync(token);
    }

    [Test]
    public async Task Snapshot_ReturnsNormalizedPreCheckShape(CancellationToken token)
    {
        var (transport, commands) = Setup();
        transport.AutoRespond("2181", LeafGoldenData.GoldenVinLines.AsElmResponse());
        transport.AutoRespond("03", "NO DATA\r\r>");
        transport.AutoRespond("07", "NO DATA\r\r>");
        await using var session = TelemetrySession.Create(commands, TestSubscription, FastOptions);

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pump = PumpBroadcastFramesAsync(transport, commands, pumpCts.Token);

        var snapshot = await session.GetSnapshotAsync(token);

        pumpCts.Cancel();
        try { await pump; }
        catch (OperationCanceledException) { }

        await Assert.That(snapshot.Vin).IsEqualTo("1N4AZ0CP7HC000001");
        await Assert.That(snapshot.SocPercent!.Value).IsEqualTo(41.921m);
        await Assert.That(snapshot.PackVoltageV!.Value).IsEqualTo(361.78m);
        await Assert.That(snapshot.CellVoltagesV!.Count).IsEqualTo(96);
        await Assert.That(snapshot.CellVoltageMinV).IsNotNull();
        await Assert.That(snapshot.StateOfHealthPercent).IsNull(); // Group 01 Hx is not SOH.
        // Pack power sign: golden capture has small positive current (discharge) → positive kW.
        await Assert.That(snapshot.PackPowerKw!.Value).IsGreaterThan(0m);
        await Assert.That(snapshot.PackPowerKw.Value).IsLessThan(1m);
        // Scripted NO DATA reads must retain failure, never a clean code list.
        await Assert.That(snapshot.DiagnosticTroubleCodes).IsNotNull();
        await Assert.That(snapshot.DiagnosticTroubleCodes!.Stored.Codes).IsNull();
        await Assert.That(snapshot.DiagnosticTroubleCodes.Stored.Status).IsNotEqualTo(DtcReadStatus.Succeeded);
        await Assert.That(snapshot.OdometerKm).IsNull();

        await commands.Monitor.StopAsync(token);
    }

    [Test]
    public async Task AbsentBroadcastSignals_DegradeToEmptySamples(CancellationToken token)
    {
        // No broadcast frames pumped at all: cache signals must come back empty within the
        // cache-read bound — no stall, no throw — while UDS signals still work.
        var (_, commands) = Setup();
        await using var session = TelemetrySession.Create(commands, TestSubscription, FastOptions);

        await session.StartAsync(token);

        await foreach (var batch in session.Batches(token))
        {
            if (batch.Tier != CadenceTier.High)
            {
                continue;
            }

            var bySignal = batch.Samples.ToDictionary(s => s.Signal);
            await Assert.That(bySignal[TelemetrySignal.StateOfCharge].Value.IsEmpty).IsFalse();
            await Assert.That(bySignal[TelemetrySignal.VehicleSpeed].Value.IsEmpty).IsTrue();
            break;
        }

        var availability = session.Availability;
        await Assert.That(availability[TelemetrySignal.StateOfCharge]).IsEqualTo(SignalAvailability.Available);
        // Broadcast signal with no data stays Unknown (may warm up while driving), not Unavailable.
        await Assert.That(availability[TelemetrySignal.VehicleSpeed]).IsEqualTo(SignalAvailability.Unknown);

        await session.StopAsync(token);
        await commands.Monitor.StopAsync(token);
    }

    [Test]
    public async Task TypedStream_YieldsSignalValuesAtTheirOwnType(CancellationToken token)
    {
        var (transport, commands) = Setup();
        await using var session = TelemetrySession.Create(commands, TestSubscription, FastOptions);

        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pump = PumpBroadcastFramesAsync(transport, commands, pumpCts.Token);

        // Both streams are created before the session starts: registration is eager, so no
        // tick produced in between is lost.
        var socStream = session.Stream(Signals.StateOfCharge, token);
        var cellStream = session.Stream(Signals.CellVoltages, token);

        await session.StartAsync(token);

        await using var socSamples = socStream.GetAsyncEnumerator(token);
        await Assert.That(await socSamples.MoveNextAsync()).IsTrue();
        var soc = socSamples.Current;

        await using var cellSamples = cellStream.GetAsyncEnumerator(token);
        await Assert.That(await cellSamples.MoveNextAsync()).IsTrue();
        var cells = cellSamples.Current;

        pumpCts.Cancel();
        try { await pump; }
        catch (OperationCanceledException) { }

        // decimal, not TelemetryValue: no Scalar/Vector/Boolean unpacking at the call site.
        var socPercent = soc.Value;
        await Assert.That(socPercent).IsEqualTo(41.921m);
        await Assert.That(soc.Signal).IsEqualTo(TelemetrySignal.StateOfCharge);
        await Assert.That(soc.Tier).IsEqualTo(CadenceTier.High);

        var cellVoltages = cells.Value;
        await Assert.That(cellVoltages.Count).IsEqualTo(96);
        await Assert.That(cells.Tier).IsEqualTo(CadenceTier.Low);

        await session.StopAsync(token);
        await commands.Monitor.StopAsync(token);
    }

    [Test]
    public async Task TypedStream_SkipsTicksWhereTheSignalHasNoValue(CancellationToken token)
    {
        // No broadcast frames pumped: VehicleSpeed is empty on every tick, so its typed
        // stream stays silent while the UDS-sourced SOC stream still produces values.
        var (_, commands) = Setup();
        await using var session = TelemetrySession.Create(commands, TestSubscription, FastOptions);

        var speedStream = session.Stream(Signals.VehicleSpeed, token);
        var socStream = session.Stream(Signals.StateOfCharge, token);

        await session.StartAsync(token);

        await using var speedSamples = speedStream.GetAsyncEnumerator(token);
        var speedPending = speedSamples.MoveNextAsync();

        // Drain a few SOC samples: enough ticks have run that an empty VehicleSpeed would
        // have surfaced by now if empties were emitted.
        await using var socSamples = socStream.GetAsyncEnumerator(token);
        for (var i = 0; i < 3; i++)
        {
            await Assert.That(await socSamples.MoveNextAsync()).IsTrue();
            await Assert.That(socSamples.Current.Value).IsEqualTo(41.921m);
        }

        await Assert.That(speedPending.IsCompleted).IsFalse();
        await Assert.That(session.Availability[TelemetrySignal.VehicleSpeed])
            .IsEqualTo(SignalAvailability.Unknown);

        await session.StopAsync(token);
        await commands.Monitor.StopAsync(token);
    }

    [Test]
    public async Task Batches_RegisterEagerly_FirstBatchIsNotMissed(CancellationToken token)
    {
        var (_, commands) = Setup();
        await using var session = TelemetrySession.Create(commands, TestSubscription, FastOptions);

        var firstPublished = new TaskCompletionSource<TelemetrySampleBatch>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.BatchAvailable += (_, batch) => firstPublished.TrySetResult(batch);

        // Subscribe before the scheduler runs, but do not start iterating yet.
        var batches = session.Batches(token);

        await session.StartAsync(token);
        var published = await firstPublished.Task.WaitAsync(token);

        // Iteration starts only now — the very first published batch must still be waiting in
        // this subscriber's buffer. With registration deferred to the first MoveNext it would
        // have been dropped and this would hand back a later batch.
        await using var enumerator = batches.GetAsyncEnumerator(token);
        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current).IsSameReferenceAs(published);

        await session.StopAsync(token);
        await commands.Monitor.StopAsync(token);
    }

    private static (ReplayElmTransport transport, LeafAze0CommandSet commands) Setup()
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var commands = new LeafAze0CommandSet(session);
        commands.Monitor.RestartDelay = TimeSpan.Zero;

        transport.AutoRespond("ATMA", "");
        transport.AutoRespond("2101", LeafGoldenData.GoldenGroup01Lines.AsElmResponse());
        transport.AutoRespond("2102", LeafGoldenData.GoldenGroup02Lines.AsElmResponse());
        transport.AutoRespond("2104", LeafGoldenData.GoldenGroup04Lines.AsElmResponse());
        transport.AutoRespond("2106", LeafGoldenData.GoldenGroup06Lines.AsElmResponse());

        return (transport, commands);
    }

    /// <summary>
    ///     Continuously re-enqueues broadcast frames (a rotation window transition's buffer
    ///     clear can eat frames that land during an Enter sequence — same pattern as
    ///     LeafAze0StreamingCommandSetTests).
    /// </summary>
    private static async Task PumpBroadcastFramesAsync(
        ReplayElmTransport transport, LeafAze0CommandSet commands, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (commands.Monitor.IsRunning)
            {
                transport.EnqueueIncoming("130 00 00 00 00 00 00 00 00\r");
                transport.EnqueueIncoming("284 00 00 00 00 0A 00 76 FC\r"); // 25.6 km/h
                transport.EnqueueIncoming("285 00 00 00 00 00 00 00 00\r");
                transport.EnqueueIncoming("354 00 00 00 00 00 08 00 00\r");
                transport.EnqueueIncoming("54A 00 00 00 00 00 00 00 00\r");
                transport.EnqueueIncoming("54B 00 00 00 00 00 00 00 00\r");
                transport.EnqueueIncoming("54C 00 00 00 00 00 00 96 00\r"); // ambient raw 150 = 35.0 °C
                transport.EnqueueIncoming("54F 00 00 00 00 00 00 00 00\r"); // intake raw 0 = −14.0 °C
                transport.EnqueueIncoming("510 00 00 00 00 00 00 00 00\r");
                transport.EnqueueIncoming("5A9 85 26 C0 11 04 10 00 00\r"); // 179.2 km
            }

            await Task.Delay(20, ct);
        }
    }
}
