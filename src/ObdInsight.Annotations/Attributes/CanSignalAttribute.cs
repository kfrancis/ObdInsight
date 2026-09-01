using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
///     Specifies metadata for a CAN (Controller Area Network) signal within a frame, enabling mapping between a property
///     and a specific bit field in a CAN message.
/// </summary>
/// <remarks>
///     Apply this attribute to a property to define how its value is encoded or decoded from a CAN frame,
///     including the bit position, length, scaling, offset, and optional validation constraints. This attribute is typically
///     used in code generation or serialization scenarios to automate CAN signal handling. The attribute is not inherited
///     and cannot be applied multiple times to the same property.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CanSignalAttribute : Attribute
{
    /// <summary>
    ///     Defines a CAN signal within a frame.
    /// </summary>
    /// <param name="bitStart">Starting bit position (0 = first bit of byte 0)</param>
    /// <param name="bitLength">Number of bits to read (1-32)</param>
    public CanSignalAttribute(int bitStart, int bitLength)
    {
        if (bitStart is < 0 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(bitStart), "Must be 0-63");
        }

        if (bitLength is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(bitLength), "Must be 1-32");
        }

        BitStart = bitStart;
        BitLength = bitLength;
    }

    /// <summary>
    ///     Number of bits to read (1-32)
    /// </summary>
    public int BitLength { get; }

    /// <summary>
    ///     Starting bit position in the CAN frame (0-63).
    ///     Bit 0 is the least significant bit of byte 0.
    ///     Under <see cref="CanByteOrder.Intel" /> this is the signal's LSB; under
    ///     <see cref="CanByteOrder.Motorola" /> it is the signal's MSB.
    /// </summary>
    public int BitStart { get; }

    /// <summary>
    ///     Bit ordering of the signal. Defaults to <see cref="CanByteOrder.Intel" />, so existing
    ///     definitions keep their meaning; set <see cref="CanByteOrder.Motorola" /> to use DBC
    ///     big-endian positions directly instead of hand-converting them.
    /// </summary>
    public CanByteOrder ByteOrder { get; set; } = CanByteOrder.Intel;

    /// <summary>
    ///     Human-readable description of this signal.
    ///     Used for XML documentation generation.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Multiplier applied to the raw value (default: 1.0).
    ///     Formula: physical_value = (raw_value * Factor) + Offset
    /// </summary>
    public double Factor { get; set; } = 1.0;

    /// <summary>
    ///     Whether the raw value should be interpreted as signed (default: false).
    ///     Only relevant for integer types (int, short, sbyte).
    /// </summary>
    public bool IsSigned { get; set; } = false;

    /// <summary>
    ///     Maximum expected value (inclusive), after scaling. Used only for generated XML
    ///     documentation ("Valid range" remarks); no runtime validation is emitted — callers
    ///     must range-check decoded values themselves if needed.
    ///     Set to double.NaN to omit from documentation.
    /// </summary>
    public double MaxValue { get; set; } = double.NaN;

    /// <summary>
    ///     Minimum expected value (inclusive), after scaling. Used only for generated XML
    ///     documentation ("Valid range" remarks); no runtime validation is emitted — callers
    ///     must range-check decoded values themselves if needed.
    ///     Set to double.NaN to omit from documentation.
    /// </summary>
    public double MinValue { get; set; } = double.NaN;

    /// <summary>
    ///     Offset added after scaling (default: 0.0).
    ///     Formula: physical_value = (raw_value * Factor) + Offset
    /// </summary>
    public double Offset { get; set; } = 0.0;

    /// <summary>
    ///     Unit of measurement (e.g., "°C", "V", "A", "kW").
    ///     Used only for documentation generation.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    ///     Determines whether this signal should be included in code generation (default: true).
    ///     Set to false for signals of unknown meaning that should be documented but not generated.
    /// </summary>
    public bool IncludeInGeneration { get; set; } = true;

    /// <summary>
    ///     Marks this signal as the frame's multiplexor selector - the DBC <c>M</c> marker.
    /// </summary>
    /// <remarks>
    ///     A multiplexed frame reuses the same bit positions to carry different signals depending
    ///     on the value of one selector field. Nissan Leaf 0x5C0 does this: the same bytes report
    ///     minimum, maximum or average battery history according to a two-bit flag. Decoding such
    ///     a frame without honouring the selector mixes the variants together and produces values
    ///     that look plausible and are meaningless.
    ///     <para>At most one signal per frame may set this.</para>
    /// </remarks>
    public bool IsMultiplexor { get; set; }

    /// <summary>
    ///     Restricts this signal to frames whose multiplexor selector equals this value - the DBC
    ///     <c>m&lt;n&gt;</c> marker. Leave unset for signals present in every frame.
    /// </summary>
    /// <remarks>
    ///     A signal carrying this must be declared with a nullable property type: it has no value
    ///     at all in frames selecting a different variant, and <c>null</c> says that honestly
    ///     where a default would be indistinguishable from a real reading of zero.
    /// </remarks>
    public int MuxValue { get; set; } = NotMultiplexed;

    /// <summary>Sentinel for <see cref="MuxValue" /> meaning "present in every frame".</summary>
    public const int NotMultiplexed = -1;
}
