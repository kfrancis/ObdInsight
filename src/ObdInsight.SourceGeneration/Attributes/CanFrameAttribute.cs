using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
/// Specifies that a class represents a CAN (Controller Area Network) frame with a particular CAN identifier.
/// </summary>
/// <remarks>Apply this attribute to a class to associate it with a specific CAN ID for use in CAN frame decoding
/// or processing scenarios. This attribute is typically used in systems that map message types to CAN frames for
/// serialization, deserialization, or documentation purposes.</remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CanFrameAttribute : Attribute
{
    /// <summary>
    /// Creates a CAN frame decoder for the specified CAN ID.
    /// </summary>
    /// <param name="canId">CAN ID in decimal (e.g., 0x54C = 1356)</param>
    public CanFrameAttribute(int canId)
    {
        CanId = canId;
    }

    /// <summary>
    /// CAN ID this frame uses (0x000 - 0x7FF for standard 11-bit CAN)
    /// </summary>
    public int CanId { get; }

    /// <summary>
    /// Optional descriptive name for the frame (used in XML docs).
    /// If not specified, uses the class name.
    /// </summary>
    public string? Description { get; set; }
}
