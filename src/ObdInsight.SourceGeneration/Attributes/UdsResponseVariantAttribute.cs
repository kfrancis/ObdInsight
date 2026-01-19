using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
/// Defines a response variant based on payload length.
/// Used to support different vehicle models/battery sizes.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class UdsResponseVariantAttribute : Attribute
{
    /// <summary>
    /// Expected payload length for this variant.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// Human-readable model identifier (e.g., "24kWh", "ZE1").
    /// </summary>
    public string Model { get; set; } = "";
}
