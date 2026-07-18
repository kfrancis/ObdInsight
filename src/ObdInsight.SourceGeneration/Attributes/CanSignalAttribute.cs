using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
/// Specifies metadata for a CAN (Controller Area Network) signal within a frame, enabling mapping between a property
/// and a specific bit field in a CAN message.
/// </summary>
/// <remarks>Apply this attribute to a property to define how its value is encoded or decoded from a CAN frame,
/// including bit position, length, scaling, offset, and optional validation constraints. This attribute is typically
/// used in code generation or serialization scenarios to automate CAN signal handling. The attribute is not inherited
/// and cannot be applied multiple times to the same property.</remarks>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class CanSignalAttribute : Attribute
{
    /// <summary>
    /// Defines a CAN signal within a frame.
    /// </summary>
    /// <param name="bitStart">Starting bit position (0 = first bit of byte 0)</param>
    /// <param name="bitLength">Number of bits to read (1-32)</param>
    public CanSignalAttribute(int bitStart, int bitLength)
    {
        if (bitStart < 0 || bitStart > 63)
            throw new ArgumentOutOfRangeException(nameof(bitStart), "Must be 0-63");
        if (bitLength < 1 || bitLength > 32)
            throw new ArgumentOutOfRangeException(nameof(bitLength), "Must be 1-32");

        BitStart = bitStart;
        BitLength = bitLength;
    }

    /// <summary>
    /// Number of bits to read (1-32)
    /// </summary>
    public int BitLength { get; }

    /// <summary>
    /// Starting bit position in the CAN frame (0-63).
    /// Bit 0 is the least significant bit of byte 0.
    /// </summary>
    public int BitStart { get; }

    /// <summary>
    /// Human-readable description of this signal.
    /// Used for XML documentation generation.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Multiplier applied to the raw value (default: 1.0).
    /// Formula: physical_value = (raw_value * Factor) + Offset
    /// </summary>
    public double Factor { get; set; } = 1.0;

    /// <summary>
    /// Whether the raw value should be interpreted as signed (default: false).
    /// Only relevant for integer types (int, short, sbyte).
    /// </summary>
    public bool IsSigned { get; set; } = false;

    /// <summary>
    /// Maximum expected value (inclusive), after scaling. Used only for generated XML
    /// documentation ("Valid range" remarks); no runtime validation is emitted — callers
    /// must range-check decoded values themselves if needed.
    /// Set to double.NaN to omit from documentation.
    /// </summary>
    public double MaxValue { get; set; } = double.NaN;

    /// <summary>
    /// Minimum expected value (inclusive), after scaling. Used only for generated XML
    /// documentation ("Valid range" remarks); no runtime validation is emitted — callers
    /// must range-check decoded values themselves if needed.
    /// Set to double.NaN to omit from documentation.
    /// </summary>
    public double MinValue { get; set; } = double.NaN;

    /// <summary>
    /// Offset added after scaling (default: 0.0).
    /// Formula: physical_value = (raw_value * Factor) + Offset
    /// </summary>
    public double Offset { get; set; } = 0.0;

    /// <summary>
    /// Unit of measurement (e.g., "°C", "V", "A", "kW").
    /// Used only for documentation generation.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Determines whether this signal should be included in code generation (default: true).
    /// Set to false for signals of unknown meaning that should be documented but not generated.
    /// </summary>
    public bool IncludeInGeneration { get; set; } = true;
}
