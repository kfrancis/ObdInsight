namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
///     Bit ordering for a CAN signal, matching the two conventions DBC files use.
/// </summary>
/// <remarks>
///     Both orders number bits identically - bit <c>N</c> means byte <c>N/8</c>, bit <c>N%8</c>,
///     with bit 7 the most significant bit of that byte. What differs is the direction the signal
///     travels from its start bit:
///     <list type="bullet">
///         <item>
///             <description>
///                 <see cref="Intel" /> (DBC <c>@1</c>): the start bit is the signal's <b>least</b>
///                 significant bit, and the signal grows toward higher bit numbers, wrapping into
///                 the next byte.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <see cref="Motorola" /> (DBC <c>@0</c>): the start bit is the signal's <b>most</b>
///                 significant bit, and the signal grows by descending within the byte; on crossing
///                 below bit 0 it continues at bit 7 of the <i>next</i> byte.
///             </description>
///         </item>
///     </list>
///     Most Nissan Leaf signals are Motorola. Before this existed every one had to be
///     hand-converted to Intel bit positions when writing a <see cref="CanSignalAttribute" />, and
///     that conversion is the documented cause of several wrong layouts - 0x55B's SOC decoded as
///     1 instead of 928 because Motorola start bit 7 was transcribed literally.
/// </remarks>
public enum CanByteOrder
{
    /// <summary>Little-endian; start bit is the signal's LSB. DBC <c>@1</c>. The default.</summary>
    Intel = 0,

    /// <summary>Big-endian; start bit is the signal's MSB. DBC <c>@0</c>.</summary>
    Motorola = 1
}
