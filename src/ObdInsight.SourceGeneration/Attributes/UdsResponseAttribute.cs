using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
/// Defines expected response characteristics for a UDS PID.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class UdsResponseAttribute : Attribute
{
    /// <summary>
    /// Maximum expected payload length (after header).
    /// </summary>
    public int MaxLength { get; set; }

    /// <summary>
    /// Minimum expected payload length (after header).
    /// </summary>
    public int MinLength { get; set; }
}
