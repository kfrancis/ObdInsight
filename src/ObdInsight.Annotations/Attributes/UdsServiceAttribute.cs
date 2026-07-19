using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
///     Marks a class as a UDS (Unified Diagnostic Services) service definition.
///     The source generator will create query methods for all PIDs defined within.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class UdsServiceAttribute : Attribute
{
    public UdsServiceAttribute(byte serviceId)
    {
        ServiceId = serviceId;
    }

    /// <summary>
    ///     Optional: Human-readable description of this service.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Optional: The ECU type this service is for (e.g., "BMS", "VCM").
    /// </summary>
    public string? EcuType { get; set; }

    /// <summary>
    ///     The UDS service ID (e.g., 0x21 for ReadDataByIdentifier).
    /// </summary>
    public byte ServiceId { get; }
}
