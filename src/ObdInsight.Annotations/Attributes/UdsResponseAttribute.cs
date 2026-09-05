using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
///     Defines expected response characteristics for a UDS PID.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class UdsResponseAttribute : Attribute
{
    /// <summary>
    ///     Maximum data length after the two-byte service/PID header. Zero means
    ///     no upper bound beyond the supported ISO-TP payload limit.
    /// </summary>
    public int MaxLength { get; set; }

    /// <summary>
    ///     Minimum data length after the two-byte service/PID header. Field geometry
    ///     is also checked even when this value is zero.
    /// </summary>
    public int MinLength { get; set; }
}
