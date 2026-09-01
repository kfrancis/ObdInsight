using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ObdInsight.Core.Communication.Elm327;

/// <summary>Reconnection behavior for <see cref="ReconnectingElmTransport" />.</summary>
public sealed record ReconnectOptions
{
    /// <summary>Reconnect attempts before giving up (default 6).</summary>
    public int MaxAttempts { get; init; } = 6;

    /// <summary>First retry delay; doubles per attempt (default 500 ms).</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Backoff cap (default 8 s).</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>
///     Resilient <see cref="IElmTransport" /> decorator (roadmap B10,
///     docs/RESILIENCE_DESIGN.md): owns a transport <em>factory</em> and transparently
///     replaces a dead inner transport. During an outage, reads and writes block until
///     reconnection succeeds — the session/monitor/capability objects above are never torn
///     down, so a BLE drop in a moving car costs a data gap, not a rebuild.
///     Reconnect triggers: the inner transport's
///     <see cref="IConnectionAwareTransport.ConnectionLost" /> (proactive) or an
///     <see cref="IOException" />/<see cref="ObjectDisposedException" /> from inner I/O
///     (reactive). Other exceptions propagate untouched — they are protocol-level, not
///     link-level. After <see cref="ReconnectOptions.MaxAttempts" /> failures the state
///     becomes <see cref="ConnectionState.Lost" /> and I/O throws until an explicit
///     <see cref="OpenAsync" />.
/// </summary>
public sealed class ReconnectingElmTransport : IConnectionAwareTransport, IConnectionStateSource
{
    private readonly ILogger<ReconnectingElmTransport> _logger;
    private readonly ReconnectOptions _options;
    private readonly object _stateLock = new();
    private readonly Func<IElmTransport> _transportFactory;
    private TaskCompletionSource _connectedSignal = NewSignal();
    private bool _disposed;

    private IElmTransport? _inner;
    private bool _reconnectInFlight;
    private ConnectionState _state = ConnectionState.Connecting;

    public ReconnectingElmTransport(
        Func<IElmTransport> transportFactory,
        ReconnectOptions? options = null,
        ILogger<ReconnectingElmTransport>? logger = null)
    {
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _options = options ?? new ReconnectOptions();
        _logger = logger ?? NullLogger<ReconnectingElmTransport>.Instance;
    }

    public event EventHandler? ConnectionLost;

    public bool IsOpen => State == ConnectionState.Connected;

    public async ValueTask OpenAsync(CancellationToken ct)
    {
        SetState(ConnectionState.Connecting);
        var inner = _transportFactory();
        await inner.OpenAsync(ct);
        AdoptInner(inner);
        SetState(ConnectionState.Connected);
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            var inner = await GetConnectedInnerAsync(ct);
            try
            {
                return await inner.ReadAsync(buffer, ct);
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                OnLinkFailure(inner, ex);
            }
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        while (true)
        {
            var inner = await GetConnectedInnerAsync(ct);
            try
            {
                await inner.WriteAsync(data, ct);
                return;
            }
            catch (Exception ex) when (IsLinkFailure(ex))
            {
                OnLinkFailure(inner, ex);
            }
        }
    }

    public ValueTask FlushAsync(CancellationToken ct)
    {
        var inner = _inner;
        return inner is { IsOpen: true } ? inner.FlushAsync(ct) : ValueTask.CompletedTask;
    }

    public void ClearBuffer()
    {
        try
        {
            _inner?.ClearBuffer();
        }
        catch
        {
            // A dead inner transport is being replaced anyway.
        }
    }

    public async ValueTask DisposeAsync()
    {
        IElmTransport? inner;
        lock (_stateLock)
        {
            _disposed = true;
            inner = _inner;
            _inner = null;
            _connectedSignal.TrySetResult(); // release blocked waiters; they see Lost
            _state = ConnectionState.Lost;
        }

        if (inner is not null)
        {
            await inner.DisposeAsync();
        }
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    public ConnectionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    private async ValueTask<IElmTransport> GetConnectedInnerAsync(CancellationToken ct)
    {
        while (true)
        {
            Task waitTask;
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_state == ConnectionState.Lost)
                {
                    throw new IOException("Transport connection lost; reconnection exhausted.");
                }

                if (_state == ConnectionState.Connected && _inner is not null)
                {
                    return _inner;
                }

                waitTask = _connectedSignal.Task;
            }

            await waitTask.WaitAsync(ct);
        }
    }

    private static bool IsLinkFailure(Exception ex) =>
        ex is IOException or ObjectDisposedException;

    private void OnLinkFailure(IElmTransport failedInner, Exception ex)
    {
        _logger.LogWarning(ex, "Transport link failure — starting reconnection");
        BeginReconnect(failedInner);
    }

    private void OnInnerConnectionLost(object? sender, EventArgs e)
    {
        if (sender is IElmTransport inner)
        {
            _logger.LogWarning("Transport reported connection lost — starting reconnection");
            BeginReconnect(inner);
        }
    }

    private void BeginReconnect(IElmTransport failedInner)
    {
        lock (_stateLock)
        {
            // Single-flight: the first trigger wins; later failures of the same (or an
            // already-replaced) inner transport don't spawn parallel supervisors.
            if (_disposed || _reconnectInFlight || !ReferenceEquals(_inner, failedInner))
            {
                return;
            }

            _reconnectInFlight = true;
            _inner = null;
        }

        SetState(ConnectionState.Reconnecting);
        _ = Task.Run(() => ReconnectLoopAsync(failedInner));
    }

    private async Task ReconnectLoopAsync(IElmTransport deadInner)
    {
        UnhookInner(deadInner);
        try
        {
            await deadInner.DisposeAsync();
        }
        catch
        {
            // Already dead.
        }

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            if (_disposed)
            {
                return;
            }

            var delayMs = Math.Min(
                _options.MaxDelay.TotalMilliseconds,
                _options.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs));

            try
            {
                var candidate = _transportFactory();
                await candidate.OpenAsync(CancellationToken.None);
                AdoptInner(candidate);
                lock (_stateLock)
                {
                    _reconnectInFlight = false;
                }

                SetState(ConnectionState.Connected);
                _logger.LogInformation("Reconnected on attempt {Attempt}", attempt);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconnect attempt {Attempt}/{Max} failed",
                    attempt, _options.MaxAttempts);
            }
        }

        lock (_stateLock)
        {
            _reconnectInFlight = false;
        }

        SetState(ConnectionState.Lost);
        ConnectionLost?.Invoke(this, EventArgs.Empty);
    }

    private void AdoptInner(IElmTransport inner)
    {
        lock (_stateLock)
        {
            _inner = inner;
        }

        if (inner is IConnectionAwareTransport aware)
        {
            aware.ConnectionLost += OnInnerConnectionLost;
        }
    }

    private void UnhookInner(IElmTransport inner)
    {
        if (inner is IConnectionAwareTransport aware)
        {
            aware.ConnectionLost -= OnInnerConnectionLost;
        }
    }

    private void SetState(ConnectionState newState)
    {
        ConnectionState oldState;
        lock (_stateLock)
        {
            if (_state == newState)
            {
                return;
            }

            oldState = _state;
            _state = newState;

            if (newState is ConnectionState.Connected or ConnectionState.Lost)
            {
                // Release blocked I/O; Lost makes them throw, Connected lets them proceed.
                _connectedSignal.TrySetResult();
            }
            else
            {
                _connectedSignal = NewSignal();
            }
        }

        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(oldState, newState));
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
