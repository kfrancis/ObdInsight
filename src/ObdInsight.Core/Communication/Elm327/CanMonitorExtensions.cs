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
        ///     Frames whose payload is not exactly 8 bytes are skipped (generated decoders
        ///     require full frames).
        /// </summary>
        public static async IAsyncEnumerable<T> Subscribe<T>(
            this CanMonitor monitor,
            [EnumeratorCancellation] CancellationToken ct = default)
            where T : ICanFrame<T>
        {
            await foreach (var raw in monitor.Subscribe(new[] { T.FrameCanId }, ct))
            {
                if (raw.Data.Length == 8)
                {
                    yield return T.Parse(raw.Data.Span);
                }
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
        ///     seen with a full 8-byte payload. O(1) plus decode; no I/O.
        /// </summary>
        public static bool TryGetLatest<T>(this CanMonitor monitor, out T frame)
            where T : ICanFrame<T>
        {
            if (monitor.TryGetLatest(T.FrameCanId, out var raw) && raw.Data.Length == 8)
            {
                frame = T.Parse(raw.Data.Span);
                return true;
            }

            frame = default!;
            return false;
        }
    }
}
