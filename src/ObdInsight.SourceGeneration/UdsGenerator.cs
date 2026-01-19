using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using ObdInsight.SourceGeneration.Models;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace ObdInsight.SourceGeneration;

/// <summary>
/// Source generator for UDS (Unified Diagnostic Services) message definitions.
/// </summary>
[Generator]
public class UdsGenerator : IIncrementalGenerator
{
    private const string UdsServiceAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsServiceAttribute";
    private const string UdsPidAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsPidAttribute";
    private const string UdsFieldAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsFieldAttribute";
    private const string UdsArrayFieldAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsArrayFieldAttribute";
    private const string UdsComputedAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsComputedAttribute";
    private const string UdsResponseAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsResponseAttribute";
    private const string UdsResponseVariantAttributeName = "ObdInsight.SourceGeneration.Attributes.UdsResponseVariantAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all classes with [UdsService] attribute
        var serviceProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsServiceCandidate(node),
                transform: static (ctx, _) => GetServiceModel(ctx))
            .Where(static m => m is not null);

        // Generate code for each service
        context.RegisterSourceOutput(serviceProvider, static (spc, service) =>
        {
            if (service is null) return;
            
            var source = GenerateServiceCode(service);
            spc.AddSource($"{service.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }

    private static bool IsServiceCandidate(SyntaxNode node)
    {
        return node is ClassDeclarationSyntax classDecl
            && classDecl.AttributeLists.Count > 0
            && classDecl.Modifiers.Any(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword);
    }

    private static UdsServiceModel? GetServiceModel(GeneratorSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.Node;
        var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl);
        
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
                ClassName = nestedType.Name,
                PidId = (byte)(pidAttr.ConstructorArguments[0].Value ?? 0x01)
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

    private static UdsFieldModel ParseField(IPropertySymbol property, AttributeData fieldAttr)
    {
        var model = new UdsFieldModel
        {
            PropertyName = property.Name,
            PropertyType = property.Type.ToDisplayString()
        };

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
                        model.FieldType = ((ObdInsight.SourceGeneration.Attributes.UdsFieldType)enumIntValue).ToString();
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
                    model.FrameSource = namedArg.Value.Value?.ToString() ?? "Payload";
                    break;
                case "FrameSequence":
                    model.FrameSequence = (int)(namedArg.Value.Value ?? 0);
                    break;
            }
        }

        return model;
    }

    private static UdsFieldModel ParseArrayField(IPropertySymbol property, AttributeData arrayFieldAttr)
    {
        var model = new UdsFieldModel
        {
            PropertyName = property.Name,
            PropertyType = property.Type.ToDisplayString(),
            IsArray = true
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
                        model.FieldType = ((ObdInsight.SourceGeneration.Attributes.UdsFieldType)enumIntValue).ToString();
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

    private static string GenerateServiceCode(UdsServiceModel model)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
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

    private static void GenerateQueryMethod(StringBuilder sb, UdsServiceModel service, UdsPidModel pid)
    {
        var methodName = $"Query{pid.MethodName}Async";
        
        sb.AppendLine($"    public async System.Threading.Tasks.Task<{pid.ClassName}?> {methodName}(System.Threading.CancellationToken ct = default)");
        sb.AppendLine("    {");
        
        // Send UDS request
        sb.AppendLine($"        var lines = await _session.QueryAsync(\"{service.ServiceId:X2}{pid.PidId:X2}\", _context, ct);");
        sb.AppendLine();
        
        // Parse ISO-TP frames
        sb.AppendLine("        var frames = ParseIsoTpFrames(lines);");
        sb.AppendLine("        if (frames.Count == 0) return null;");
        sb.AppendLine();
        
        // Reassemble payload
        sb.AppendLine("        var payload = ReassembleIsoTpPayload(frames);");
        sb.AppendLine();
        
        // Validate header
        var expectedResponse = service.ServiceId + 0x40;
        sb.AppendLine($"        if (payload.Length < 2 || payload[0] != 0x{expectedResponse:X2} || payload[1] != 0x{pid.PidId:X2})");
        sb.AppendLine("            return null;");
        sb.AppendLine();
        
        // Detect variant if applicable
        if (pid.Variants.Count > 0)
        {
            sb.AppendLine("        var data = payload.AsSpan(2);");
            sb.AppendLine("        var variant = data.Length switch");
            sb.AppendLine("        {");
            foreach (var variant in pid.Variants)
            {
                sb.AppendLine($"            {variant.Length} => \"{variant.Model}\",");
            }
            sb.AppendLine("            _ => null");
            sb.AppendLine("        };");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("        var data = payload.AsSpan(2);");
            sb.AppendLine("        string? variant = null;");
            sb.AppendLine();
        }
        
        sb.AppendLine($"        var response = new {pid.ClassName}();");
        sb.AppendLine();
        
        // Generate field extraction code
        foreach (var field in pid.Fields.Where(f => !f.IsComputed))
        {
            if (field.IsArray)
            {
                GenerateArrayFieldExtraction(sb, field);
            }
            else
            {
                GenerateFieldExtraction(sb, field, pid.Variants.Count > 0);
            }
        }
        
        sb.AppendLine("        return response;");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void GenerateFieldExtraction(StringBuilder sb, UdsFieldModel field, bool hasVariants)
    {
        // Check variant applicability
        if (!string.IsNullOrEmpty(field.AppliesTo) && hasVariants)
        {
            var variants = field.AppliesTo.Split(',').Select(v => v.Trim());
            var condition = string.Join(" || ", variants.Select(v => $"variant == \"{v}\""));
            sb.AppendLine($"        if ({condition})");
            sb.AppendLine("        {");
        }

        // Check data availability based on frame source
        if (field.FrameSource == "ConsecutiveFrame")
        {
            sb.AppendLine($"        var cf{field.FrameSequence} = frames.FirstOrDefault(f => f.FrameType == 2 && f.SeqOrLen == {field.FrameSequence}).Data;");
            sb.AppendLine($"        if (cf{field.FrameSequence}?.Length >= {field.Offset + field.Length})");
            sb.AppendLine("        {");
            GenerateValueExtraction(sb, field, $"cf{field.FrameSequence}", "            ");
            sb.AppendLine("        }");
        }
        else
        {
            sb.AppendLine($"        if (data.Length >= {field.Offset + field.Length})");
            sb.AppendLine("        {");
            GenerateValueExtraction(sb, field, "data", "            ");
            sb.AppendLine("        }");
        }

        if (!string.IsNullOrEmpty(field.AppliesTo) && hasVariants)
        {
            sb.AppendLine("        }");
        }
        
        sb.AppendLine();
    }

    private static void GenerateValueExtraction(StringBuilder sb, UdsFieldModel field, string dataVar, string indent)
    {
        var offset = field.Offset;
        var rawVarName = $"{field.PropertyName.ToLower()}Raw";
        
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
                sb.AppendLine($"{indent}var {rawVarName} = ({dataVar}[{offset}] << 16) | ({dataVar}[{offset + 1}] << 8) | {dataVar}[{offset + 2}];");
                break;
                
            case "UInt32BE":
                sb.AppendLine($"{indent}var {rawVarName} = ((uint){dataVar}[{offset}] << 24) | ((uint){dataVar}[{offset + 1}] << 16) | ((uint){dataVar}[{offset + 2}] << 8) | {dataVar}[{offset + 3}];");
                break;
                
            case "Int32BE":
                sb.AppendLine($"{indent}var {rawVarName}Unsigned = ((uint){dataVar}[{offset}] << 24) | ((uint){dataVar}[{offset + 1}] << 16) | ((uint){dataVar}[{offset + 2}] << 8) | {dataVar}[{offset + 3}];");
                sb.AppendLine($"{indent}var {rawVarName} = unchecked((int){rawVarName}Unsigned);");
                break;
                
            default:
                // Fallback - generate a comment for debugging
                sb.AppendLine($"{indent}// TODO: Unsupported field type '{field.FieldType}' for {field.PropertyName}");
                sb.AppendLine($"{indent}var {rawVarName} = 0;");
                break;
        }
        
        // Apply scaling
        var isNullable = field.PropertyType.EndsWith("?");
        if (Math.Abs(field.Scale - 1.0) > 0.0001)
        {
            sb.AppendLine($"{indent}var value = {rawVarName} * {field.Scale};");
        }
        else
        {
            sb.AppendLine($"{indent}var value = ({field.PropertyType.TrimEnd('?')}){rawVarName};");
        }
        
        // Apply range validation if specified
        if (!string.IsNullOrEmpty(field.ValidRange))
        {
            var parts = field.ValidRange.Split(new[] { ".." }, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                sb.AppendLine($"{indent}if (value >= {parts[0]} && value <= {parts[1]})");
                sb.AppendLine($"{indent}    response.{field.PropertyName} = value;");
            }
        }
        else
        {
            sb.AppendLine($"{indent}response.{field.PropertyName} = value;");
        }
    }

    private static void GenerateArrayFieldExtraction(StringBuilder sb, UdsFieldModel field)
    {
        sb.AppendLine($"        var {field.PropertyName.ToLower()}List = new System.Collections.Generic.List<{GetElementType(field.PropertyType)}>();");
        sb.AppendLine($"        for (int i = {field.Offset}; i + {field.ElementLength - 1} < data.Length && {field.PropertyName.ToLower()}List.Count < {field.ElementCount}; i += {field.ElementLength})");
        sb.AppendLine("        {");
        
        // Generate element extraction based on type
        switch (field.FieldType)
        {
            case "UInt8":
                sb.AppendLine("            var value = data[i];");
                break;
                
            case "UInt16BE":
                sb.AppendLine("            var value = (data[i] << 8) | data[i + 1];");
                break;
                
            case "UInt24BE":
                sb.AppendLine("            var value = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];");
                break;
                
            case "UInt32BE":
                sb.AppendLine("            var value = ((uint)data[i] << 24) | ((uint)data[i + 1] << 16) | ((uint)data[i + 2] << 8) | data[i + 3];");
                break;
                
            default:
                sb.AppendLine($"            // TODO: Unsupported array element type '{field.FieldType}'");
                sb.AppendLine("            var value = 0;");
                break;
        }
        
        // Apply range validation if specified
        if (!string.IsNullOrEmpty(field.ValidRange))
        {
            var parts = field.ValidRange.Split(new[] { ".." }, StringSplitOptions.None);
            if (parts.Length == 2)
            {
                sb.AppendLine($"            if (value >= {parts[0]} && value <= {parts[1]})");
                sb.AppendLine($"                {field.PropertyName.ToLower()}List.Add(value);");
            }
        }
        else
        {
            sb.AppendLine($"            {field.PropertyName.ToLower()}List.Add(value);");
        }
        
        sb.AppendLine("        }");
        sb.AppendLine($"        response.{field.PropertyName} = {field.PropertyName.ToLower()}List.ToArray();");
        sb.AppendLine();
    }

    private static string GetElementType(string arrayType)
    {
        // Extract element type from array type (e.g., "int[]" -> "int")
        return arrayType.Replace("[]", "").Replace("System.", "");
    }
}
