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

    /// <summary>Parses an 8-byte little-endian CAN frame payload.</summary>
    static abstract TSelf Parse(ReadOnlySpan<byte> data);
}
