using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Communication.Elm327
{
    /// <summary>
    ///     Long-lived owner of the adapter's monitoring mode with channel-based fan-out.
    ///     One monitoring pass feeds any number of subscribers (each with an independent bounded
    ///     channel) plus a latest-frame cache for O(1) snapshots. Recovers automatically from
    ///     ELM327 BUFFER FULL and reports why a run ended via <see cref="EndReason" />.
    ///     See docs/STREAMING_MONITOR_DESIGN.md (Phase 1).
    /// </summary>
    /// <remarks>
    ///     Not safe for concurrent <see cref="StartAsync" />/<see cref="StopAsync" /> calls (matches
    ///     <see cref="ElmSession" />'s threading contract). Subscribing and reading streams from any
    ///     thread is safe.
    /// </remarks>
    public sealed class CanMonitor : IAsyncDisposable
    {
        // Exactly one of (_session + _context) or _source is set. The ELM path owns mode
        // transitions, filter rotation, activation and keep-alive; a frame source (raw CAN
        // adapter such as a CANable) has none of those concepts - it starts and emits frames.
        private readonly EcuContext? _context;

        // Serializes suspend/resume cycles (external query arbitration vs the keep-alive timer).
        private readonly SemaphoreSlim _controlGate = new(1, 1);
        private readonly ConcurrentDictionary<int, RawCanFrame> _latest = new();
        private readonly Lock _lock = new();
        private readonly ILogger _logger;
        private readonly IElmSession? _session;
        private readonly ICanFrameSource? _source;
        private readonly List<Subscription> _subscriptions = [];
        private bool _ended;
        private volatile bool _disposed;
        private Task? _disposeTask;
        private CancellationTokenSource? _keepAliveCts;
        private Task? _keepAliveTask;

        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private bool _suspending;

        private int _windowIndex;

        /// <param name="session">The session to monitor through. The monitor owns mode transitions while running.</param>
        /// <param name="monitoringContext">
        ///     A monitoring-mode <see cref="EcuContext" /> (e.g.
        ///     <see cref="EcuContext.NissanLeafHvbatMonitor" />).
        /// </param>
        /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
        public CanMonitor(IElmSession session, EcuContext monitoringContext, ILogger<CanMonitor>? logger = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _context = monitoringContext ?? throw new ArgumentNullException(nameof(monitoringContext));
            _logger = logger ?? NullLogger<CanMonitor>.Instance;
        }

        /// <summary>
        ///     Monitors a raw CAN frame source (e.g. <c>SlcanFrameSource</c> over a CANable) instead
        ///     of an ELM327 session. Same fan-out, cache, typed streams and end reasons; the
        ///     ELM-only features do not apply: <see cref="FilterRotation" /> must stay empty
        ///     (software demux already sees every frame), there is no session activation or
        ///     keep-alive, and <see cref="SuspendAsync" /> stops and restarts the source.
        /// </summary>
        /// <param name="source">The frame source. Started by <see cref="StartAsync" />, stopped by <see cref="StopAsync" />.</param>
        /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
        public CanMonitor(ICanFrameSource source, ILogger<CanMonitor>? logger = null)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _logger = logger ?? NullLogger<CanMonitor>.Instance;
        }

        /// <summary>True when monitoring an <see cref="ICanFrameSource" /> rather than an ELM327 session.</summary>
        public bool IsFrameSourceBacked
        {
            get => _source is not null;
        }

        /// <summary>Why the last run ended. <see cref="MonitoringEndReason.None" /> while running.</summary>
        public MonitoringEndReason EndReason { get; private set; }

        /// <summary>Whether the monitoring loop is currently running.</summary>
        public bool IsRunning
        {
            get => _loopTask is { IsCompleted: false };
        }

        /// <summary>
        ///     Per-subscriber channel capacity. When a subscriber falls behind, the OLDEST frames
        ///     are dropped — for broadcast data the newest value is the valuable one, and no
        ///     subscriber may stall the read loop.
        /// </summary>
        public int SubscriberBufferSize { get; set; } = 64;

        /// <summary>
        ///     Consecutive no-progress restarts after BUFFER FULL before giving up. Restarts where
        ///     frames flowed in between reset the counter.
        /// </summary>
        public int MaxBufferFullRestarts { get; set; } = 3;

        /// <summary>Delay before re-entering monitoring after BUFFER FULL.</summary>
        public TimeSpan RestartDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        ///     Optional hardware-filter rotation. Empty (default): one continuous monitoring pass
        ///     using the context's own filter. Non-empty: the loop cycles through the windows,
        ///     applying each window's AT CM/CF filter for its dwell time — the workaround for
        ///     adapters that overflow on accept-all monitoring. The latest-frame cache accumulates
        ///     across windows, so cache-view capabilities see data at most one full cycle stale.
        ///     Set before <see cref="StartAsync" />.
        /// </summary>
        public IReadOnlyList<CanFilterWindow> FilterRotation { get; set; } = [];

        public ValueTask DisposeAsync()
        {
            lock (_lock)
            {
                _disposed = true;
                return new ValueTask(_disposeTask ??= Task.Run(DisposeCoreAsync));
            }
        }

        private async Task DisposeCoreAsync()
        {
            try
            {
                await StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[CanMonitor] Dispose-time stop failed");
            }

            _keepAliveCts?.Cancel();
            if (_keepAliveTask is not null)
            {
                try { await _keepAliveTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _latest.Clear();
            _loopCts?.Dispose();
            _keepAliveCts?.Dispose();
        }

        /// <summary>
        ///     Enters monitoring mode and starts the shared read loop. Idempotent while running.
        ///     When the context requires session activation (e.g. a sleeping EPS module), the
        ///     activation command is sent first; when it defines a keep-alive command, a periodic
        ///     keep-alive cycle (brief suspend → TesterPresent → resume) starts alongside the loop.
        /// </summary>
        public async ValueTask StartAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
            {
                return;
            }

            if (_source is not null && FilterRotation.Count > 0)
            {
                throw new InvalidOperationException(
                    "FilterRotation is an ELM327 hardware-filter workaround and does not apply to a frame-source-backed monitor.");
            }

            // Cold-start only — resume after a suspension skips re-activation; the keep-alive
            // cycle is what keeps the ECU's session alive across suspensions.
            if (_session is not null && _context is { RequiresSessionActivation: true } &&
                !string.IsNullOrEmpty(_context.SessionActivationCommand))
            {
                var activated = await _session.ActivateSessionAsync(_context, ct);
                if (!activated)
                {
                    _logger.LogDebug(
                        "[CanMonitor] Session activation failed for '{Context}' - starting anyway (frames may be absent)",
                        _context.Name);
                }
            }

            await StartCoreAsync(ct);
        }

        private async ValueTask StartCoreAsync(CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session?.Failure is { } failure) throw failure;
            // With a filter rotation the loop enters monitoring itself, once per window.
            if (FilterRotation.Count == 0)
            {
                await EnterAsync(ct);
            }

            lock (_lock)
            {
                _ended = false;
            }

            EndReason = MonitoringEndReason.None;
            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token), CancellationToken.None);

            if (!string.IsNullOrEmpty(_context?.KeepAliveCommand) && _keepAliveTask is not { IsCompleted: false })
            {
                _keepAliveCts = new CancellationTokenSource();
                _keepAliveTask = Task.Run(() => RunKeepAliveAsync(_keepAliveCts.Token), CancellationToken.None);
            }
        }

        /// <summary>
        ///     Stops the read loop and exits monitoring mode. Safe to call when not running;
        ///     the monitor can be started again afterward.
        /// </summary>
        /// <exception cref="ObjectDisposedException"></exception>
        /// <exception cref="AggregateException"></exception>
        public async ValueTask StopAsync(CancellationToken ct)
        {
            // Serialize with suspend/resume cycles (keep-alive timer, query arbitration) so a
            // mid-cycle resume cannot revive the loop after this stop completes.
            await _controlGate.WaitAsync(ct);
            try
            {
                _keepAliveCts?.Cancel();

                var task = _loopTask;
                if (task is null)
                {
                    return;
                }

                // ReSharper disable once MethodHasAsyncOverload
                _loopCts?.Cancel();
                try
                {
                    await task.WaitAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Caller gave up waiting; the loop still winds down on its own.
                }
            }
            finally
            {
                _controlGate.Release();
            }
        }

        /// <summary>
        ///     Temporarily halts monitoring so request/response work (UDS queries) can use the
        ///     session, without tearing down subscriptions: channels stay open, the latest-frame
        ///     cache stays warm, and no end reason is recorded. Disposing the returned scope
        ///     re-enters monitoring and resumes the loop. No-op scope when not running.
        ///     Not reentrant — matches the session's single-consumer threading contract.
        /// </summary>
        public async ValueTask<IAsyncDisposable> SuspendAsync(CancellationToken ct)
        {
            // The control gate serializes suspension cycles (external query arbitration vs the
            // keep-alive timer). Held for the whole scope; released by resume.
            await _controlGate.WaitAsync(ct);

            if (!IsRunning)
            {
                _controlGate.Release();
                return NoopScope.Instance;
            }

            _suspending = true;
            try
            {
                _loopCts!.Cancel();
                await _loopTask!.WaitAsync(ct);
            }
            catch
            {
                _suspending = false;
                _controlGate.Release();
                throw;
            }

            return new SuspendScope(this);
        }

        private async ValueTask ResumeAsync()
        {
            try
            {
                _suspending = false;
                if (_session?.Failure is not null)
                {
                    // Never mask the original query's cancellation/timeout with a resume
                    // failure, nor reopen a monitor on an untrustworthy response boundary.
                    EndReason = MonitoringEndReason.TransportError;
                    _keepAliveCts?.Cancel();
                    lock (_lock)
                    {
                        _ended = true;
                        _latest.Clear();
                        foreach (var subscription in _subscriptions) subscription.Channel.Writer.TryComplete();
                    }
                    return;
                }
                await StartCoreAsync(CancellationToken.None);
            }
            finally
            {
                _controlGate.Release();
            }
        }

        private async Task RunKeepAliveAsync(CancellationToken ct)
        {
            // Only ever started for a session-backed monitor whose context has a keep-alive.
            var context = _context!;
            var session = _session!;
            var interval = TimeSpan.FromMilliseconds(Math.Max(100, context.KeepAliveIntervalMs));
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!IsRunning)
                {
                    // Parked or externally suspended — skip this beat rather than fight for the session.
                    continue;
                }

                try
                {
                    await using var scope = await SuspendAsync(ct);
                    await session.SendKeepAliveAsync(context, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Best-effort: a failed keep-alive beat must not kill monitoring.
                    _logger.LogDebug(ex, "[CanMonitor] Keep-alive cycle failed");
                }
            }
        }

        /// <summary>Latest frame seen for a CAN ID, if any. O(1), no I/O.</summary>
        public bool TryGetLatest(int canId, out RawCanFrame frame)
        {
            if (_disposed) { frame = default!; return false; }
            return _latest.TryGetValue(canId, out frame);
        }

        /// <summary>
        ///     Streams frames whose CAN ID is in <paramref name="canIds" /> (empty = all frames).
        ///     Registration is immediate; the stream completes when the monitor ends permanently.
        ///     Each subscriber gets an independent bounded channel (<see cref="SubscriberBufferSize" />,
        ///     drop-oldest).
        /// </summary>
        public IAsyncEnumerable<RawCanFrame> Subscribe(ReadOnlyMemory<int> canIds, CancellationToken ct = default)
        {
            var channel = Channel.CreateBounded<RawCanFrame>(new BoundedChannelOptions(SubscriberBufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true
            });
            var subscription = new Subscription(channel, canIds.Length == 0 ? null : [.. canIds.ToArray()]);

            lock (_lock)
            {
                if (_ended)
                {
                    channel.Writer.TryComplete();
                }

                _subscriptions.Add(subscription);
            }

            return ReadSubscriptionAsync(subscription, ct);
        }

        private async IAsyncEnumerable<RawCanFrame> ReadSubscriptionAsync(
            Subscription subscription,
            [EnumeratorCancellation] CancellationToken ct)
        {
            try
            {
                await foreach (var frame in subscription.Channel.Reader.ReadAllAsync(ct))
                {
                    yield return frame;
                }
            }
            finally
            {
                lock (_lock)
                {
                    _subscriptions.Remove(subscription);
                }
            }
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            var noProgressRestarts = 0;
            var reason = MonitoringEndReason.Stopped;
            var rotating = FilterRotation.Count > 0;

            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    CancellationTokenSource? dwellCts = null;
                    try
                    {
                        var frameToken = ct;
                        if (rotating)
                        {
                            // Enter monitoring with this window's hardware filter; Enter exits
                            // any previous window first. The dwell token rotates us out.
                            var window = FilterRotation[_windowIndex % FilterRotation.Count];
                            // Internal suspension stops the reader, not an in-flight adapter
                            // configuration. Join this command-bounded transition before exit.
                            await _session!.EnterMonitoringModeAsync(CreateWindowContext(window), CancellationToken.None);
                            dwellCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            dwellCts.CancelAfter(window.Dwell);
                            frameToken = dwellCts.Token;
                        }

                        var framesThisRun = 0;
                        await foreach (var frame in ReadFramesAsync(frameToken))
                        {
                            framesThisRun++;
                            _latest[frame.CanId] = frame;
                            Publish(frame);
                        }

                        if (ct.IsCancellationRequested)
                        {
                            reason = MonitoringEndReason.Stopped;
                            break;
                        }

                        var sessionReason = LastSourceEndReason;
                        // Both are adapter-initiated exits (overflow, or a stray prompt from
                        // residual bytes/adapter quirks) — recoverable by re-entering monitoring.
                        if (sessionReason is MonitoringEndReason.BufferFull or MonitoringEndReason.PromptDetected)
                        {
                            if (framesThisRun > 0)
                            {
                                noProgressRestarts = 0;
                            }

                            if (noProgressRestarts >= MaxBufferFullRestarts)
                            {
                                _logger.LogDebug(
                                    "[CanMonitor] {Reason} after {Restarts} no-progress restarts - giving up",
                                    sessionReason, noProgressRestarts);
                                reason = sessionReason;
                                break;
                            }

                            noProgressRestarts++;
                            _logger.LogDebug("[CanMonitor] {Reason} - restarting monitoring (attempt {Attempt}/{Max})",
                                sessionReason, noProgressRestarts, MaxBufferFullRestarts);
                            try
                            {
                                if (RestartDelay > TimeSpan.Zero)
                                {
                                    await Task.Delay(RestartDelay, ct);
                                }

                                if (!rotating)
                                {
                                    await EnterAsync(CancellationToken.None);
                                }
                                // Rotating: the next iteration enters the next window anyway.
                            }
                            catch (OperationCanceledException)
                            {
                                reason = MonitoringEndReason.Stopped;
                                break;
                            }

                            if (rotating)
                            {
                                _windowIndex++;
                            }

                            continue;
                        }

                        if (rotating)
                        {
                            // Dwell expired (or a benign end) — rotate to the next window.
                            if (framesThisRun > 0)
                            {
                                noProgressRestarts = 0;
                            }

                            _windowIndex++;
                            continue;
                        }

                        // Unexpected end without a recorded reason.
                        reason = sessionReason == MonitoringEndReason.None
                            ? MonitoringEndReason.Stopped
                            : sessionReason;
                        break;
                    }
                    finally
                    {
                        dwellCts?.Dispose();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                reason = MonitoringEndReason.Stopped;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[CanMonitor] Monitoring loop failed");
                reason = MonitoringEndReason.TransportError;
            }
            finally
            {
                // During a suspend (see SuspendAsync) the stop is temporary: subscribers stay
                // registered with open channels and no permanent end reason is recorded.
                if (!_suspending)
                {
                    EndReason = reason;
                    _keepAliveCts?.Cancel();
                    lock (_lock)
                    {
                        _ended = true;
                        foreach (var subscription in _subscriptions)
                        {
                            subscription.Channel.Writer.TryComplete();
                        }
                    }
                }

                try
                {
                    await ExitAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[CanMonitor] Failed to exit monitoring mode during shutdown");
                }
            }
        }

        private EcuContext CreateWindowContext(CanFilterWindow window)
        {
            // Rotation is rejected for frame-source monitors in StartAsync, so the context exists.
            var context = _context!;
            return new EcuContext
            {
                Name = $"{context.Name} [{window.Pattern}/{window.Mask}]",
                TxHeader = context.TxHeader,
                RxFilter = context.RxFilter,
                FlowControlHeader = context.FlowControlHeader,
                FlowControlData = context.FlowControlData,
                FlowControlMode = context.FlowControlMode,
                EnableHeaders = context.EnableHeaders,
                EnableAutoFormatting = context.EnableAutoFormatting,
                CommunicationMode = context.CommunicationMode,
                MonitoringCommand = context.MonitoringCommand,
                AdapterTimeoutUnits = context.AdapterTimeoutUnits,
                SessionActivationCommand = context.SessionActivationCommand,
                RequiresSessionActivation = context.RequiresSessionActivation,
                KeepAliveCommand = context.KeepAliveCommand,
                KeepAliveIntervalMs = context.KeepAliveIntervalMs,
                CanFilterMask = window.Mask,
                CanFilterPattern = window.Pattern
            };
        }

        // ---- Backend seam: ELM327 session vs raw frame source -------------------------------

        /// <summary>Enters monitoring on the session, or starts the frame source.</summary>
        private ValueTask EnterAsync(CancellationToken ct)
        {
            return _source is not null
                ? _source.StartAsync(ct)
                : _session!.EnterMonitoringModeAsync(_context!, ct);
        }

        private IAsyncEnumerable<RawCanFrame> ReadFramesAsync(CancellationToken ct)
        {
            return _source is not null
                ? _source.ReadFramesAsync(ct)
                : _session!.MonitorFramesAsync(ct);
        }

        /// <summary>
        ///     Why the backend's last frame enumeration ended. A frame source never reports
        ///     <see cref="MonitoringEndReason.BufferFull" /> / <see cref="MonitoringEndReason.PromptDetected" />
        ///     (those are ELM327 adapter exits), so the restart branch in the loop is ELM-only by construction.
        /// </summary>
        private MonitoringEndReason LastSourceEndReason
        {
            get => _source?.LastEndReason ?? _session!.LastMonitoringEndReason;
        }

        /// <summary>Exits monitoring on the session, or stops the frame source.</summary>
        private ValueTask ExitAsync()
        {
            return _source is not null
                ? _source.StopAsync(CancellationToken.None)
                : _session!.ExitMonitoringModeAsync(CancellationToken.None);
        }

        private void Publish(RawCanFrame frame)
        {
            lock (_lock)
            {
                foreach (var subscription in _subscriptions)
                {
                    if (subscription.Ids is null || subscription.Ids.Contains(frame.CanId))
                    {
                        subscription.Channel.Writer.TryWrite(frame);
                    }
                }
            }
        }

        private sealed record Subscription(Channel<RawCanFrame> Channel, HashSet<int>? Ids);

        private sealed class SuspendScope(CanMonitor monitor) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                return monitor.ResumeAsync();
            }
        }

        private sealed class NoopScope : IAsyncDisposable
        {
            public static readonly NoopScope Instance = new();

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
