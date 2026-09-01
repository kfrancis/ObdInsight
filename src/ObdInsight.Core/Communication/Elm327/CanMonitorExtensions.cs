using System.Runtime.CompilerServices;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Communication.Elm327
{
    /// <summary>
    ///     Typed decoded streams over <see cref="CanMonitor" /> for source-generated frame types.
    ///     See docs/STREAMING_MONITOR_DESIGN.md (Phase 2).
    /// </summary>
    public static class CanMonitorExtensions
    {
        /// <summary>
        ///     Streams decoded frames of type <typeparamref name="T" /> as they arrive.
        ///     Registration is immediate (same semantics as the raw Subscribe — frames arriving
        ///     before the consumer starts iterating are buffered, not lost). Frames shorter than
        ///     <c>T.MinimumLength</c> are skipped — they cannot carry all of the frame's signals.
        /// </summary>
        public static IAsyncEnumerable<T> Subscribe<T>(
            this CanMonitor monitor,
            CancellationToken ct = default)
            where T : ICanFrame<T>
        {
            // Register eagerly — an async iterator here would defer registration to the first
            // MoveNext, silently dropping frames that arrive before iteration starts.
            var raw = monitor.Subscribe(new[] { T.FrameCanId }, ct);
            return DecodeAsync<T>(raw);
        }

        private static async IAsyncEnumerable<T> DecodeAsync<T>(IAsyncEnumerable<RawCanFrame> frames)
            where T : ICanFrame<T>
        {
            await foreach (var raw in frames)
            {
                if (raw.Data.Length >= T.MinimumLength)
                {
                    yield return T.Parse(raw.Data.Span);
                }
            }
        }

        /// <summary>
        ///     Streams a status projection built from the monitor's latest-frame cache, re-emitting
        ///     whenever any of <paramref name="canIds" /> arrives (coalesce-on-any). This is how a
        ///     multi-frame status DTO is streamed: each contributing CAN ID has its own cadence, so
        ///     the newest frame triggers the emission and the rest of the DTO comes from the cache.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Registration is immediate — frames arriving before the consumer starts iterating
        ///         are buffered, not lost. The monitor is started on first enumeration if it has not run
        ///         yet; a monitor that has already ended is not restarted, so the stream just completes.
        ///     </para>
        ///     <para>
        ///         Cold start: the first emission fires on the first contributing frame, so fields
        ///         sourced from IDs not yet seen are null/default. That matches the pull API's
        ///         degradation contract (absence is null, never an exception).
        ///     </para>
        ///     <para>
        ///         The stream completes when the monitor's run ends (see
        ///         <see cref="CanMonitor.EndReason" /> for why).
        ///     </para>
        /// </remarks>
        /// <param name="monitor">The shared monitor to view.</param>
        /// <param name="canIds">The CAN IDs that contribute to the projection. Empty = every frame.</param>
        /// <param name="snapshot">Builds the status from the cache. Called once per emission.</param>
        /// <param name="minInterval">
        ///     Minimum spacing between emissions; default (zero) emits on every contributing frame.
        ///     Emissions inside the interval are skipped, not queued — the next frame after it
        ///     carries the newest state, which is what a 10 ms broadcast consumer wants.
        /// </param>
        /// <param name="ct">Stops the stream.</param>
        public static IAsyncEnumerable<TStatus> StreamSnapshots<TStatus>(
            this CanMonitor monitor,
            ReadOnlyMemory<int> canIds,
            Func<TStatus> snapshot,
            TimeSpan minInterval = default,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(monitor);
            ArgumentNullException.ThrowIfNull(snapshot);

            // Register eagerly — an async iterator would defer registration to the first
            // MoveNext, silently dropping frames that arrive before iteration starts.
            var frames = monitor.Subscribe(canIds, ct);
            return CoalesceAsync(monitor, frames, snapshot, minInterval, ct);
        }

        private static async IAsyncEnumerable<TStatus> CoalesceAsync<TStatus>(
            CanMonitor monitor,
            IAsyncEnumerable<RawCanFrame> frames,
            Func<TStatus> snapshot,
            TimeSpan minInterval,
            [EnumeratorCancellation] CancellationToken ct)
        {
            // Start on first enumeration so a stream is usable on its own, but never resurrect a
            // monitor that has already ended - that subscription is closed, so the stream simply
            // completes (EndReason says why).
            if (monitor.EndReason == MonitoringEndReason.None)
            {
                await monitor.StartAsync(ct);
            }

            var throttleMs = (long)minInterval.TotalMilliseconds;
            var emitted = false;
            var lastEmit = 0L;

            await foreach (var _ in frames.WithCancellation(ct))
            {
                var now = Environment.TickCount64;
                if (throttleMs > 0 && emitted && now - lastEmit < throttleMs)
                {
                    continue;
                }

                emitted = true;
                lastEmit = now;
                yield return snapshot();
            }
        }

        /// <summary>
        ///     Waits until the monitor's latest-frame cache holds all of <paramref name="canIds" />,
        ///     or the timeout elapses. Returns immediately on a warm cache. Used by cache-view
        ///     capabilities to bridge the cold-start gap; partial data after timeout is expected.
        /// </summary>
        /// <returns>true if all IDs are cached; false if the timeout elapsed first.</returns>
        public static async ValueTask<bool> WaitForCacheAsync(
            this CanMonitor monitor,
            TimeSpan timeout,
            CancellationToken ct,
            params int[] canIds)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (true)
            {
                var allPresent = true;
                foreach (var id in canIds)
                {
                    if (!monitor.TryGetLatest(id, out _))
                    {
                        allPresent = false;
                        break;
                    }
                }

                if (allPresent)
                {
                    return true;
                }

                if (Environment.TickCount64 >= deadline)
                {
                    return false;
                }

                await Task.Delay(10, ct);
            }
        }

        /// <summary>
        ///     Decodes the latest cached frame of type <typeparamref name="T" />, if one has been
        ///     seen with a payload of at least <c>T.MinimumLength</c> bytes. O(1) plus decode; no I/O.
        /// </summary>
        public static bool TryGetLatest<T>(this CanMonitor monitor, out T frame)
            where T : ICanFrame<T>
        {
            if (monitor.TryGetLatest(T.FrameCanId, out var raw) && raw.Data.Length >= T.MinimumLength)
            {
                frame = T.Parse(raw.Data.Span);
                return true;
            }

            frame = default!;
            return false;
        }
    }
}
