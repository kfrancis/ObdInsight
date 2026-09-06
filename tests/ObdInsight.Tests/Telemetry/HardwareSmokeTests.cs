using System.Text.Json;
using ObdInsight.Application;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.Telemetry;

[Timeout(120_000)]
public class HardwareSmokeTests
{
    private static SmokeOptions Options(string mode = "simulation") =>
        new(mode, null, null, 500, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(90), "unused");

    [Test]
    [Arguments("--tx")]
    [Arguments("--duration=0")]
    [Arguments("--duration=abc")]
    [Arguments("--device=AA:BB:CC:DD:EE:FF")]
    [Arguments("--smoke=slcan")]
    public async Task InvalidOptionsAreRejected(string argument, CancellationToken ct)
    {
        await Assert.That(() => SmokeOptions.Parse(["--smoke=simulation", argument])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Simulation_RecordsEvidenceWithoutIdentifiers_AndDisposes(CancellationToken ct)
    {
        var transport = new TrackedTransport();
        var output = new StringWriter();
        var code = await new HardwareSmokeRunner(output).RunAsync(Options(), () => transport, ct);
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(transport.Disposals).IsEqualTo(1);
        var text = output.ToString();
        await Assert.That(text).DoesNotContain(SimulatedLeafAze0Transport.SimulatedVin);
        await Assert.That(text).Contains("transport-open");
        await Assert.That(text).Contains("elm-initialize");
        await Assert.That(text).Contains("vehicle-detect");
        var events = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var stages = events.Select(e => e.RootElement.GetProperty("Stage").GetString()).ToArray();
            await Assert.That(stages).Contains("pre-snapshot");
            await Assert.That(stages).Contains("batch");
            await Assert.That(stages).Contains("post-snapshot");
            await Assert.That(stages[^2]).IsEqualTo("shutdown-complete");
            await Assert.That(text).Contains("ObservedAtUtc");
            await Assert.That(text).Contains("Freshness");
        }
        finally { foreach (var item in events) item.Dispose(); }
    }

    [Test]
    public async Task CancellationDuringOpen_JoinsOwner(CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var transport = new TrackedTransport { BlockOpen = true };
        var output = new StringWriter();
        var run = new HardwareSmokeRunner(output).RunAsync(Options(), () => transport, cancel.Token);
        await transport.Opened.Task.WaitAsync(ct);
        await cancel.CancelAsync();
        await Assert.That(await run).IsEqualTo(1);
        await Assert.That(transport.Disposals).IsEqualTo(1);
        await Assert.That(output.ToString()).Contains("cancelled");
        await Assert.That(output.ToString()).DoesNotContain("shutdown-complete");
    }

    [Test]
    public async Task OutputFailureDuringSnapshot_StillDisposesOwner(CancellationToken ct)
    {
        var transport = new TrackedTransport();
        var runner = new HardwareSmokeRunner(new FailingWriter());
        try { await runner.RunAsync(Options(), () => transport, ct); }
        catch (SmokeOutputException) { }
        await Assert.That(transport.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task DeadlineDuringStartup_DisposesEveryCreatedTransport(CancellationToken ct)
    {
        var transport = new TrackedTransport { BlockOpen = true };
        var output = new StringWriter();
        var created = 0;
        var code = await new HardwareSmokeRunner(output).RunAsync(
            Options() with { Timeout = TimeSpan.FromMilliseconds(100) }, () => { created++; return transport; }, ct);
        await Assert.That(code).IsEqualTo(1);
        await Assert.That(transport.Disposals).IsEqualTo(created);
        await Assert.That(output.ToString()).Contains("deadline");
    }

    [Test]
    public async Task CancellationWhileRecording_DoesNotAttemptPostSnapshot(CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var transport = new TrackedTransport();
        var output = new TriggerWriter(line => { if (line.Contains("\"Stage\":\"batch\"")) cancel.Cancel(); });
        var code = await new HardwareSmokeRunner(output).RunAsync(Options(), () => transport, cancel.Token);
        await Assert.That(code).IsEqualTo(1);
        await Assert.That(transport.Disposals).IsEqualTo(1);
        await Assert.That(output.ToString()).DoesNotContain("post-snapshot");
    }

    [Test]
    public async Task ExistingOutputIsNeverOverwritten(CancellationToken ct)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "retain me", ct);
            var code = await HardwareSmokeCommand.RunAsync(["--smoke=simulation", $"--output={path}"]);
            await Assert.That(code).IsEqualTo(1);
            await Assert.That(await File.ReadAllTextAsync(path, ct)).IsEqualTo("retain me");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task Reconnect_RecordsNewGeneration_WithoutReplayingPreSnapshot(CancellationToken ct)
    {
        var first = new TrackedTransport();
        var second = new TrackedTransport();
        var calls = 0;
        var output = new TriggerWriter(line =>
        {
            if (line.Contains("\"Stage\":\"batch\"") && calls == 1) first.Lose();
        });
        var options = Options() with { Duration = TimeSpan.FromSeconds(5) };
        var code = await new HardwareSmokeRunner(output).RunAsync(options,
            () => Interlocked.Increment(ref calls) == 1 ? first : second, ct);
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(calls).IsEqualTo(2);
        await Assert.That(first.Disposals).IsEqualTo(1);
        await Assert.That(second.Disposals).IsEqualTo(1);
        await Assert.That(output.ToString()).Contains("\"Generation\":2");
        await Assert.That(output.ToString().Split("\"Stage\":\"pre-snapshot\"").Length).IsEqualTo(2);
    }

    [Test]
    public async Task Slcan_CancelledQuietCapture_OnlyUsesListenOnlyCommands(CancellationToken ct)
    {
        var replay = new ReplayElmTransport { AutoRespondToAtCommands = false };
        foreach (var command in new[] { "C", "S6", "M1", "O" }) replay.AutoRespond(command, "\r");
        replay.AutoRespond("V", "secret-id github.com/normaldotcom/canable2.git\r");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var output = new TriggerWriter(line => { if (line.Contains("slcan-ready")) cancel.Cancel(); });
        var code = await new HardwareSmokeRunner(output).RunAsync(Options("slcan"), () => replay, cancel.Token);
        await Assert.That(code).IsEqualTo(1);
        await Assert.That(replay.IsOpen).IsFalse();
        await Assert.That(replay.SentCommands).IsEquivalentTo(new[] { "C", "V", "S6", "M1", "O", "C" });
        await Assert.That(output.ToString()).DoesNotContain("secret-id");
    }

    [Test]
    public async Task WindowsBle_QuietReadWaitsUntilCancellation_NotFalseEof(CancellationToken ct)
    {
        await using var transport = new ObdInsight.Transports.WindowsBle.BleElmTransport("00:00:00:00:00:00");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var read = transport.ReadAsync(new byte[16], cancel.Token).AsTask();
        await Task.Delay(350, ct); // Regression: the previous implementation returned zero at 250 ms.
        await Assert.That(read.IsCompleted).IsFalse();
        await cancel.CancelAsync();
        await Assert.That(async () => await read).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task WindowsBle_DisposalUnblocksPendingRead(CancellationToken ct)
    {
        var transport = new ObdInsight.Transports.WindowsBle.BleElmTransport("00:00:00:00:00:00");
        var read = transport.ReadAsync(new byte[16], ct).AsTask();
        await transport.DisposeAsync();
        await Assert.That(async () => await read).Throws<IOException>();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Slcan_FullCaptureOrQuietOutcome_NoActiveDiagnostics(bool feedFrames, CancellationToken ct)
    {
        var replay = new ReplayElmTransport { AutoRespondToAtCommands = false };
        foreach (var command in new[] { "C", "S6", "L" }) replay.AutoRespond(command, "\r");
        replay.AutoRespond("V", "V1013\r");
        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync();
        var output = new StringWriter();
        try
        {
            // Even partial/empty caches must not impose warmup waits on every poll.
            var code = await new HardwareSmokeRunner(output).RunAsync(Options("slcan") with { Duration = TimeSpan.FromSeconds(3) }, () => replay, ct);
            await Assert.That(code).IsEqualTo(feedFrames ? 0 : 2);
            await Assert.That(output.ToString()).Contains("post-snapshot");
            await Assert.That(output.ToString()).Contains("shutdown-complete");
            await Assert.That(output.ToString()).Contains("frame-coverage");
            await Assert.That(replay.IsOpen).IsFalse();
            await Assert.That(replay.SentCommands).IsEquivalentTo(new[] { "C", "V", "S6", "L", "C" });
            if (feedFrames) await Assert.That(output.ToString()).Contains("\"644\":");
        }
        finally
        {
            await pumpCancellation.CancelAsync();
            try { await pump; } catch (OperationCanceledException) when (pumpCancellation.IsCancellationRequested) { }
        }

        async Task PumpAsync()
        {
            while (!replay.SentCommands.Contains("L")) await Task.Delay(10, pumpCancellation.Token);
            while (feedFrames && replay.IsOpen)
            {
                replay.EnqueueIncoming("malformed\rt2848000000000A0076FC\r");
                await Task.Delay(20, pumpCancellation.Token);
            }
        }
    }

    private sealed class FailingWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => value?.Contains("pre-snapshot") == true || value?.Contains("failed") == true
            ? Task.FromException(new IOException("secret identifier")) : base.WriteLineAsync(value);
    }

    private sealed class TriggerWriter(Action<string> onLine) : StringWriter
    {
        public override async Task WriteLineAsync(string? value)
        {
            await base.WriteLineAsync(value);
            onLine(value!);
        }
    }

    private sealed class TrackedTransport : IConnectionAwareTransport
    {
        private readonly SimulatedLeafAze0Transport _inner = new();
        public bool BlockOpen { get; init; }
        public int Disposals { get; private set; }
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler? ConnectionLost;
        public void Lose() => ConnectionLost?.Invoke(this, EventArgs.Empty);
        public bool IsOpen => _inner.IsOpen;
        public async ValueTask OpenAsync(CancellationToken ct)
        {
            Opened.TrySetResult();
            if (BlockOpen) await Task.Delay(Timeout.Infinite, ct);
            await _inner.OpenAsync(ct);
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _inner.ReadAsync(buffer, ct);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => _inner.WriteAsync(data, ct);
        public ValueTask FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public void ClearBuffer() => _inner.ClearBuffer();
        public async ValueTask DisposeAsync() { Disposals++; await _inner.DisposeAsync(); }
    }
}
