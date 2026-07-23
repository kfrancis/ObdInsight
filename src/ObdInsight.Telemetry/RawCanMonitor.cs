using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Telemetry;

/// <summary>
///     Default <see cref="IRawCanMonitor" />: a thin timestamp-stamping wrapper over
///     <see cref="CanMonitor" />, reusing its BUFFER FULL auto-restart, reconnect-safe
///     re-entry, and drop-oldest per-subscriber buffering.
/// </summary>
public sealed class RawCanMonitor : IRawCanMonitor
{
    private readonly CanMonitor _monitor;

    /// <param name="session">The session to monitor through.</param>
    /// <param name="context">
    ///     Monitoring context. Defaults to <see cref="EcuContext.RawCanMonitor" /> (accept-all
    ///     ATMA, no vehicle-specific filtering).
    /// </param>
    /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
    public RawCanMonitor(IElmSession session, EcuContext? context = null, ILogger<CanMonitor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _monitor = new CanMonitor(session, context ?? EcuContext.RawCanMonitor, logger);
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get => _monitor.IsRunning;
    }

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        return _monitor.StartAsync(ct);
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken ct = default)
    {
        return _monitor.StopAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Registration with the underlying <see cref="CanMonitor" /> happens synchronously in
    ///     this call (not on first enumeration) so frames enqueued right after calling this
    ///     method are never missed, even if iteration starts on another thread/Task.
    /// </remarks>
    public IAsyncEnumerable<RawCanFrame> MonitorRawFramesAsync(CancellationToken ct = default)
    {
        var frames = _monitor.Subscribe(ReadOnlyMemory<int>.Empty, ct);
        return TimestampAsync(frames, ct);
    }

    private static async IAsyncEnumerable<RawCanFrame> TimestampAsync(
        IAsyncEnumerable<Core.Protocols.RawCanFrame> frames,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var frame in frames.WithCancellation(ct))
        {
            yield return new RawCanFrame(DateTimeOffset.UtcNow, frame.CanId, frame.Data.ToArray());
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _monitor.DisposeAsync();
    }
}
