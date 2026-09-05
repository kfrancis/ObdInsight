using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Communication.Elm327;

/// <summary>Per-request retry behavior for <see cref="RetryingElmSession" />.</summary>
public sealed record QueryRetryOptions
{
    /// <summary>Total attempts per query, including the first (default 3).</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Delay between attempts (default 250 ms).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Exact commands the caller confirms are safe to repeat. Empty by default.</summary>
    public IReadOnlyCollection<string> RetrySafeCommands { get; init; } = [];
}

/// <summary>
///     <see cref="IElmSession" /> decorator adding per-request retry to <c>QueryAsync</c>
///     only for explicitly allowlisted retry-safe commands that receive an
///     <see cref="ElmQueryRejectedException" /> (a complete response rejected by validation).
///     Uncertain I/O, invalidated sessions, timeouts and cancellation are never retried.
///     Compose <em>inside</em> the monitor-suspension decorator so a
///     suspension spans all attempts (the Leaf command set does this when handed a
///     retrying session).
/// </summary>
public sealed class RetryingElmSession : IElmSession
{
    private readonly IElmSession _inner;
    private readonly ILogger<RetryingElmSession> _logger;
    private readonly QueryRetryOptions _options;
    private readonly HashSet<string> _retrySafeCommands;

    public RetryingElmSession(
        IElmSession inner,
        QueryRetryOptions? options = null,
        ILogger<RetryingElmSession>? logger = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? new QueryRetryOptions();
        if (_options.MaxAttempts < 1 || _options.RetryDelay < TimeSpan.Zero ||
            _options.RetryDelay.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        ArgumentNullException.ThrowIfNull(_options.RetrySafeCommands);
        _retrySafeCommands = new(_options.RetrySafeCommands, StringComparer.OrdinalIgnoreCase);
        _logger = logger ?? NullLogger<RetryingElmSession>.Instance;
    }

    public TimeSpan CommandTimeout
    {
        get => _inner.CommandTimeout;
        set => _inner.CommandTimeout = value;
    }

    public EcuCommunicationMode CurrentMode => _inner.CurrentMode;

    public bool EnableDebugLogging
    {
        get => _inner.EnableDebugLogging;
        set => _inner.EnableDebugLogging = value;
    }

    public TimeSpan ProtocolDetectionTimeout
    {
        get => _inner.ProtocolDetectionTimeout;
        set => _inner.ProtocolDetectionTimeout = value;
    }

    public MonitoringEndReason LastMonitoringEndReason => _inner.LastMonitoringEndReason;

    public ValueTask<bool> ActivateSessionAsync(EcuContext context, CancellationToken ct) =>
        _inner.ActivateSessionAsync(context, ct);

    public ValueTask<bool> SendKeepAliveAsync(EcuContext context, CancellationToken ct) =>
        _inner.SendKeepAliveAsync(context, ct);

    public ValueTask EnterMonitoringModeAsync(EcuContext context, CancellationToken ct) =>
        _inner.EnterMonitoringModeAsync(context, ct);

    public ValueTask ExitMonitoringModeAsync(CancellationToken ct) =>
        _inner.ExitMonitoringModeAsync(ct);

    public ValueTask InitializeAndLockAsync(CancellationToken ct) =>
        _inner.InitializeAndLockAsync(ct);

    public IAsyncEnumerable<RawCanFrame> MonitorFramesAsync(CancellationToken ct) =>
        _inner.MonitorFramesAsync(ct);

    public ValueTask<string[]> QueryAsync(string obdCommand, CancellationToken ct) =>
        RetryAsync(() => _inner.QueryAsync(obdCommand, ct), obdCommand, ct);

    public TimeProvider TimeProvider => _inner.TimeProvider;
    public ElmSessionInvalidatedException? Failure => _inner.Failure;
    public ValueTask<Observed<string[]>> QueryResponseAsync(string command, EcuContext context, CancellationToken ct) =>
        RetryAsync(() => _inner.QueryResponseAsync(command, context, ct), command, ct);

    public ValueTask<string[]> QueryAsync(string obdCommand, EcuContext context, CancellationToken ct) =>
        RetryAsync(() => _inner.QueryAsync(obdCommand, context, ct), obdCommand, ct);

    public ValueTask SetEcuContextAsync(EcuContext context, CancellationToken ct) =>
        _inner.SetEcuContextAsync(context, ct);

    private async ValueTask<T> RetryAsync<T>(
        Func<ValueTask<T>> query, string command, CancellationToken ct)
    {
        for (var attempt = 1;; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await query();
            }
            catch (ElmQueryRejectedException ex) when (attempt < _options.MaxAttempts &&
                _retrySafeCommands.Contains(command) && _inner.Failure is null)
            {
                _logger.LogDebug(ex, "Query '{Command}' attempt {Attempt}/{Max} failed - retrying",
                    command, attempt, _options.MaxAttempts);
                await Task.Delay(_options.RetryDelay, ct);
            }
        }
    }
}
