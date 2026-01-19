using System.Collections.Generic;

namespace ObdInsight.SourceGeneration.Models;

/// <summary>
/// Represents a response variant (different vehicle models).
/// </summary>
internal sealed class ResponseVariant
{
    public int Length { get; set; }
    public string Model { get; set; } = "";
}

/// <summary>
/// Represents a parsed UDS PID response class.
/// </summary>
internal sealed class UdsPidModel
{
    public string ClassName { get; set; } = "";
    public List<UdsFieldModel> Fields { get; set; } = [];
    public int MaxLength { get; set; }
    public string? MethodName { get; set; }
    public int MinLength { get; set; }
    public byte PidId { get; set; }
    public List<ResponseVariant> Variants { get; set; } = [];
}

/// <summary>
/// Represents a parsed UDS service class.
/// </summary>
internal sealed class UdsServiceModel
{
    public string ClassName { get; set; } = "";
    public string? Description { get; set; }
    public string? EcuType { get; set; }
    public string Namespace { get; set; } = "";
    public List<UdsPidModel> Pids { get; set; } = [];
    public byte ServiceId { get; set; }
}

/// <summary>
/// Represents a parsed field definition.
/// </summary>
internal sealed class UdsFieldModel
{
    public string? AppliesTo { get; set; }
    public int ElementCount { get; set; }
    public int ElementLength { get; set; }
    public string FieldType { get; set; } = "";
    public int FrameSequence { get; set; }
    public string FrameSource { get; set; } = "Payload";
    public bool IsArray { get; set; }
    public bool IsComputed { get; set; }
    public int Length { get; set; }
    public int Offset { get; set; }
    public string PropertyName { get; set; } = "";
    public string PropertyType { get; set; } = "";
    public double Scale { get; set; } = 1.0;
    public string? ValidRange { get; set; }
}
