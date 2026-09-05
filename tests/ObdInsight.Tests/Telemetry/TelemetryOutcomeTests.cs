using ObdInsight.Telemetry;

namespace ObdInsight.Tests.Telemetry;

[Timeout(15_000)]
public class TelemetryOutcomeTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TelemetrySession Create(ITelemetryProvider provider) => new([provider],
        new TelemetrySubscription(new Dictionary<TelemetrySignal, CadenceTier>
        { [TelemetrySignal.StateOfCharge] = CadenceTier.High }),
        new TelemetrySessionOptions { HighPeriod = TimeSpan.FromMilliseconds(10) });

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProducerFailure_FaultsCompletionAndWaitingReaders(bool unexpectedCancellation, CancellationToken ct)
    {
        var release = Signal();
        Exception failure = unexpectedCancellation ? new OperationCanceledException("not our token") : new IOException("link ended");
        var provider = new Provider(async (call, _) =>
        {
            if (call == 1) return;
            await release.Task.WaitAsync(ct);
            throw failure;
        });
        await using var session = Create(provider);
        await using var reader = session.Batches(ct).GetAsyncEnumerator(ct);
        await session.StartAsync(ct);
        var completion = session.Completion;
        var pending = reader.MoveNextAsync().AsTask();
        release.SetResult();
        await ExpectSame(completion, failure);
        await ExpectSame(pending, failure);
        await Assert.That(completion.IsFaulted).IsTrue(); // Unexpected OCE is failure, not normal stop.
        await session.StopAsync(ct);
        await using var late = session.Batches(ct).GetAsyncEnumerator(ct);
        await ExpectSame(late.MoveNextAsync().AsTask(), failure);
    }

    [Test]
    public async Task CanceledStop_RetainsProducerUntilJoined_ThenAllowsRestart(CancellationToken ct)
    {
        var entered = Signal();
        var release = Signal();
        var provider = new Provider(async (call, _) =>
        {
            if (call == 2) { entered.SetResult(); await release.Task.WaitAsync(ct); }
        });
        await using var session = Create(provider);
        await session.StartAsync(ct);
        var first = session.Completion;
        await entered.Task.WaitAsync(ct);
        using var stop = new CancellationTokenSource();
        var stopping = session.StopAsync(stop.Token).AsTask();
        stop.Cancel();
        await Assert.That(async () => await stopping).Throws<OperationCanceledException>();
        await Assert.That(async () => await session.StartAsync(ct)).Throws<InvalidOperationException>();
        await Assert.That(first.IsCompleted).IsFalse();
        release.SetResult();
        await session.StopAsync(ct);
        await first;
        await session.StartAsync(ct);
        await Assert.That(ReferenceEquals(first, session.Completion)).IsFalse();
        await session.StopAsync(ct);
    }

    [Test]
    public async Task ConcurrentStarts_ShareProbe_AndStopCancelsStartup(CancellationToken ct)
    {
        var entered = Signal();
        var provider = new Provider(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var session = Create(provider);
        var first = session.StartAsync(ct).AsTask();
        await entered.Task.WaitAsync(ct);
        var second = session.StartAsync(ct).AsTask();
        await using var reader = session.Batches(ct).GetAsyncEnumerator(ct);
        var pending = reader.MoveNextAsync().AsTask();
        await session.StopAsync(ct);
        await Assert.That(async () => await first).Throws<OperationCanceledException>();
        await Assert.That(async () => await second).Throws<OperationCanceledException>();
        await Assert.That(await pending).IsFalse();
        await Assert.That(provider.Calls).IsEqualTo(1);
        await session.Completion;
    }

    [Test]
    public async Task CallbackFailure_IsIsolated_AndStopDrainsBufferedBatches(CancellationToken ct)
    {
        await using var session = Create(new Provider((_, _) => Task.CompletedTask));
        var delivered = Signal();
        session.BatchAvailable += (_, _) => throw new InvalidOperationException("UI callback failed");
        session.BatchAvailable += (_, _) => delivered.TrySetResult();
        await using var reader = session.Batches(ct).GetAsyncEnumerator(ct);
        await session.StartAsync(ct);
        await delivered.Task.WaitAsync(ct);
        await session.StopAsync(ct);
        await session.Completion;
        var count = 0;
        while (await reader.MoveNextAsync()) count++;
        await Assert.That(count).IsGreaterThan(0);
    }

    [Test]
    public async Task StartupFailure_CompletesStreams_AndDisposalPreventsRestart(CancellationToken ct)
    {
        var failure = new InvalidOperationException("provider bug");
        var session = Create(new Provider((_, _) => throw failure));
        await using var reader = session.Batches(ct).GetAsyncEnumerator(ct);
        await ExpectSame(session.StartAsync(ct).AsTask(), failure);
        await ExpectSame(session.Completion, failure);
        await ExpectSame(reader.MoveNextAsync().AsTask(), failure);
        await Task.WhenAll(session.DisposeAsync().AsTask(), session.DisposeAsync().AsTask());
        await Assert.That(async () => await session.StartAsync(ct)).Throws<ObjectDisposedException>();
        await Assert.That(async () => await session.GetSnapshotAsync(ct)).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task QueryTimeout_RemainsMissingData_NotTerminalFailure(CancellationToken ct)
    {
        await using var session = Create(new Provider((_, _) => throw new TimeoutException()));
        await using var reader = session.Batches(ct).GetAsyncEnumerator(ct);
        await session.StartAsync(ct);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Samples[0].Value.IsEmpty).IsTrue();
        await session.StopAsync(ct);
        await session.Completion;
    }

    private static async Task ExpectSame(Task task, Exception expected)
    {
        Exception? actual = null;
        try { await task; } catch (Exception ex) { actual = ex; }
        await Assert.That(ReferenceEquals(actual, expected)).IsTrue();
    }

    [Test]
    public async Task Dispose_JoinsSnapshot_AndRejectsNewWork(CancellationToken ct)
    {
        var entered = Signal();
        var release = Signal();
        var provider = new Provider(async (_, _) => { entered.SetResult(); await release.Task.WaitAsync(ct); });
        var session = Create(provider);
        var snapshot = session.GetSnapshotAsync(ct).AsTask();
        await entered.Task.WaitAsync(ct);
        var disposal = session.DisposeAsync().AsTask();
        await Assert.That(disposal.IsCompleted).IsFalse();
        await Assert.That(async () => await session.GetSnapshotAsync(ct)).Throws<ObjectDisposedException>();
        release.SetResult();
        await snapshot;
        await disposal;
        await session.DisposeAsync();
    }

    [Test]
    public async Task InitiatingCancellation_EndsProbeAndStreams_ButNotASubsequentRun(CancellationToken ct)
    {
        var entered = Signal();
        var provider = new Provider(async (call, token) =>
        {
            if (call != 1) return;
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        await using var session = Create(provider);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var reader = session.Batches(ct).GetAsyncEnumerator(ct);
        var starting = session.StartAsync(caller.Token).AsTask();
        await entered.Task.WaitAsync(ct);
        caller.Cancel();
        await Assert.That(async () => await starting).Throws<OperationCanceledException>();
        await session.Completion.WaitAsync(ct);
        await Assert.That(await reader.MoveNextAsync()).IsFalse();
        await session.StartAsync(ct); // Completion itself is a valid restart boundary.
        await using var second = session.Batches(ct).GetAsyncEnumerator(ct);
        await Assert.That(await second.MoveNextAsync()).IsTrue();
        await session.StopAsync(ct);
    }

    [Test]
    public async Task StartToken_AfterSuccessfulProbe_DoesNotOwnRun(CancellationToken ct)
    {
        await using var session = Create(new Provider((_, _) => Task.CompletedTask));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await session.StartAsync(caller.Token);
        caller.Cancel();
        await using var reader = session.Batches(ct).GetAsyncEnumerator(ct);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(session.Completion.IsCompleted).IsFalse();
        await session.StopAsync(ct);
    }

    private sealed class Provider(Func<int, CancellationToken, Task> read) : ITelemetryProvider
    {
        public int Calls;
        public IReadOnlyCollection<TelemetrySignal> Signals => [TelemetrySignal.StateOfCharge];
        public bool IsCacheOnly => false;
        public async ValueTask<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>> ReadAsync(
            IReadOnlySet<TelemetrySignal> requested, CancellationToken ct)
        {
            await read(Interlocked.Increment(ref Calls), ct);
            return new Dictionary<TelemetrySignal, TelemetryValue> { [TelemetrySignal.StateOfCharge] = new(50m) };
        }
    }
}
