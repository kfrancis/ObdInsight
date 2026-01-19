using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
/// Data type for UDS field parsing.
/// </summary>
public enum UdsFieldType
{
    /// <summary>Unsigned 8-bit integer</summary>
    UInt8,

    /// <summary>Signed 8-bit integer</summary>
    Int8,

    /// <summary>Unsigned 16-bit big-endian integer</summary>
    UInt16BE,

    /// <summary>Signed 16-bit big-endian integer</summary>
    Int16BE,

    /// <summary>Unsigned 24-bit big-endian integer</summary>
    UInt24BE,

    /// <summary>Signed 24-bit big-endian integer</summary>
    Int24BE,

    /// <summary>Unsigned 32-bit big-endian integer</summary>
    UInt32BE,

    /// <summary>Signed 32-bit big-endian integer</summary>
    Int32BE
}

/// <summary>
/// Defines how to extract a field from a UDS response payload.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
public sealed class UdsFieldAttribute : Attribute
{
    /// <summary>
    /// Optional: Comma-separated list of model variants this field applies to.
    /// If null, applies to all variants.
    /// </summary>
    public string? AppliesTo { get; set; }

    /// <summary>
    /// If FrameType is ConsecutiveFrame, which CF sequence number to use.
    /// </summary>
    public int FrameSequence { get; set; }

    /// <summary>
    /// Where to extract this field from (default: reassembled payload).
    /// </summary>
    public FrameSource FrameType { get; set; } = FrameSource.Payload;

    /// <summary>
    /// Length in bytes.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// Byte offset in the payload (after header stripping).
    /// </summary>
    public int Offset { get; set; }

    /// <summary>
    /// Scale factor to apply after parsing (default: 1.0).
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// Data type to parse.
    /// </summary>
    public UdsFieldType Type { get; set; }

    /// <summary>
    /// Optional: Valid range in format "min..max" (e.g., "10..100").
    /// If value is outside range, it will be ignored.
    /// </summary>
    public string? ValidRange { get; set; }
}

/// <summary>
/// Where to extract field data from.
/// </summary>
public enum FrameSource
{
    /// <summary>From reassembled payload (default)</summary>
    Payload,

    /// <summary>From a specific consecutive frame</summary>
    ConsecutiveFrame,

    /// <summary>From the first frame</summary>
    FirstFrame
}
