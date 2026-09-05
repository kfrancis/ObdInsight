using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
///     Defines how to extract an array field from a UDS response payload.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UdsArrayFieldAttribute : Attribute
{
    /// <summary>
    ///     Number of elements in the array.
    /// </summary>
    public int ElementCount { get; set; }

    /// <summary>
    ///     Length of each element in bytes.
    /// </summary>
    public int ElementLength { get; set; }

    /// <summary>
    ///     Byte offset in the payload (after header stripping).
    /// </summary>
    public int Offset { get; set; }

    /// <summary>
    ///     Data type for each element.
    /// </summary>
    public UdsFieldType Type { get; set; }

    /// <summary>
    ///     Optional: Valid range for each element in format "min..max".
    ///     For nullable element types, invalid elements remain null at their original
    ///     index. For nonnullable element types, an invalid element fails the response.
    /// </summary>
    public string? ValidRange { get; set; }
}
