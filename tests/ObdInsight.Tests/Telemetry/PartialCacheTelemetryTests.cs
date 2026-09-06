using ObdInsight.Core.Communication.Slcan;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;
using ObdInsight.Telemetry;

namespace ObdInsight.Tests.Telemetry;

[Timeout(15_000)]
public class PartialCacheTelemetryTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Reads_ReturnAvailableEvidenceWithoutWaitingForAbsentFrames(bool partial, CancellationToken ct)
    {
        await using var transport = new ReplayElmTransport();
        foreach (var command in new[] { "C", "S6", "L" }) transport.AutoRespond(command, "\r");
        transport.AutoRespond("V", "V1013\r");
        await transport.OpenAsync(ct);
        await using var source = new SlcanFrameSource(transport);
        await using var commands = new LeafAze0CommandSet(source);
        await commands.Monitor.StartAsync(ct);
        if (partial)
        {
            transport.EnqueueIncoming("t2848000000000A0076FC\rt54A80000000000000000\rt54B80000000000000000\rt5A988526C01104100000\r");
            while (!commands.Monitor.TryGetLatest(0x5A9, out _)) await Task.Delay(5, ct);
        }
        commands.TryGet<IAntilockBrakingSystem>(out var abs);
        commands.TryGet<IHvac>(out var hvac);
        commands.TryGet<IVcm>(out var vcm);
        for (var i = 0; i < 3; i++)
        {
            // No 130, 54C, 54F or 510: no asynchronous cache wait on a running monitor.
            var speedRead = abs.GetStatusAsync(ct);
            var hvacRead = hvac.GetStatusAsync(ct);
            var rangeRead = vcm.GetStatusAsync(ct);
            await Assert.That(speedRead.IsCompletedSuccessfully).IsTrue();
            await Assert.That(hvacRead.IsCompletedSuccessfully).IsTrue();
            await Assert.That(rangeRead.IsCompletedSuccessfully).IsTrue();
            await Assert.That((await speedRead).VehicleSpeedKmh.HasValue).IsEqualTo(partial);
            await Assert.That((await rangeRead).RangeKm.HasValue).IsEqualTo(partial);
            await Assert.That((await hvacRead).CabinTemperatureObservation.Quality).IsEqualTo(ObservationQuality.Missing);
        }
        await using var telemetry = TelemetrySession.Create(commands, options: new()
        { HighPeriod = TimeSpan.FromMilliseconds(50), MediumPeriod = TimeSpan.FromMilliseconds(50) });
        await telemetry.StartAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await using var reader = telemetry.Batches(timeout.Token).GetAsyncEnumerator();
        var speedSamples = 0;
        while (speedSamples < 5)
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            foreach (var sample in reader.Current.Samples)
            {
                if (sample.Signal != TelemetrySignal.VehicleSpeed) continue;
                await Assert.That(sample.Value.IsEmpty).IsEqualTo(!partial);
                await Assert.That(sample.Value.Observation.Quality).IsEqualTo(partial ? ObservationQuality.Valid : ObservationQuality.Missing);
                speedSamples++;
            }
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(async () => await abs.GetStatusAsync(cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await hvac.GetStatusAsync(cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await vcm.GetStatusAsync(cancelled.Token)).Throws<OperationCanceledException>();
    }
}
