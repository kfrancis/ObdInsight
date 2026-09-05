using ObdInsight.Core.Communication.Elm327;

namespace ObdInsight.Telemetry;

// One physical connection, never replaceable. No uncertain read/write/flush is replayed.
internal sealed class GenerationTransport : IElmTransport
{
    private readonly IElmTransport _inner;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _ended = new();
    private readonly TaskCompletionSource<Exception> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _cancellation;
    private Task? _dispose;
    private int _operations;

    public GenerationTransport(IElmTransport inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (inner is IConnectionAwareTransport aware) aware.ConnectionLost += OnLost;
    }

    public Task<Exception> Failure => _failure.Task;
    public bool IsOpen => !Failure.IsCompleted && _inner.IsOpen;
    private void OnLost(object? sender, EventArgs args) => Fail(new IOException("Physical connection lost."));

    public void Fail(Exception error)
    {
        lock (_gate)
        {
            if (!_failure.TrySetResult(error)) return;
            _cancellation = _ended.CancelAsync();
            if (_operations == 0) _drained.TrySetResult();
        }
    }

    public void ThrowIfEnded()
    {
        if (Failure.IsCompleted) throw new IOException("This diagnostic connection generation has ended.", Failure.Result);
    }

    private async ValueTask<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) { ThrowIfEnded(); _operations++; }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _ended.Token);
            var value = await operation(linked.Token).ConfigureAwait(false);
            ThrowIfEnded();
            ct.ThrowIfCancellationRequested();
            return value;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Fail(ex);
            ct.ThrowIfCancellationRequested();
            throw new IOException("Physical I/O failed; delivery is uncertain and was not retried.", ex);
        }
        catch (OperationCanceledException) when (Failure.IsCompleted && !ct.IsCancellationRequested)
        {
            throw new IOException("Physical connection ended during I/O.", Failure.Result);
        }
        finally
        {
            lock (_gate)
            {
                _operations--;
                if (_operations == 0 && Failure.IsCompleted) _drained.TrySetResult();
            }
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        await InvokeAsync(async token =>
        {
            var count = await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0 && buffer.Length != 0) throw new EndOfStreamException("Physical transport ended.");
            return count;
        }, ct).ConfigureAwait(false);

    public async ValueTask OpenAsync(CancellationToken ct) =>
        _ = await InvokeAsync(async token => { await _inner.OpenAsync(token).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        _ = await InvokeAsync(async token => { await _inner.WriteAsync(data, token).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);

    public async ValueTask FlushAsync(CancellationToken ct) =>
        _ = await InvokeAsync(async token => { await _inner.FlushAsync(token).ConfigureAwait(false); return true; }, ct).ConfigureAwait(false);

    public void ClearBuffer()
    {
        lock (_gate)
        {
            ThrowIfEnded();
            try { _inner.ClearBuffer(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Fail(ex); throw; }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            Fail(new IOException("Connection generation disposed."));
            return new ValueTask(_dispose ??= Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (_inner is IConnectionAwareTransport aware) aware.ConnectionLost -= OnLost;
        try { await Task.WhenAll(_drained.Task, _cancellation!).ConfigureAwait(false); }
        finally
        {
            try { await _inner.DisposeAsync().ConfigureAwait(false); }
            finally { _ended.Dispose(); }
        }
    }
}
