using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ObdInsight.SourceGeneration;
/// <summary>
/// Base helper for testing source generators with proper compilation setup
/// </summary>
public static class GeneratorTestHelper
{
    /// <summary>
    /// Creates a compilation with the necessary references for testing CAN frame generation
    /// </summary>
    public static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = new List<MetadataReference>
        {
            // Core BCL references
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),                    // System.Private.CoreLib
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),                   // System.Console
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),                // System.Linq
            MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),                 // System.Runtime
        };

        // Add System.Runtime if it's not already included
        try
        {
            var systemRuntimeAssembly = System.Reflection.Assembly.Load(new AssemblyName("System.Runtime"));
            references.Add(MetadataReference.CreateFromFile(systemRuntimeAssembly.Location));
        }
        catch
        {
            // System.Runtime might be embedded in System.Private.CoreLib on some platforms
        }

        // Add netstandard if available (for .NET Framework compatibility)
        try
        {
            var netstandardAssembly = System.Reflection.Assembly.Load(new AssemblyName("netstandard"));
            references.Add(MetadataReference.CreateFromFile(netstandardAssembly.Location));
        }
        catch
        {
            // netstandard not available on .NET Core/.NET 5+
        }

        // Add reference to the attributes assembly (CanFrameAttribute, CanSignalAttribute)
        var attributesAssembly = typeof(CanFrameAttribute).Assembly;
        references.Add(MetadataReference.CreateFromFile(attributesAssembly.Location));

        // Add System.Memory if available (for ReadOnlySpan<T>)
        try
        {
            var memoryAssembly = System.Reflection.Assembly.Load(new AssemblyName("System.Memory"));
            references.Add(MetadataReference.CreateFromFile(memoryAssembly.Location));
        }
        catch
        {
            // System.Memory might be in core lib
        }

        return CSharpCompilation.Create(
            assemblyName: "TestAssembly",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>
    /// Runs the generator and returns the generated sources
    /// </summary>
    public static GeneratorDriverRunResult RunGenerator(string source)
    {
        var compilation = CreateCompilation(source);
        var generator = new CanSignalGenerator();

        var driver = CSharpGeneratorDriver.Create(generator);
        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation,
            out var outputCompilation,
            out var diagnostics);

        return driver.GetRunResult();
    }

    /// <summary>
    /// Gets the single generated source from the result
    /// </summary>
    public static string GetGeneratedSource(GeneratorDriverRunResult result)
    {
        if (result.Results.Length == 0)
            throw new InvalidOperationException("No generator results found");

        if (result.Results[0].GeneratedSources.Length == 0)
            throw new InvalidOperationException("No generated sources found");

        return result.Results[0].GeneratedSources[0].SourceText.ToString();
    }

    /// <summary>
    /// Gets all generated sources indexed by hint name
    /// </summary>
    public static Dictionary<string, string> GetAllGeneratedSources(GeneratorDriverRunResult result)
    {
        if (result.Results.Length == 0)
            return new Dictionary<string, string>();

        return result.Results[0]
            .GeneratedSources
            .ToDictionary(
                source => source.HintName,
                source => source.SourceText.ToString());
    }
}
