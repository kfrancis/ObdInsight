using System;
using System.Linq;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ObdInsight.SourceGeneration.Attributes;
using ObdInsight.SourceGeneration.Models;

namespace ObdInsight.SourceGeneration;

/// <summary>
///     Source generator for UDS (Unified Diagnostic Services) message definitions.
/// </summary>
[Generator]
public class UdsGenerator : IIncrementalGenerator
{
    private const string UdsArrayFieldAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsArrayFieldAttribute";
    private const string UdsComputedAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsComputedAttribute";
    private const string UdsFieldAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsFieldAttribute";
    private const string UdsPidAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsPidAttribute";
    private const string UdsResponseAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsResponseAttribute";

    private const string UdsResponseVariantAttributeName =
        "ObdInsight.SourceGeneration.Attributes.UdsResponseVariantAttribute";

    private const string UdsServiceAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsServiceAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all classes with [UdsService] attribute
        var serviceProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsServiceCandidate(node),
                static (ctx, _) => GetServiceModel(ctx))
            .Where(static m => m is not null);

        // Generate code for each service
        context.RegisterSourceOutput(serviceProvider, static (spc, service) =>
        {
            if (service is null) return;

            var error = ValidateService(service);
            if (error is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(InvalidSchema, Location.None, error));
                return;
            }
            var source = GenerateServiceCode(service);
            spc.AddSource($"{service.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }

    private static readonly DiagnosticDescriptor InvalidSchema = new(
        "OBDUDS001", "Invalid UDS schema", "{0}", "ObdInsight", DiagnosticSeverity.Error, true);

    private static int Width(string type) => type switch
    {
        "UInt8" => 1, "UInt16BE" => 2, "UInt24BE" => 3,
        "UInt32BE" or "Int32BE" => 4, _ => 0
    };

    private static string? ValidateService(UdsServiceModel service)
    {
        foreach (var pid in service.Pids)
        {
            if (pid.MinLength is < 0 or > 4093 || pid.MaxLength is < 0 or > 4093 ||
                (pid.MaxLength != 0 && pid.MaxLength < pid.MinLength))
                return $"{pid.ClassName}: invalid response bounds.";
            if (pid.Variants.GroupBy(v => v.Length).Any(g => g.Count() > 1) ||
                pid.Variants.Any(v => v.Length < pid.MinLength ||
                    (pid.MaxLength > 0 && v.Length > pid.MaxLength) || string.IsNullOrWhiteSpace(v.Model)))
                return $"{pid.ClassName}: ambiguous or out-of-bounds variants.";
            foreach (var group in pid.Fields.Where(f => !f.IsComputed && !f.IsArray && !f.PropertyType.EndsWith("?")).GroupBy(f => f.PropertyName))
            {
                if (group.All(f => !string.IsNullOrEmpty(f.AppliesTo)) &&
                    pid.Variants.Any(v => !group.Any(f => f.AppliesTo!.Split(',').Any(a => a.Trim() == v.Model))))
                    return $"{pid.ClassName}.{group.Key}: variant-optional fields must be nullable.";
            }
            foreach (var field in pid.Fields.Where(f => !f.IsComputed))
            {
                var valueType = (field.IsArray ? GetElementType(field.PropertyType) : field.PropertyType).TrimEnd('?');
                if (valueType is not ("byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" or "decimal"))
                    return $"{pid.ClassName}.{field.PropertyName}: unsupported numeric property type.";
                var width = Width(field.FieldType);
                if (width == 0 || (field.IsArray && field.FieldType == "Int32BE"))
                    return $"{pid.ClassName}.{field.PropertyName}: unsupported field type {field.FieldType}.";
                if (field.Offset < 0 || (field.IsArray
                    ? field.ElementLength != width || field.ElementCount <= 0 ||
                      (long)field.Offset + (long)field.ElementCount * width > 4093
                    : field.Length != width || (long)field.Offset + width > 4093))
                    return $"{pid.ClassName}.{field.PropertyName}: invalid field geometry.";
                if (field.FrameSource is not ("Payload" or "FirstFrame" or "ConsecutiveFrame") ||
                    (field.FrameSource == "ConsecutiveFrame" && field.FrameSequence is < 0 or > 15) ||
                    (field.FrameSource != "Payload" && field.Offset + width >
                        (field.FrameSource == "FirstFrame" ? 6 : 7)))
                    return $"{pid.ClassName}.{field.PropertyName}: unsupported frame source geometry.";
                if (double.IsNaN(field.Scale) || double.IsInfinity(field.Scale))
                    return $"{pid.ClassName}.{field.PropertyName}: scale must be finite.";
                if (!string.IsNullOrEmpty(field.ValidRange) && !TryRange(field.ValidRange!, out _, out _))
                    return $"{pid.ClassName}.{field.PropertyName}: invalid numeric range.";
                if (!string.IsNullOrEmpty(field.AppliesTo) &&
                    field.AppliesTo!.Split(',').Any(v => !pid.Variants.Any(p => p.Model == v.Trim())))
                    return $"{pid.ClassName}.{field.PropertyName}: unknown variant.";
            }
        }
        return null;
    }

    private static bool TryRange(string range, out double min, out double max)
    {
        var parts = range.Split(new[] { ".." }, StringSplitOptions.None);
        min = max = 0;
        return parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out min) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out max) &&
            !double.IsNaN(min) && !double.IsInfinity(min) &&
            !double.IsNaN(max) && !double.IsInfinity(max) && min <= max;
    }

    private static void GenerateArrayFieldExtraction(StringBuilder sb, UdsFieldModel field)
    {
        var elementType = GetElementType(field.PropertyType);
        var name = field.PropertyName.ToLowerInvariant() + "Values";
        sb.AppendLine($"        if (data.Length < {field.Offset + field.ElementCount * field.ElementLength}) return Invalid();");
        sb.AppendLine($"        var {name} = new {elementType}[{field.ElementCount}];");
        sb.AppendLine($"        for (int index = 0; index < {field.ElementCount}; index++)");
        sb.AppendLine("        {");
        sb.AppendLine($"            int i = {field.Offset} + index * {field.ElementLength};");
        var expression = field.FieldType switch
        {
            "UInt8" => "data[i]",
            "UInt16BE" => "(data[i] << 8) | data[i + 1]",
            "UInt24BE" => "(data[i] << 16) | (data[i + 1] << 8) | data[i + 2]",
            _ => "((uint)data[i] << 24) | ((uint)data[i + 1] << 16) | ((uint)data[i + 2] << 8) | data[i + 3]"
        };
        sb.AppendLine($"            var value = {expression};");
        if (!string.IsNullOrEmpty(field.ValidRange))
        {
            TryRange(field.ValidRange!, out var min, out var max);
            sb.AppendLine($"            if (value < {min.ToString("R", CultureInfo.InvariantCulture)}d || value > {max.ToString("R", CultureInfo.InvariantCulture)}d)");
            sb.AppendLine(elementType.EndsWith("?") ? "                continue; // Preserve this index as missing." : "                return Invalid();");
        }
        var numericType = elementType.TrimEnd('?');
        sb.AppendLine($"            if ((double)value < (double){numericType}.MinValue || (double)value > (double){numericType}.MaxValue) return Invalid();");
        sb.AppendLine($"            {name}[index] = ({elementType})value;");
        sb.AppendLine("        }");
        sb.AppendLine($"        response.{field.PropertyName} = {name};");
        sb.AppendLine();
    }

    private static void GenerateFieldExtraction(StringBuilder sb, UdsFieldModel field, bool hasVariants)
    {
        // A separate scope also permits several fields from the same frame.
        if (!string.IsNullOrEmpty(field.AppliesTo) && hasVariants)
        {
            var condition = string.Join(" || ", field.AppliesTo!.Split(',').Select(v => $"variant == \"{v.Trim()}\""));
            sb.AppendLine($"        if ({condition})");
        }
        sb.AppendLine("        {");
        var source = "data";
        if (field.FrameSource != "Payload")
        {
            var start = field.FrameSource == "FirstFrame" ? 0 : 6 + ((field.FrameSequence == 0 ? 16 : field.FrameSequence) - 1) * 7;
            sb.AppendLine($"            if (payload.Length <= 7 || payload.Length < {start + field.Offset + field.Length}) return Invalid();");
            sb.AppendLine($"            var frameData = payload.AsSpan({start}, System.Math.Min({(field.FrameSource == "FirstFrame" ? 6 : 7)}, payload.Length - {start}));");
            source = "frameData";
        }
        sb.AppendLine($"            if ({source}.Length < {field.Offset + field.Length}) return Invalid();");
        GenerateValueExtraction(sb, field, source, "            ");
        sb.AppendLine("        }");
        sb.AppendLine();
    }

    private static void GenerateQueryMethod(StringBuilder sb, UdsServiceModel service, UdsPidModel pid)
    {
        sb.AppendLine($"    public async System.Threading.Tasks.Task<global::ObdInsight.Core.Protocols.Observed<{pid.ClassName}?>> Query{pid.MethodName}Async(System.Threading.CancellationToken ct = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        ct.ThrowIfCancellationRequested();");
        sb.AppendLine($"        var reply = await _session.QueryResponseAsync(\"{service.ServiceId:X2}{pid.PidId:X2}\", _context, ct).ConfigureAwait(false);");
        sb.AppendLine("        var lines = reply.Value;");
        sb.AppendLine($"        global::ObdInsight.Core.Protocols.Observed<{pid.ClassName}?> Invalid() => new(null, reply.Observation with {{ Quality = global::ObdInsight.Core.Protocols.ObservationQuality.Invalid }});");
        sb.AppendLine("        ct.ThrowIfCancellationRequested();");
        sb.AppendLine($"        if (!global::ObdInsight.Core.Protocols.IsoTpParser.TryReadPayload(lines, out var payload, _context.RxFilter, \"{service.ServiceId:X2}{pid.PidId:X2}\")) return Invalid();");
        sb.AppendLine($"        if (payload.Length < 2 || payload[0] != 0x{service.ServiceId + 0x40:X2} || payload[1] != 0x{pid.PidId:X2}) return Invalid();");
        sb.AppendLine("        var data = payload.AsSpan(2);");
        if (pid.MinLength > 0) sb.AppendLine($"        if (data.Length < {pid.MinLength}) return Invalid();");
        if (pid.MaxLength > 0) sb.AppendLine($"        if (data.Length > {pid.MaxLength}) return Invalid();");
        if (pid.Variants.Count > 0)
        {
            sb.AppendLine("        string? variant = data.Length switch");
            sb.AppendLine("        {");
            foreach (var variant in pid.Variants)
                sb.AppendLine($"            {variant.Length} => \"{variant.Model}\",");
            sb.AppendLine("            _ => null");
            sb.AppendLine("        };");
            sb.AppendLine("        if (variant is null) return Invalid();");
        }
        sb.AppendLine($"        var response = new {pid.ClassName}();");
        foreach (var name in pid.Fields.Where(f => !f.IsComputed && !f.IsArray && f.PropertyType.EndsWith("?")).Select(f => f.PropertyName).Distinct())
            sb.AppendLine($"        response.{name} = null;");
        foreach (var field in pid.Fields.Where(f => !f.IsComputed))
        {
            if (field.IsArray) GenerateArrayFieldExtraction(sb, field);
            else GenerateFieldExtraction(sb, field, pid.Variants.Count > 0);
        }
        sb.AppendLine($"        return new global::ObdInsight.Core.Protocols.Observed<{pid.ClassName}?>(response, reply.Observation);");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static string GenerateServiceCode(UdsServiceModel model)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("using System;");
        sb.AppendLine();
        sb.AppendLine($"namespace {model.Namespace};");
        sb.AppendLine();
        sb.AppendLine($"partial class {model.ClassName}");
        sb.AppendLine("{");

        foreach (var pid in model.Pids)
        {
            GenerateQueryMethod(sb, model, pid);
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void GenerateValueExtraction(StringBuilder sb, UdsFieldModel field, string dataVar, string indent)
    {
        var offset = field.Offset;
        var rawVarName = $"{field.PropertyName.ToLowerInvariant()}Raw";

        // Generate extraction based on type
        switch (field.FieldType)
        {
            case "UInt8":
                sb.AppendLine($"{indent}var {rawVarName} = {dataVar}[{offset}];");
                break;

            case "UInt16BE":
                sb.AppendLine($"{indent}var {rawVarName} = ({dataVar}[{offset}] << 8) | {dataVar}[{offset + 1}];");
                break;

            case "UInt24BE":
                sb.AppendLine(
                    $"{indent}var {rawVarName} = ({dataVar}[{offset}] << 16) | ({dataVar}[{offset + 1}] << 8) | {dataVar}[{offset + 2}];");
                break;

            case "UInt32BE":
                sb.AppendLine(
                    $"{indent}var {rawVarName} = ((uint){dataVar}[{offset}] << 24) | ((uint){dataVar}[{offset + 1}] << 16) | ((uint){dataVar}[{offset + 2}] << 8) | {dataVar}[{offset + 3}];");
                break;

            case "Int32BE":
                sb.AppendLine(
                    $"{indent}var {rawVarName}Unsigned = ((uint){dataVar}[{offset}] << 24) | ((uint){dataVar}[{offset + 1}] << 16) | ((uint){dataVar}[{offset + 2}] << 8) | {dataVar}[{offset + 3}];");
                sb.AppendLine($"{indent}var {rawVarName} = unchecked((int){rawVarName}Unsigned);");
                break;

            default:
                throw new InvalidOperationException("Unsupported field passed schema validation.");
        }

        // Apply scaling
        var isNullable = field.PropertyType.EndsWith("?");
        if (field.Scale != 1.0)
        {
            sb.AppendLine($"{indent}var value = {rawVarName} * {field.Scale.ToString("R", CultureInfo.InvariantCulture)}d;");
        }
        else
        {
            sb.AppendLine($"{indent}var value = (double){rawVarName};");
        }

        var targetType = field.PropertyType.TrimEnd('?');
        sb.AppendLine($"{indent}if (!double.IsFinite(value) || value < (double){targetType}.MinValue || value > (double){targetType}.MaxValue) return Invalid();");
        sb.AppendLine($"{indent}{targetType} converted;");
        sb.AppendLine($"{indent}try {{ converted = checked(({targetType})value); }}");
        sb.AppendLine($"{indent}catch (System.OverflowException) {{ return Invalid(); }}");
        if (!string.IsNullOrEmpty(field.ValidRange))
        {
            TryRange(field.ValidRange!, out var min, out var max);
            sb.AppendLine($"{indent}if (value >= {min.ToString("R", CultureInfo.InvariantCulture)}d && value <= {max.ToString("R", CultureInfo.InvariantCulture)}d)");
            sb.AppendLine($"{indent}    response.{field.PropertyName} = converted;");
            if (!isNullable) sb.AppendLine($"{indent}else return Invalid();");
        }
        else
        {
            sb.AppendLine($"{indent}response.{field.PropertyName} = converted;");
        }
    }

    private static string GetElementType(string arrayType)
    {
        // Extract element type from array type (e.g., "int[]" -> "int")
        var type = arrayType.EndsWith("[]?") ? arrayType.Substring(0, arrayType.Length - 1) : arrayType;
        return type.Replace("[]", "").Replace("System.", "");
    }

    private static UdsServiceModel? GetServiceModel(GeneratorSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var symbol = ModelExtensions.GetDeclaredSymbol(context.SemanticModel, classDecl);

        if (symbol is not INamedTypeSymbol classSymbol)
            return null;

        // Check for [UdsService] attribute
        var serviceAttr = classSymbol.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == UdsServiceAttributeName);

        if (serviceAttr is null)
            return null;

        var model = new UdsServiceModel
        {
            ClassName = classSymbol.Name,
            Namespace = classSymbol.ContainingNamespace.ToDisplayString(),
            ServiceId = (byte)(serviceAttr.ConstructorArguments[0].Value ?? 0x21)
        };

        // Extract optional properties
        foreach (var namedArg in serviceAttr.NamedArguments)
        {
            switch (namedArg.Key)
            {
                case "EcuType":
                    model.EcuType = namedArg.Value.Value?.ToString();
                    break;
                case "Description":
                    model.Description = namedArg.Value.Value?.ToString();
                    break;
            }
        }

        // Find nested PID response classes
        foreach (var nestedType in classSymbol.GetTypeMembers())
        {
            var pidAttr = nestedType.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == UdsPidAttributeName);

            if (pidAttr is null)
                continue;

            var pidModel = new UdsPidModel
            {
                ClassName = nestedType.Name, PidId = (byte)(pidAttr.ConstructorArguments[0].Value ?? 0x01)
            };

            // Extract method name
            foreach (var namedArg in pidAttr.NamedArguments)
            {
                if (namedArg.Key == "Name")
                    pidModel.MethodName = namedArg.Value.Value?.ToString();
            }

            // Default method name from class name
            if (string.IsNullOrEmpty(pidModel.MethodName))
            {
                pidModel.MethodName = pidModel.ClassName.Replace("Response", "");
            }

            // Extract response metadata
            var responseAttrs = nestedType.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == UdsResponseAttributeName);

            foreach (var responseAttr in responseAttrs)
            {
                foreach (var namedArg in responseAttr.NamedArguments)
                {
                    switch (namedArg.Key)
                    {
                        case "MinLength":
                            pidModel.MinLength = (int)(namedArg.Value.Value ?? 0);
                            break;
                        case "MaxLength":
                            pidModel.MaxLength = (int)(namedArg.Value.Value ?? 0);
                            break;
                    }
                }
            }

            // Extract variants
            var variantAttrs = nestedType.GetAttributes()
                .Where(a => a.AttributeClass?.ToDisplayString() == UdsResponseVariantAttributeName);

            foreach (var variantAttr in variantAttrs)
            {
                var variant = new ResponseVariant();
                foreach (var namedArg in variantAttr.NamedArguments)
                {
                    switch (namedArg.Key)
                    {
                        case "Length":
                            variant.Length = (int)(namedArg.Value.Value ?? 0);
                            break;
                        case "Model":
                            variant.Model = namedArg.Value.Value?.ToString() ?? "";
                            break;
                    }
                }

                pidModel.Variants.Add(variant);
            }

            // Extract fields from properties
            foreach (var member in nestedType.GetMembers())
            {
                if (member is not IPropertySymbol property)
                    continue;

                var isComputed = property.GetAttributes()
                    .Any(a => a.AttributeClass?.ToDisplayString() == UdsComputedAttributeName);

                if (isComputed)
                {
                    pidModel.Fields.Add(new UdsFieldModel
                    {
                        PropertyName = property.Name,
                        PropertyType = property.Type.ToDisplayString(),
                        IsComputed = true
                    });
                    continue;
                }

                // Check for array field
                var arrayFieldAttr = property.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == UdsArrayFieldAttributeName);

                if (arrayFieldAttr is not null)
                {
                    var fieldModel = ParseArrayField(property, arrayFieldAttr);
                    pidModel.Fields.Add(fieldModel);
                    continue;
                }

                // Check for regular field (may have multiple for variants)
                var fieldAttrs = property.GetAttributes()
                    .Where(a => a.AttributeClass?.ToDisplayString() == UdsFieldAttributeName);

                foreach (var fieldAttr in fieldAttrs)
                {
                    var fieldModel = ParseField(property, fieldAttr);
                    pidModel.Fields.Add(fieldModel);
                }
            }

            model.Pids.Add(pidModel);
        }

        return model.Pids.Count > 0 ? model : null;
    }

    private static bool IsServiceCandidate(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax classDecl
               && classDecl.AttributeLists.Count > 0
               && classDecl.Modifiers.Any(SyntaxKind.PartialKeyword);
    }

    private static UdsFieldModel ParseArrayField(IPropertySymbol property, AttributeData arrayFieldAttr)
    {
        var model = new UdsFieldModel
        {
            PropertyName = property.Name, PropertyType = property.Type.ToDisplayString(), IsArray = true, FieldType = "UInt8"
        };

        foreach (var namedArg in arrayFieldAttr.NamedArguments)
        {
            switch (namedArg.Key)
            {
                case "Offset":
                    model.Offset = (int)(namedArg.Value.Value ?? 0);
                    break;
                case "ElementCount":
                    model.ElementCount = (int)(namedArg.Value.Value ?? 0);
                    break;
                case "ElementLength":
                    model.ElementLength = (int)(namedArg.Value.Value ?? 0);
                    break;
                case "Type":
                    // Handle enum value - could be string name or numeric value
                    var typeValue = namedArg.Value.Value;
                    if (typeValue is int enumIntValue)
                    {
                        // Convert numeric enum value to string name
                        model.FieldType = ((UdsFieldType)enumIntValue).ToString();
                    }
                    else
                    {
                        model.FieldType = typeValue?.ToString() ?? "UInt8";
                    }

                    break;
                case "ValidRange":
                    model.ValidRange = namedArg.Value.Value?.ToString();
                    break;
            }
        }

        return model;
    }

    private static UdsFieldModel ParseField(IPropertySymbol property, AttributeData fieldAttr)
    {
        var model = new UdsFieldModel { PropertyName = property.Name, PropertyType = property.Type.ToDisplayString(), FieldType = "UInt8" };

        foreach (var namedArg in fieldAttr.NamedArguments)
        {
            switch (namedArg.Key)
            {
                case "Offset":
                    model.Offset = (int)(namedArg.Value.Value ?? 0);
                    break;
                case "Length":
                    model.Length = (int)(namedArg.Value.Value ?? 0);
                    break;
                case "Type":
                    // Handle enum value - could be string name or numeric value
                    var typeValue = namedArg.Value.Value;
                    if (typeValue is int enumIntValue)
                    {
                        // Convert numeric enum value to string name
                        model.FieldType = ((UdsFieldType)enumIntValue).ToString();
                    }
                    else
                    {
                        model.FieldType = typeValue?.ToString() ?? "UInt8";
                    }

                    break;
                case "Scale":
                    model.Scale = (double)(namedArg.Value.Value ?? 1.0);
                    break;
                case "ValidRange":
                    model.ValidRange = namedArg.Value.Value?.ToString();
                    break;
                case "AppliesTo":
                    model.AppliesTo = namedArg.Value.Value?.ToString();
                    break;
                case "FrameType":
                    // Enum named arguments arrive as their boxed underlying int — convert to the
                    // member name, otherwise the "ConsecutiveFrame" comparison never matches.
                    var frameSourceValue = namedArg.Value.Value;
                    if (frameSourceValue is int frameSourceInt)
                    {
                        model.FrameSource = ((FrameSource)frameSourceInt).ToString();
                    }
                    else
                    {
                        model.FrameSource = frameSourceValue?.ToString() ?? "Payload";
                    }

                    break;
                case "FrameSequence":
                    model.FrameSequence = (int)(namedArg.Value.Value ?? 0);
                    break;
            }
        }

        return model;
    }
}
