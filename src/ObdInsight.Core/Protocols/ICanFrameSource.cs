using ObdInsight.Core.Communication.Elm327;

namespace ObdInsight.Core.Protocols;

/// <summary>
///     A stream of raw CAN frames, whatever produced them.
/// </summary>
/// <remarks>
///     <para>
///         Everything upstream of this point currently assumes an ELM327: <see cref="IElmSession" />
///         models an adapter that must be put into a monitoring mode, kept alive, and restarted
///         when its buffer overflows. A raw CAN interface has none of those concepts - it is opened
///         and it emits frames - so consumers that only want frames should not have to know which
///         kind of device is underneath.
///     </para>
///     <para>
///         Deliberately narrower than <see cref="IElmSession" />. It carries no notion of ECU
///         context, filters, or querying, because those are properties of the ELM327 command set
///         rather than of CAN. An implementation that has them (see the ELM adapter) keeps them
///         internal; one that does not (SLCAN) is not forced to invent them.
///     </para>
/// </remarks>
public interface ICanFrameSource : IAsyncDisposable
{
    /// <summary>
    ///     Why the last <see cref="ReadFramesAsync" /> enumeration finished.
    /// </summary>
    /// <remarks>
    ///     Lets a consumer distinguish a clean stop from a recoverable adapter failure without
    ///     knowing what kind of device it is talking to. Sources with no failure mode of their own
    ///     report <see cref="MonitoringEndReason.Stopped" />.
    /// </remarks>
    MonitoringEndReason LastEndReason { get; }

    /// <summary>Opens the device and begins receiving. Idempotent.</summary>
    ValueTask StartAsync(CancellationToken ct);

    /// <summary>Stops receiving and returns the device to a quiescent state. Idempotent.</summary>
    ValueTask StopAsync(CancellationToken ct);

    /// <summary>
    ///     Yields frames until cancelled or the source ends. Ending is not necessarily an error -
    ///     check <see cref="LastEndReason" />.
    /// </summary>
    IAsyncEnumerable<RawCanFrame> ReadFramesAsync(CancellationToken ct);
}
