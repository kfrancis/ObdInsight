using System.Text;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;
using ObdInsight.Telemetry;

namespace ObdInsight.Tests.Telemetry;

[Timeout(30_000)]
public class VehicleConnectionTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static VehicleConnection Create(Func<IElmTransport> factory, int attempts = 2, TimeSpan? delay = null) =>
        new(factory, [new ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.NissanLeaf()],
            new VehicleConnectionOptions { MaxReconnectAttempts = attempts, RetryDelay = delay ?? TimeSpan.Zero },
            new TelemetrySubscription(new Dictionary<TelemetrySignal, CadenceTier>
            { [TelemetrySignal.StateOfCharge] = CadenceTier.High }),
            new TelemetrySessionOptions { HighPeriod = TimeSpan.FromMilliseconds(20) });

    [Test]
    public async Task Loss_ReinitializesFreshGeneration_EndsOldStream_AndRejectsOldCache(CancellationToken ct)
    {
        var first = new Transport();
        var second = new Transport();
        var calls = 0;
        await using var owner = Create(() => Interlocked.Increment(ref calls) == 1 ? first : second);
        var old = await owner.OpenAsync(ct);
        var monitor = ((LeafAze0CommandSet)old.Detection.Commands!).Monitor;
        await monitor.StartAsync(ct);
        while (!monitor.TryGetLatest(0x284, out _))
        {
            first.Replay.EnqueueIncoming("284 00 00 00 00 0A 00 76 FC\r");
            await Task.Delay(10, ct);
        }
        await old.Telemetry.StartAsync(ct);
        await using var reader = old.Telemetry.Batches(ct).GetAsyncEnumerator(ct);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        first.Lose();
        await Assert.That(await old.Ended.WaitAsync(ct)).IsTypeOf<IOException>();
        var fresh = await owner.WaitForReadyAsync(old.Number, ct);
        await Assert.That(fresh.Number).IsEqualTo(old.Number + 1);
        await Assert.That(ReferenceEquals(old.Detection.Commands, fresh.Detection.Commands)).IsFalse();
        await Assert.That(first.DisposeCount).IsEqualTo(1);
        await Assert.That(second.Replay.SentCommands).Contains("AT Z");
        await Assert.That(second.Replay.SentCommands).Contains("0100");
        await Assert.That(second.Replay.SentCommands).Contains("2181");
        await Assert.That(async () => await old.Telemetry.StartAsync(ct)).Throws<ObjectDisposedException>();
        await Assert.That(async () => await monitor.StartAsync(ct)).Throws<ObjectDisposedException>();
        await Assert.That(monitor.TryGetLatest(0x284, out _)).IsFalse();
        await Assert.That(async () => await old.Telemetry.Completion).Throws<IOException>();
        await Assert.That(async () => { while (await reader.MoveNextAsync()) { } }).Throws<IOException>();
        await fresh.Telemetry.StartAsync(ct);
        await using var next = fresh.Telemetry.Batches(ct).GetAsyncEnumerator(ct);
        await Assert.That(await next.MoveNextAsync()).IsTrue();
        // Old callbacks cannot invalidate the replacement.
        first.Lose();
        await Assert.That(fresh.Ended.IsCompleted).IsFalse();
    }

    [Test]
    public async Task DisposeDuringOpen_JoinsLateCandidate_NeverPublishesReady(CancellationToken ct)
    {
        var entered = Signal(); var release = Signal();
        var candidate = new Transport { Opening = async _ => { entered.SetResult(); await release.Task.WaitAsync(ct); } };
        var owner = Create(() => candidate);
        var ready = owner.OpenAsync(ct).AsTask();
        await entered.Task.WaitAsync(ct);
        var disposal = owner.DisposeAsync().AsTask();
        await Assert.That(disposal.IsCompleted).IsFalse();
        release.SetResult();
        await disposal.WaitAsync(ct);
        await Assert.That(async () => await ready).Throws<ObjectDisposedException>();
        await Assert.That(candidate.DisposeCount).IsEqualTo(1);
        await Assert.That(candidate.Replay.SentCommands.Count).IsEqualTo(0);
        await owner.DisposeAsync();
        await owner.Completion;
    }

    [Test]
    public async Task FailedCandidates_AreDisposed_AndExhaustionReleasesWaiters(CancellationToken ct)
    {
        var candidates = new List<Transport>();
        await using var owner = Create(() =>
        {
            var candidate = new Transport { Opening = _ => throw new IOException("open failed") };
            candidates.Add(candidate); return candidate;
        });
        await Assert.That(async () => await owner.OpenAsync(ct)).Throws<IOException>();
        await Assert.That(async () => await owner.Completion).Throws<IOException>();
        await Assert.That(candidates.Count).IsEqualTo(3); // initial + two retries
        await Assert.That(candidates.All(t => t.DisposeCount == 1)).IsTrue();
        await Assert.That(owner.State).IsEqualTo(ConnectionState.Lost);
    }

    [Test]
    public async Task ConcurrentOpen_IsSingleFlight_AndCanceledWaitDoesNotOwnRecovery(CancellationToken ct)
    {
        var entered = Signal(); var release = Signal(); var count = 0;
        await using var owner = Create(() =>
        {
            Interlocked.Increment(ref count);
            return new Transport { Opening = async token => { entered.SetResult(); await release.Task.WaitAsync(token); } };
        });
        using var caller = new CancellationTokenSource();
        var canceled = owner.OpenAsync(caller.Token).AsTask();
        await entered.Task.WaitAsync(ct);
        var other = owner.OpenAsync(ct).AsTask();
        caller.Cancel();
        await Assert.That(async () => await canceled).Throws<OperationCanceledException>();
        release.SetResult();
        var ready = await other;
        await Assert.That(ready.Number).IsEqualTo(1);
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task DisposeDuringBackoff_DoesNotOpenAnotherCandidate(CancellationToken ct)
    {
        var first = new Transport(); var count = 0;
        var reconnecting = Signal();
        var owner = Create(() => { Interlocked.Increment(ref count); return first; }, delay: TimeSpan.FromHours(1));
        owner.StateChanged += (_, e) => { if (e.NewState == ConnectionState.Reconnecting) reconnecting.TrySetResult(); };
        await owner.OpenAsync(ct);
        first.Lose();
        await reconnecting.Task.WaitAsync(ct);
        await owner.DisposeAsync().AsTask().WaitAsync(ct);
        await Assert.That(count).IsEqualTo(1);
        await Assert.That(first.DisposeCount).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UncertainWrite_IsNotReplayedOnReplacement(bool flush, CancellationToken ct)
    {
        var first = new Transport(); var second = new Transport(); var count = 0;
        await using var owner = Create(() => ++count == 1 ? first : second);
        var old = await owner.OpenAsync(ct);
        if (flush) first.FailFlush = true;
        else first.FailCommand = "2101";
        // Leaf currently degrades BMS I/O, but the physical generation still ends.
        try { await old.Telemetry.StartAsync(ct); } catch (Exception) { }
        await old.Ended.WaitAsync(ct);
        var fresh = await owner.WaitForReadyAsync(old.Number, ct);
        await Assert.That(first.UncertainWrites).IsEqualTo(1);
        await Assert.That(second.Replay.SentCommands.Contains("2101")).IsFalse();
        await Assert.That(fresh.Telemetry.Completion.IsCompleted).IsTrue(); // not auto-started/replayed
    }

    [Test]
    public async Task EofWithoutEvent_EndsGenerationAndRecovers(CancellationToken ct)
    {
        var first = new Transport(); var second = new Transport(); var count = 0;
        await using var owner = Create(() => ++count == 1 ? first : second);
        var old = await owner.OpenAsync(ct);
        first.Eof = true;
        try { await old.Telemetry.StartAsync(ct); } catch (Exception) { }
        await old.Ended.WaitAsync(ct);
        await owner.WaitForReadyAsync(old.Number, ct);
        await Assert.That(first.DisposeCount).IsEqualTo(1);
    }

    private sealed class Transport : IConnectionAwareTransport
    {
        public ReplayElmTransport Replay { get; } = new();
        public Func<CancellationToken, Task>? Opening { get; init; }
        public string? FailCommand;
        public bool Eof;
        public bool FailFlush;
        public int UncertainWrites;
        public int DisposeCount;
        public Transport()
        {
            Replay.AutoRespond("0100", "7E8064100BE3FA813\r>");
            Replay.AutoRespond("2181", LeafGoldenData.GoldenVinLines.AsElmResponse());
            Replay.AutoRespond("2101", LeafGoldenData.GoldenGroup01Lines.AsElmResponse());
            Replay.AutoRespond("2104", LeafGoldenData.GoldenGroup04Lines.AsElmResponse());
            Replay.AutoRespond("ATMA", "");
        }
        public event EventHandler? ConnectionLost;
        public bool IsOpen => Replay.IsOpen;
        public void Lose() { Replay.SimulateConnectionLost(); ConnectionLost?.Invoke(this, EventArgs.Empty); }
        public async ValueTask OpenAsync(CancellationToken ct)
        {
            if (Opening is not null) await Opening(ct);
            await Replay.OpenAsync(ct);
        }
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => Eof ? ValueTask.FromResult(0) : Replay.ReadAsync(buffer, ct);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            if (Encoding.ASCII.GetString(bytes.Span).Trim() == FailCommand)
            { UncertainWrites++; throw new IOException("write may have reached adapter"); }
            return Replay.WriteAsync(bytes, ct);
        }
        public ValueTask FlushAsync(CancellationToken ct)
        {
            if (FailFlush) { UncertainWrites++; throw new IOException("flush failed after write"); }
            return Replay.FlushAsync(ct);
        }
        public void ClearBuffer() => Replay.ClearBuffer();
        public ValueTask DisposeAsync() { DisposeCount++; return Replay.DisposeAsync(); }
    }

    [Test]
    public async Task InitializationFailure_DisposesCandidateBeforeReplacement(CancellationToken ct)
    {
        var first = new Transport { FailCommand = "AT Z" }; var second = new Transport(); var count = 0;
        await using var owner = Create(() => ++count == 1 ? first : second);
        var ready = await owner.OpenAsync(ct);
        await Assert.That(ready.Number).IsEqualTo(1);
        await Assert.That(first.DisposeCount).IsEqualTo(1);
        await Assert.That(first.UncertainWrites).IsEqualTo(1);
        await Assert.That(second.Replay.SentCommands).Contains("AT Z");
    }

    [Test]
    public async Task ChangedVehicle_IsNotAdoptedIntoExistingDrive(CancellationToken ct)
    {
        var first = new Transport(); var second = new Transport(); var count = 0;
        second.Replay.AutoRespond("2181", LeafGoldenData.GoldenVinLines.AsElmResponse().Replace("2230303030303100", "2230303030303200"));
        await using var owner = Create(() => ++count == 1 ? first : second);
        var old = await owner.OpenAsync(ct);
        first.Lose();
        await Assert.That(async () => await owner.WaitForReadyAsync(old.Number, ct)).Throws<IOException>();
        await Assert.That(async () => await owner.Completion).Throws<InvalidOperationException>();
        await Assert.That(second.DisposeCount).IsEqualTo(1);
        await Assert.That(count).IsEqualTo(2);
    }
}
