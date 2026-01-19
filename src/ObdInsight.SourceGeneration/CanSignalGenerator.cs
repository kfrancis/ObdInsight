using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif

namespace ObdInsight.SourceGeneration
{
    /// <summary>
    /// Generates strongly-typed CAN frame decoder classes and bit manipulation helpers at compile time for classes
    /// annotated with the [CanFrame] attribute.
    /// </summary>
    /// <remarks>This incremental source generator scans for partial classes marked with [CanFrame] and their
    /// partial properties marked with [CanSignal], then generates code to parse raw 8-byte CAN frame data into
    /// strongly-typed objects. It also emits a CanBits helper class per namespace for efficient bit extraction. The
    /// generator is intended for use in projects that require type-safe decoding of CAN bus messages. Thread safety and
    /// performance are determined by the generated code and typical usage patterns.</remarks>
    [Generator]
    public class CanSignalGenerator : IIncrementalGenerator
    {
        /// <summary>
        /// Initializes the incremental source generator by registering syntax providers and source outputs for classes
        /// marked with the [CanFrame] attribute.
        /// </summary>
        /// <remarks>This method configures the generator to produce source code for each class annotated
        /// with the [CanFrame] attribute and to generate a CanBits helper file for each namespace containing such
        /// classes. It should be called from the generator's initialization entry point.</remarks>
        /// <param name="context">The context for generator initialization, used to register syntax providers and source outputs.</param>
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // Find all classes with [CanFrame] attribute
            var canFrameClasses = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => IsCanFrameCandidate(node),
                    transform: static (ctx, _) => GetCanFrameModel(ctx))
                .Where(static m => m is not null)
                .Select(static (m, _) => m!);

            // Generate source for each frame class
            context.RegisterSourceOutput(canFrameClasses, static (spc, model) =>
            {
                var source = GenerateCanFrameDecoder(model);
                spc.AddSource($"{model.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
            });

            // Generate CanBits helper once per namespace  
            var firstFramePerNamespace = canFrameClasses
                .Collect()
                .SelectMany(static (frames, _) =>
                {
                    var grouped = frames
                        .GroupBy(f => f.Namespace)
                        .Select(g => (Namespace: g.Key, FirstFrame: g.First()))
                        .ToImmutableArray();
                    return grouped;
                });

            context.RegisterSourceOutput(firstFramePerNamespace, static (spc, item) =>
            {
                var source = GenerateCanBitsFile(item.Namespace);
                spc.AddSource($"CanBits_{item.Namespace.Replace(".", "_")}.g.cs", SourceText.From(source, Encoding.UTF8));
            });

            // Generate CanFrameRouter once per namespace
            var framesGroupedByNamespace = canFrameClasses
                .Collect()
                .SelectMany(static (frames, _) =>
                {
                    var grouped = frames
                        .GroupBy(f => f.Namespace)
                        .Select(g => (Namespace: g.Key, Frames: g.ToImmutableArray()))
                        .ToImmutableArray();
                    return grouped;
                });

            context.RegisterSourceOutput(framesGroupedByNamespace, static (spc, item) =>
            {
                var source = GenerateCanFrameRouterFile(item.Namespace, item.Frames);
                spc.AddSource($"CanFrameRouter_{item.Namespace.Replace(".", "_")}.g.cs", SourceText.From(source, Encoding.UTF8));
            });
        }

        /// <summary>
        /// Determines whether the specified syntax node represents a partial class declaration with at least one
        /// attribute applied.
        /// </summary>
        /// <param name="node">The syntax node to evaluate as a potential candidate.</param>
        /// <returns>true if the node is a partial class declaration with one or more attributes; otherwise, false.</returns>
        private static bool IsCanFrameCandidate(SyntaxNode node)
        {
            return node is ClassDeclarationSyntax classDecl
                && classDecl.Modifiers.Any(SyntaxKind.PartialKeyword)
                && classDecl.AttributeLists.Count > 0;
        }

        /// <summary>
        /// Attempts to create a CAN frame model from a class declaration node annotated with the CanFrame attribute.
        /// </summary>
        /// <remarks>The method expects the class to be decorated with the
        /// ObdInsight.SourceGeneration.CanFrameAttribute and to have at least one partial property marked with the
        /// CanSignal attribute. Only properties that are partial and have the required CanSignal attribute are included
        /// as signals in the resulting model.</remarks>
        /// <param name="context">The syntax context containing the class declaration node to analyze.</param>
        /// <returns>A CanFrameModel representing the CAN frame and its signals if the class is properly annotated; otherwise,
        /// null.</returns>
        private static CanFrameModel? GetCanFrameModel(GeneratorSyntaxContext context)
        {
            var classDecl = (ClassDeclarationSyntax)context.Node;
            var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl);

