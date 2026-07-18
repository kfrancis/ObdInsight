using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
///     Marks a nested class as a UDS PID (Parameter ID) response definition.
///     The source generator will create a Query{Name}Async method.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UdsPidAttribute : Attribute
{
    public UdsPidAttribute(byte pidId)
    {
        PidId = pidId;
    }

    /// <summary>
    ///     Optional: Name to use for the generated method (default: class name without "Response" suffix).
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    ///     The PID byte value (e.g., 0x01 for group 1).
    /// </summary>
    public byte PidId { get; }
}
