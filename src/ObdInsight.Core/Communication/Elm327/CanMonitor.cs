using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Communication.Elm327
{
    /// <summary>
    /// Long-lived owner of the adapter's monitoring mode with channel-based fan-out.
    /// One monitoring pass feeds any number of subscribers (each with an independent bounded
    /// channel) plus a latest-frame cache for O(1) snapshots. Recovers automatically from
    /// ELM327 BUFFER FULL and reports why a run ended via <see cref="EndReason"/>.
    /// See docs/STREAMING_MONITOR_DESIGN.md (Phase 1).
    /// </summary>
    /// <remarks>
    /// Not safe for concurrent <see cref="StartAsync"/>/<see cref="StopAsync"/> calls (matches
    /// <see cref="ElmSession"/>'s threading contract). Subscribing and reading streams from any
    /// thread is safe.
    /// </remarks>
    public sealed class CanMonitor : IAsyncDisposable
    {
        private readonly IElmSession _session;
        private readonly EcuContext _context;
        private readonly ILogger _logger;
        private readonly object _lock = new();
        private readonly List<Subscription> _subscriptions = [];
        private readonly ConcurrentDictionary<int, RawCanFrame> _latest = new();

        private CancellationTokenSource? _loopCts;
        private Task? _loopTask;
        private bool _ended;

        /// <param name="session">The session to monitor through. The monitor owns mode transitions while running.</param>
        /// <param name="monitoringContext">A monitoring-mode <see cref="EcuContext"/> (e.g. <see cref="EcuContext.NissanLeafHvbatMonitor"/>).</param>
        /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
        public CanMonitor(IElmSession session, EcuContext monitoringContext, ILogger<CanMonitor>? logger = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _context = monitoringContext ?? throw new ArgumentNullException(nameof(monitoringContext));
            _logger = logger ?? NullLogger<CanMonitor>.Instance;
        }

        /// <summary>Why the last run ended. <see cref="MonitoringEndReason.None"/> while running.</summary>
        public MonitoringEndReason EndReason { get; private set; }

        /// <summary>Whether the monitoring loop is currently running.</summary>
        public bool IsRunning => _loopTask is { IsCompleted: false };

        /// <summary>
        /// Per-subscriber channel capacity. When a subscriber falls behind, the OLDEST frames
        /// are dropped — for broadcast data the newest value is the valuable one, and no
        /// subscriber may stall the read loop.
        /// </summary>
        public int SubscriberBufferSize { get; set; } = 64;

        /// <summary>
        /// Consecutive no-progress restarts after BUFFER FULL before giving up. Restarts where
        /// frames flowed in between reset the counter.
        /// </summary>
        public int MaxBufferFullRestarts { get; set; } = 3;

        /// <summary>Delay before re-entering monitoring after BUFFER FULL.</summary>
        public TimeSpan RestartDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Enters monitoring mode and starts the shared read loop. Idempotent while running.
        /// </summary>
        public async ValueTask StartAsync(CancellationToken ct)
        {
            if (IsRunning) return;

            await _session.EnterMonitoringModeAsync(_context, ct);

            lock (_lock)
            {
                _ended = false;
            }
            EndReason = MonitoringEndReason.None;
            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token), CancellationToken.None);
        }

        /// <summary>
        /// Stops the read loop and exits monitoring mode. Safe to call when not running;
        /// the monitor can be started again afterwards.
        /// </summary>
        public async ValueTask StopAsync(CancellationToken ct)
        {
            var task = _loopTask;
            if (task is null) return;

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

        /// <summary>Latest frame seen for a CAN ID, if any. O(1), no I/O.</summary>
        public bool TryGetLatest(int canId, out RawCanFrame frame) => _latest.TryGetValue(canId, out frame);

        /// <summary>
        /// Streams frames whose CAN ID is in <paramref name="canIds"/> (empty = all frames).
        /// Registration is immediate; the stream completes when the monitor ends permanently.
        /// Each subscriber gets an independent bounded channel (<see cref="SubscriberBufferSize"/>,
        /// drop-oldest).
        /// </summary>
        public IAsyncEnumerable<RawCanFrame> Subscribe(ReadOnlyMemory<int> canIds, CancellationToken ct = default)
        {
            var channel = Channel.CreateBounded<RawCanFrame>(new BoundedChannelOptions(SubscriberBufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
            var subscription = new Subscription(channel, canIds.Length == 0 ? null : new HashSet<int>(canIds.ToArray()));

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

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[CanMonitor] Dispose-time stop failed");
            }
            _loopCts?.Dispose();
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

            try
            {
                while (true)
                {
                    var framesThisRun = 0;
                    await foreach (var frame in _session.MonitorFramesAsync(ct))
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

                    var sessionReason = _session.LastMonitoringEndReason;
                    if (sessionReason == MonitoringEndReason.BufferFull)
                    {
                        if (framesThisRun > 0)
                        {
                            noProgressRestarts = 0;
                        }
                        if (noProgressRestarts >= MaxBufferFullRestarts)
                        {
                            _logger.LogDebug("[CanMonitor] BUFFER FULL after {Restarts} no-progress restarts - giving up", noProgressRestarts);
                            reason = MonitoringEndReason.BufferFull;
                            break;
                        }

                        noProgressRestarts++;
                        _logger.LogDebug("[CanMonitor] BUFFER FULL - restarting monitoring (attempt {Attempt}/{Max})", noProgressRestarts, MaxBufferFullRestarts);
                        try
                        {
                            if (RestartDelay > TimeSpan.Zero)
                                await Task.Delay(RestartDelay, ct);
                            await _session.EnterMonitoringModeAsync(_context, ct);
                        }
                        catch (OperationCanceledException)
                        {
                            reason = MonitoringEndReason.Stopped;
                            break;
                        }
                        continue;
                    }

                    // PromptDetected or an unexpected end without a recorded reason.
                    reason = sessionReason == MonitoringEndReason.None ? MonitoringEndReason.Stopped : sessionReason;
                    break;
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
                EndReason = reason;
                lock (_lock)
                {
                    _ended = true;
                    foreach (var subscription in _subscriptions)
                    {
                        subscription.Channel.Writer.TryComplete();
                    }
                }

                try
                {
                    await _session.ExitMonitoringModeAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[CanMonitor] Failed to exit monitoring mode during shutdown");
                }
            }
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
    }
}
