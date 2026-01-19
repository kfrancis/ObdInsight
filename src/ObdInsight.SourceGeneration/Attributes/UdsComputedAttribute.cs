using System;

namespace ObdInsight.SourceGeneration.Attributes;

/// <summary>
/// Marks a property as computed (not extracted from payload).
/// The generator will skip generating extraction code for this property.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class UdsComputedAttribute : Attribute
{
}