            if (symbol is not INamedTypeSymbol classSymbol)
                return null;

            // Find [CanFrame] attribute
            var canFrameAttr = classSymbol.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "ObdInsight.SourceGeneration.Attributes.CanFrameAttribute");

            if (canFrameAttr is null)
                return null;

            // Extract CAN ID from constructor
            if (canFrameAttr.ConstructorArguments.Length == 0)
                return null;

            var canId = canFrameAttr.ConstructorArguments[0].Value as int?;
            if (!canId.HasValue)
                return null;

            // Extract optional Description
            var description = canFrameAttr.NamedArguments
                .FirstOrDefault(a => a.Key == "Description").Value.Value as string;

            // Find all properties with [CanSignal]
            var signals = new List<CanSignalModel>();

            foreach (var member in classSymbol.GetMembers().OfType<IPropertySymbol>())
            {
                // Property must be partial
                if (!member.IsPartialDefinition)
                    continue;

                var signalAttr = member.GetAttributes()
                    .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "ObdInsight.SourceGeneration.Attributes.CanSignalAttribute");

                if (signalAttr is null)
                    continue;

                // Extract required constructor arguments
                if (signalAttr.ConstructorArguments.Length < 2)
                    continue;

                var bitStart = (int)signalAttr.ConstructorArguments[0].Value!;
                var bitLength = (int)signalAttr.ConstructorArguments[1].Value!;

                // Extract optional named arguments
                var factor = 1.0;
                var offset = 0.0;
                var isSigned = false;
                string? unit = null;
                string? signalDescription = null;
                double? minValue = null;
                double? maxValue = null;
                var includeInGeneration = true;

                foreach (var namedArg in signalAttr.NamedArguments)
                {
                    switch (namedArg.Key)
                    {
                        case "Factor":
                            factor = (double)namedArg.Value.Value!;
                            break;
                        case "Offset":
                            offset = (double)namedArg.Value.Value!;
                            break;
                        case "IsSigned":
                            isSigned = (bool)namedArg.Value.Value!;
                            break;
                        case "Unit":
                            unit = namedArg.Value.Value as string;
                            break;
                        case "Description":
                            signalDescription = namedArg.Value.Value as string;
                            break;
                        case "MinValue":
                            var min = (double)namedArg.Value.Value!;
                            if (!double.IsNaN(min)) minValue = min;
                            break;
                        case "MaxValue":
                            var max = (double)namedArg.Value.Value!;
                            if (!double.IsNaN(max)) maxValue = max;
                            break;
                        case "IncludeInGeneration":
                            includeInGeneration = (bool)namedArg.Value.Value!;
                            break;
                    }
                }

                // Skip signals that are marked as not to be included in generation
                if (!includeInGeneration)
                    continue;

                signals.Add(new CanSignalModel(
                    PropertyName: member.Name,
                    PropertyType: member.Type.ToDisplayString(),
                    BitStart: bitStart,
                    BitLength: bitLength,
                    Factor: factor,
                    Offset: offset,
                    IsSigned: isSigned,
                    Unit: unit,
                    Description: signalDescription,
                    MinValue: minValue,
                    MaxValue: maxValue));
            }

            if (signals.Count == 0)
                return null;

            return new CanFrameModel(
                Namespace: classSymbol.ContainingNamespace.ToDisplayString(),
                ClassName: classSymbol.Name,
                CanId: canId.Value,
                Description: description,
                Signals: [.. signals]);
        }

        /// <summary>
        /// Generates the source code for a CAN frame decoder class based on the specified CAN frame model.
        /// </summary>
        /// <remarks>The generated code includes class documentation, a parse method, and properties for
        /// each signal defined in the model. The output is intended for use with code generation workflows and should
        /// not be modified manually.</remarks>
        /// <param name="model">The CAN frame model that defines the structure, signals, and metadata for the generated decoder class.
        /// Cannot be null.</param>
        /// <returns>A string containing the complete source code for the generated CAN frame decoder class.</returns>
        private static string GenerateCanFrameDecoder(CanFrameModel model)
        {
            var sb = new StringBuilder();

            // File header
            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("// This code was generated by ObdInsight.SourceGeneration.CanSignalGenerator");
            sb.AppendLine("// Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine();
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine();

            // Namespace
            sb.AppendLine($"namespace {model.Namespace}");
            sb.AppendLine("{");

            // Class documentation
            sb.AppendLine("    /// <summary>");
            if (!string.IsNullOrEmpty(model.Description))
            {
                sb.AppendLine($"    /// {model.Description}");
            }
            else
            {
                sb.AppendLine($"    /// CAN Frame decoder for ID 0x{model.CanId:X3}");
            }
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    partial class {model.ClassName}");
            sb.AppendLine("    {");

            // Generate Parse method
            GenerateParseMethod(sb, model);

            // Generate properties
            foreach (var signal in model.Signals)
            {
                GenerateProperty(sb, signal);
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        /// <summary>
        /// Generates the source code for a static Parse method that parses a CAN frame with a specific ID from raw
        /// 8-byte data.
        /// </summary>
        /// <remarks>The generated Parse method validates that the input data is exactly 8 bytes in length
        /// and throws an exception if this condition is not met. The method constructs an instance of the specified
        /// model class by decoding each signal from the provided data.</remarks>
        /// <param name="sb">The StringBuilder to which the generated method source code will be appended.</param>
        /// <param name="model">The CAN frame model describing the frame structure and signals to be parsed.</param>
        private static void GenerateParseMethod(StringBuilder sb, CanFrameModel model)
        {
            sb.AppendLine("        /// <summary>");
            sb.AppendLine($"        /// Parses a CAN frame with ID 0x{model.CanId:X3} from raw 8-byte data.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        /// <param name=\"data\">8-byte CAN frame data (little-endian byte order)</param>");
            sb.AppendLine($"        /// <returns>Parsed {model.ClassName} instance</returns>");
            sb.AppendLine("        /// <exception cref=\"ArgumentException\">Thrown if data length is not 8 bytes</exception>");
            sb.AppendLine($"        public static {model.ClassName} Parse(ReadOnlySpan<byte> data)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (data.Length != 8)");
            sb.AppendLine("                throw new ArgumentException($\"CAN frame data must be exactly 8 bytes, got {data.Length}\", nameof(data));");
            sb.AppendLine();
            sb.AppendLine($"            return new {model.ClassName}");
            sb.AppendLine("            {");

            for (var i = 0; i < model.Signals.Length; i++)
            {
                var signal = model.Signals[i];
                var decodeExpr = GenerateDecodeExpression(signal);
                var isLast = i == model.Signals.Length - 1;
                var comma = isLast ? "" : ",";  // No comma on last item
                sb.AppendLine($"                {signal.PropertyName} = {decodeExpr}{comma}");
            }

            sb.AppendLine("            };");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        /// <summary>
        /// Appends XML documentation comments for a property representing a CAN signal to the specified StringBuilder.
        /// </summary>
        /// <remarks>Includes unit and valid value range information in the documentation if available in
        /// the signal model.</remarks>
        /// <param name="sb">The StringBuilder to which the property documentation and declaration are appended.</param>
        /// <param name="signal">The CAN signal model containing metadata used to generate the property's documentation and declaration.
        /// Cannot be null.</param>
        private static void GenerateProperty(StringBuilder sb, CanSignalModel signal)
        {
            // XML documentation
            sb.AppendLine("        /// <summary>");
            if (!string.IsNullOrEmpty(signal.Description))
            {
                sb.AppendLine($"        /// {signal.Description}");
            }
            else
            {
                sb.AppendLine($"        /// Signal at bit {signal.BitStart}, length {signal.BitLength}");
            }
            sb.AppendLine("        /// </summary>");

            if (!string.IsNullOrEmpty(signal.Unit))
            {
                sb.AppendLine($"        /// <remarks>Unit: {signal.Unit}</remarks>");
            }

            if (signal.MinValue.HasValue || signal.MaxValue.HasValue)
            {
                var range = (signal.MinValue, signal.MaxValue) switch
                {
                    ({ } min, { } max) => $"Valid range: {min} to {max}",
                    ({ } min, null) => $"Minimum value: {min}",
                    (null, { } max) => $"Maximum value: {max}",
                    _ => null
                };
                if (range != null)
                {
                    sb.AppendLine($"        /// <remarks>{range}</remarks>");
                }
            }

            sb.AppendLine($"        public partial {signal.PropertyType} {signal.PropertyName} {{ get => __{signal.PropertyName}; init => __{signal.PropertyName} = value; }}");
            sb.AppendLine($"        private {signal.PropertyType} __{signal.PropertyName};");
            sb.AppendLine();
        }

        /// <summary>
        /// Generates a C# expression that decodes a CAN signal from a data buffer according to the specified signal
        /// model.
        /// </summary>
        /// <remarks>The generated expression uses methods from the CanBits class to extract the raw value
        /// and applies scaling and offset if required. The result is cast to the target property type as defined in the
        /// signal model.</remarks>
        /// <param name="signal">The CAN signal model that defines the bit position, length, scaling, and type information used to generate
        /// the decode expression. Cannot be null.</param>
        /// <returns>A string containing a C# expression that reads and converts the signal value from a CAN data buffer,
        /// applying scaling and type conversion as specified by the signal model.</returns>
        private static string GenerateDecodeExpression(CanSignalModel signal)
        {
            // Determine the read method based on type and signedness
            string readMethod;
            if (signal.PropertyType == "bool")
            {
                readMethod = "CanBits.ReadBool";
            }
            else if (signal.IsSigned)
            {
                readMethod = "CanBits.ReadSigned";
            }
            else
            {
                readMethod = "CanBits.ReadUnsigned";
            }

            // For bool, just read the bit
            if (signal.PropertyType == "bool")
            {
                return $"{readMethod}(data, {signal.BitStart})";
            }

            // Build the raw value expression
            var rawExpr = signal.BitLength == 1
                ? $"{readMethod}(data, {signal.BitStart})"
                : $"{readMethod}(data, {signal.BitStart}, {signal.BitLength})";

            // Apply scaling if needed
            var needsScaling = signal.Factor != 1.0 || signal.Offset != 0.0;

            if (!needsScaling)
            {
                return CastExpression(rawExpr, signal.PropertyType);
            }

            // Build scaling expression: (raw * factor) + offset
            string scaledExpr;
            if (signal.Factor != 1.0 && signal.Offset != 0.0)
            {
                scaledExpr = $"({rawExpr} * {FormatDouble(signal.Factor)}) + {FormatDouble(signal.Offset)}";
            }
            else if (signal.Factor != 1.0)
            {
                scaledExpr = $"{rawExpr} * {FormatDouble(signal.Factor)}";
            }
            else // offset != 0.0
            {
                scaledExpr = $"{rawExpr} + {FormatDouble(signal.Offset)}";
            }

            // ALWAYS wrap scaled expressions in explicit cast for clarity
            if (signal.PropertyType == "double" || signal.PropertyType == "float")
            {
                return $"({signal.PropertyType})({scaledExpr})";
            }

            return CastExpression(scaledExpr, signal.PropertyType);
        }

        /// <summary>
        /// Generates a string representing a C# cast expression for the specified target type.
        /// </summary>
        /// <remarks>If the target type is not one of the recognized primitive types, the method generates
        /// a cast using the provided type name as-is. The returned string is intended for code generation scenarios and
        /// does not validate the correctness of the cast.</remarks>
        /// <param name="expr">The expression to be cast, represented as a string.</param>
        /// <param name="targetType">The name of the target C# type to cast to. Common primitive types such as "int", "double", and "float" are
        /// supported.</param>
        /// <returns>A string containing the C# cast expression that casts the specified expression to the target type.</returns>
        private static string CastExpression(string expr, string targetType)
        {
            return targetType switch
            {
                "double" => $"(double)({expr})",  // Explicit cast
                "float" => $"(float)({expr})",
                "int" => $"(int)({expr})",
                "long" => $"(long)({expr})",
                "short" => $"(short)({expr})",
                "byte" => $"(byte)({expr})",
                "uint" => $"(uint)({expr})",
                "ushort" => $"(ushort)({expr})",
                _ => $"({targetType})({expr})"
            };
        }

        /// <summary>
        /// Formats a double-precision floating-point value as a string using fixed-point notation, omitting scientific
        /// notation and unnecessary decimal digits.
        /// </summary>
        /// <param name="value">The double-precision floating-point value to format.</param>
        /// <returns>A string representation of the value in fixed-point notation. If the value is an integer, no decimal point
        /// is included; otherwise, up to 13 decimal digits are shown without trailing zeros.</returns>
        private static string FormatDouble(double value)
        {
            // Format with appropriate precision and avoid scientific notation
            if (value == (int)value)
                return ((int)value).ToString();

            return value.ToString("0.0##############");
        }

        /// <summary>
        /// Generates helper method definitions for reading boolean and integer values from bit fields and appends them
        /// to the specified StringBuilder.
        /// </summary>
        /// <remarks>The generated methods provide functionality for reading single bits as booleans and
        /// extracting signed or unsigned integer values from a sequence of bits, which is commonly required when
        /// working with CAN (Controller Area Network) message data.</remarks>
        /// <param name="sb">The StringBuilder to which the generated helper method definitions are appended.</param>
        private static void GenerateCanBitsHelperMethods(StringBuilder sb)
        {
            sb.AppendLine("        public static bool ReadBool(ReadOnlySpan<byte> data, int bitPos)");
            sb.AppendLine("        {");
            sb.AppendLine("            return ReadUnsigned(data, bitPos, 1) != 0;");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        public static uint ReadSigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)");
            sb.AppendLine("        {");
            sb.AppendLine("            var unsigned = ReadUnsigned(data, bitPos, bitLen);");
            sb.AppendLine("            var signBitMask = 1u << (bitLen - 1);");
            sb.AppendLine("            if ((unsigned & signBitMask) != 0)");
            sb.AppendLine("            {");
            sb.AppendLine("                var signExtendMask = ~((1u << bitLen) - 1);");
            sb.AppendLine("                return unsigned | signExtendMask;");
            sb.AppendLine("            }");
            sb.AppendLine("            return unsigned;");
            sb.AppendLine("        }");
            sb.AppendLine();

            sb.AppendLine("        public static uint ReadUnsigned(ReadOnlySpan<byte> data, int bitPos, int bitLen)");
            sb.AppendLine("        {");
            sb.AppendLine("            var raw = BinaryPrimitives.ReadUInt64LittleEndian(data);");
            sb.AppendLine("            var mask = bitLen == 32 ? 0xFFFF_FFFFul : ((1ul << bitLen) - 1ul);");
            sb.AppendLine("            return (uint)((raw >> bitPos) & mask);");
            sb.AppendLine("        }");
        }

        /// <summary>
        /// Generates the complete source code for a helper class that provides raw CAN frame bit manipulation methods
        /// within the specified namespace.
        /// </summary>
        /// <remarks>The generated code includes an auto-generated file header and enables nullable
        /// reference types. Any changes made to the generated file may be lost if the code is regenerated.</remarks>
        /// <param name="namespace">The namespace in which to place the generated CanBits helper class. Cannot be null or empty.</param>
        /// <returns>A string containing the full source code of the generated CanBits helper class, including file headers and
        /// using directives.</returns>
        private static string GenerateCanBitsFile(string @namespace)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("// This code was generated by ObdInsight.SourceGeneration.CanSignalGenerator");
            sb.AppendLine("// Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine();
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Buffers.Binary;");
            sb.AppendLine();

            sb.AppendLine($"namespace {@namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    // Helper class for raw CAN frame bit manipulation");
            sb.AppendLine("    static class CanBits");
            sb.AppendLine("    {");
            GenerateCanBitsHelperMethods(sb);
            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }

        private static string GenerateCanFrameRouterFile(string @namespace, ImmutableArray<CanFrameModel> frames)
        {
            var sb = new StringBuilder();

            sb.AppendLine("// <auto-generated>");
            sb.AppendLine("// This code was generated by ObdInsight.SourceGeneration.CanSignalGenerator");
            sb.AppendLine("// Changes to this file may cause incorrect behavior and will be lost if the code is regenerated.");
            sb.AppendLine("// </auto-generated>");
            sb.AppendLine();
            sb.AppendLine("#nullable enable");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine();

            sb.AppendLine($"namespace {@namespace}");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine("    /// Provides automatic routing of CAN frames to their corresponding parser methods based on CAN ID.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine("    public static class CanFrameRouter");
            sb.AppendLine("    {");

            // Generate TryParse method for each frame type
            foreach (var frame in frames)
            {
                sb.AppendLine($"        /// <summary>");
                sb.AppendLine($"        /// Attempts to parse a CAN frame with ID 0x{frame.CanId:X3}.");
                sb.AppendLine($"        /// </summary>");
                sb.AppendLine($"        public static bool TryParse{frame.ClassName}(int canId, ReadOnlySpan<byte> data, out {frame.ClassName}? result)");
                sb.AppendLine("        {");
                sb.AppendLine($"            if (canId == 0x{frame.CanId:X3} && data.Length == 8)");
                sb.AppendLine("            {");
                sb.AppendLine($"                result = {frame.ClassName}.Parse(data);");
                sb.AppendLine("                return true;");
                sb.AppendLine("            }");
                sb.AppendLine("            result = null;");
                sb.AppendLine("            return false;");
                sb.AppendLine("        }");
                sb.AppendLine();
            }

            // Generate unified TryParseAny method
            sb.AppendLine("        /// <summary>");
            sb.AppendLine("        /// Attempts to parse any registered CAN frame type based on the CAN ID.");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine("        /// <returns>Parsed frame object, or null if the CAN ID is not recognized.</returns>");
            sb.AppendLine("        public static object? TryParseAny(int canId, ReadOnlySpan<byte> data)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (data.Length != 8) return null;");
            sb.AppendLine();
            sb.AppendLine("            return canId switch");
            sb.AppendLine("            {");

            foreach (var frame in frames)
            {
                sb.AppendLine($"                0x{frame.CanId:X3} => {frame.ClassName}.Parse(data),");
            }

            sb.AppendLine("                _ => null");
            sb.AppendLine("            };");
            sb.AppendLine("        }");

            sb.AppendLine("    }");
            sb.AppendLine("}");

            return sb.ToString();
        }
    }

    /// <summary>
    /// Represents a CAN (Controller Area Network) frame definition, including its namespace, class name, CAN
    /// identifier, description, and associated signals.
    /// </summary>
    /// <remarks>This record is intended for modeling and code generation scenarios involving CAN bus
    /// communication. It is not intended for direct use in runtime message transmission or reception.</remarks>
    /// <param name="Namespace">The logical namespace to which the CAN frame belongs. Used to organize frames within a larger system or domain.</param>
    /// <param name="ClassName">The name of the class representing the CAN frame. Typically used for code generation or identification purposes.</param>
    /// <param name="CanId">The numeric CAN identifier (CAN ID) associated with the frame. Must be a valid CAN frame identifier.</param>
    /// <param name="Description">An optional description providing additional information about the CAN frame. Can be null if no description is
    /// available.</param>
    /// <param name="Signals">The collection of signals defined within the CAN frame. Each signal describes a specific data field carried by
    /// the frame.</param>
    internal sealed record CanFrameModel(
        string Namespace,
        string ClassName,
        int CanId,
        string? Description,
        ImmutableArray<CanSignalModel> Signals);

    /// <summary>
    /// Represents the definition of a CAN signal, including its bit position, scaling, and metadata used for
    /// interpreting raw CAN data.
    /// </summary>
    /// <remarks>This model is typically used to describe how to extract and interpret a signal from a CAN
    /// message according to its bit layout and scaling parameters. It is intended for use in applications that parse or
    /// generate CAN messages based on signal definitions.</remarks>
    /// <param name="PropertyName">The name of the property that corresponds to the CAN signal.</param>
    /// <param name="PropertyType">The data type of the property representing the CAN signal (for example, "int", "double").</param>
    /// <param name="BitStart">The zero-based starting bit position of the signal within the CAN message payload.</param>
    /// <param name="BitLength">The number of bits occupied by the signal in the CAN message payload. Must be positive.</param>
    /// <param name="Factor">The scaling factor applied to the raw signal value to convert it to a physical value.</param>
    /// <param name="Offset">The offset added to the scaled signal value to obtain the final physical value.</param>
    /// <param name="IsSigned">A value indicating whether the signal is interpreted as a signed value. Set to <see langword="true"/> if the
    /// signal is signed; otherwise, <see langword="false"/>.</param>
    /// <param name="Unit">The unit of measurement for the physical value of the signal, or <see langword="null"/> if not specified.</param>
    /// <param name="Description">A textual description of the signal, or <see langword="null"/> if not provided.</param>
    /// <param name="MinValue">The minimum valid physical value for the signal, or <see langword="null"/> if not specified.</param>
    /// <param name="MaxValue">The maximum valid physical value for the signal, or <see langword="null"/> if not specified.</param>
    internal sealed record CanSignalModel(
        string PropertyName,
        string PropertyType,
        int BitStart,
        int BitLength,
        double Factor,
        double Offset,
        bool IsSigned,
        string? Unit,
        string? Description,
        double? MinValue,
        double? MaxValue);
}
