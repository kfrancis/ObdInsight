namespace ObdInsight.Core.Protocols;

/// <summary>
///     Implemented by source-generated CAN frame decoder classes (the generator adds it
///     automatically when this interface is visible in the compilation). Enables typed
///     <c>CanMonitor.Subscribe&lt;T&gt;()</c> / <c>TryGetLatest&lt;T&gt;()</c> without reflection.
/// </summary>
/// <typeparam name="TSelf">The implementing frame type.</typeparam>
public interface ICanFrame<out TSelf> where TSelf : ICanFrame<TSelf>
{
    /// <summary>The CAN ID this frame type decodes.</summary>
    static abstract int FrameCanId { get; }

    /// <summary>
    ///     The shortest payload <see cref="Parse" /> accepts: the highest byte any of this frame's
    ///     signals touches. Frames on the wire are often shorter than 8 bytes, so consumers filter
    ///     on this rather than on a fixed length.
    /// </summary>
    static abstract int MinimumLength { get; }

    /// <summary>
    ///     Parses a little-endian CAN frame payload of at least <see cref="MinimumLength" /> bytes.
    ///     Bytes past the last signal are ignored.
    /// </summary>
    /// <exception cref="System.ArgumentException">
    ///     Thrown if <paramref name="data" /> is shorter than <see cref="MinimumLength" />.
    /// </exception>
    static abstract TSelf Parse(ReadOnlySpan<byte> data);
}
