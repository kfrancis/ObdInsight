using System.Collections.Generic;

namespace ObdInsight.SourceGeneration.Models;

/// <summary>
/// Represents a response variant (different vehicle models).
/// </summary>
internal sealed class ResponseVariant
{
    /// <summary>
    /// Gets or sets the length value associated with the current instance.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// Gets or sets the model name associated with this instance.
    /// </summary>
    public string Model { get; set; } = "";
}

/// <summary>
/// Represents a parsed UDS PID response class.
/// </summary>
internal sealed class UdsPidModel
{
    /// <summary>
    /// Gets or sets the name of the class represented by this instance.
    /// </summary>
    public string ClassName { get; set; } = "";

    /// <summary>
    /// Gets or sets the collection of user-defined fields associated with this model.
    /// </summary>
    /// <remarks>Each field in the collection represents a custom data element. Modifying the collection
    /// affects the set of fields available for this model. The order of fields in the list may be significant depending
    /// on how the model is processed.</remarks>
    public List<UdsFieldModel> Fields { get; set; } = [];

    /// <summary>
    /// Gets or sets the maximum allowed length for the content.
    /// </summary>
    public int MaxLength { get; set; }

    /// <summary>
    /// Gets or sets the name of the method to be invoked or referenced.
    /// </summary>
    public string? MethodName { get; set; }

    /// <summary>
    /// Gets or sets the minimum allowable length for the input value.
    /// </summary>
    public int MinLength { get; set; }

    /// <summary>
    /// Gets or sets the identifier for the process associated with this instance.
    /// </summary>
    public byte PidId { get; set; }

    /// <summary>
    /// Gets or sets the collection of response variants associated with this instance.
    /// </summary>
    /// <remarks>Each variant represents an alternative response option. Modifying this collection affects
    /// which variants are available for selection or processing.</remarks>
    public List<ResponseVariant> Variants { get; set; } = [];
}

/// <summary>
/// Represents a parsed UDS service class.
/// </summary>
internal sealed class UdsServiceModel
{
    /// <summary>
    /// Gets or sets the name of the class represented by this instance.
    /// </summary>
    public string ClassName { get; set; } = "";

    /// <summary>
    /// Gets or sets the descriptive text associated with the object.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the type of the electronic control unit (ECU) associated with this instance.
    /// </summary>
    public string? EcuType { get; set; }

    /// <summary>
    /// Gets or sets the namespace associated with the current object.
    /// </summary>
    public string Namespace { get; set; } = "";

    /// <summary>
    /// Gets or sets the collection of supported UDS parameter identifiers (PIDs).
    /// </summary>
    /// <remarks>Each item in the collection represents a diagnostic parameter available for querying or
    /// reporting. Modifying this list affects which PIDs are exposed by the model.</remarks>
    public List<UdsPidModel> Pids { get; set; } = [];

    /// <summary>
    /// Gets or sets the unique identifier for the service.
    /// </summary>
    public byte ServiceId { get; set; }
}

/// <summary>
/// Represents a parsed field definition.
/// </summary>
internal sealed class UdsFieldModel
{
    /// <summary>
    /// Gets or sets the identifier of the entity or resource to which this item applies.
    /// </summary>
    public string? AppliesTo { get; set; }

    /// <summary>
    /// Gets or sets the number of elements contained in the collection.
    /// </summary>
    public int ElementCount { get; set; }

    /// <summary>
    /// Gets or sets the length of each element in the collection.
    /// </summary>
    public int ElementLength { get; set; }

    /// <summary>
    /// Gets or sets the type of the field as a string.
    /// </summary>
    public string FieldType { get; set; } = "";

    /// <summary>
    /// Gets or sets the sequence number of the current frame.
    /// </summary>
    public int FrameSequence { get; set; }

    /// <summary>
    /// Gets or sets the source identifier for the frame content.
    /// </summary>
    public string FrameSource { get; set; } = "Payload";

    /// <summary>
    /// Gets or sets a value indicating whether the current instance represents an array type.
    /// </summary>
    public bool IsArray { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the value of this property is computed rather than directly assigned.
    /// </summary>
    public bool IsComputed { get; set; }

    /// <summary>
    /// Gets or sets the length value associated with the current instance.
    /// </summary>
    public int Length { get; set; }

    /// <summary>
    /// Gets or sets the offset value used to adjust the starting position for an operation.
    /// </summary>
    public int Offset { get; set; }

    /// <summary>
    /// Gets or sets the name of the property.
    /// </summary>
    public string PropertyName { get; set; } = "";

    /// <summary>
    /// Gets or sets the type name of the property as a string.
    /// </summary>
    public string PropertyType { get; set; } = "";

    /// <summary>
    /// Gets or sets the scale factor applied to the object.
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// Gets or sets the valid range for the associated value, expressed as a string.
    /// </summary>
    /// <remarks>The format and interpretation of the range string may vary depending on the context in which
    /// this property is used. If no range is specified, the property value may be null.</remarks>
    public string? ValidRange { get; set; }
}
