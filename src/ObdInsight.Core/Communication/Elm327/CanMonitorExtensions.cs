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
