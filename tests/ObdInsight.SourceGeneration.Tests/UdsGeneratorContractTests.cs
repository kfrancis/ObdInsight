using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ObdInsight.SourceGeneration.Tests;

public class UdsGeneratorContractTests
{
    private static string Schema(string responseAttributes, string fields) => $$"""
        using System.Threading;
        using System.Threading.Tasks;
        using ObdInsight.SourceGeneration.Attributes;
        namespace Contracts;
        public class Session
        {
            public Task<string[]> QueryAsync(string command, Context context, CancellationToken ct) => Task.FromResult(new string[0]);
            public async Task<ObdInsight.Core.Protocols.Observed<string[]>> QueryResponseAsync(string command, Context context, CancellationToken ct) =>
                new(await QueryAsync(command, context, ct), new ObdInsight.Core.Protocols.ObservationMetadata(
                    new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero),
                    ObdInsight.Core.Protocols.ObservationSource.DiagnosticQuery, Query: command));
        }
        public class Context { public string RxFilter => "7BB"; }
        [UdsService(0x21)]
        public partial class Diagnostics
        {
            private readonly Session _session = new();
            private readonly Context _context = new();
            [UdsPid(0x01, Name = "Status")]
            {{responseAttributes}}
            public class Response
            {
                {{fields}}
            }
        }
        """;

    [Test]
    public async Task NullableArray_PreservesSlotsAndRejectsMissingBytes()
    {
        var result = GeneratorTestHelper.RunGenerator<UdsGenerator>(Schema(
            "[UdsResponse(MinLength = 6, MaxLength = 6)]",
            """
            [UdsArrayField(Offset = 0, ElementCount = 3, ElementLength = 2, Type = UdsFieldType.UInt16BE, ValidRange = "2500..4500")]
            public int?[] Cells { get; set; } = [];
            """));
        var source = GeneratorTestHelper.GetGeneratedSource(result);
        await Assert.That(source).Contains("new int?[3]");
        await Assert.That(source).Contains("if (data.Length < 6) return Invalid();");
        await Assert.That(source).Contains("if (data.Length > 6) return Invalid();");
        await Assert.That(source).Contains("continue; // Preserve this index as missing.");
        await Assert.That(source).Contains("cellsValues[index]");
    }

    [Test]
    public async Task MultipleFrameSourcedFields_CompileWithSeparateScopes()
    {
        var result = GeneratorTestHelper.RunGenerator<UdsGenerator>(Schema("", """
            [UdsField(FrameType = FrameSource.FirstFrame, Offset = 2, Length = 1, Type = UdsFieldType.UInt8)]
            public int First { get; set; }
            [UdsField(FrameType = FrameSource.ConsecutiveFrame, FrameSequence = 3, Offset = 0, Length = 1, Type = UdsFieldType.UInt8)]
            public int A { get; set; }
            [UdsField(FrameType = FrameSource.ConsecutiveFrame, FrameSequence = 3, Offset = 1, Length = 1, Type = UdsFieldType.UInt8)]
            public int B { get; set; }
            """));
        await Assert.That(result.Diagnostics).IsEmpty();
    }

    [Test]
    [Arguments("[UdsField(Offset = -1, Length = 2, Type = UdsFieldType.UInt16BE)]")]
    [Arguments("[UdsField(Offset = 0, Length = 1, Type = UdsFieldType.UInt16BE)]")]
    [Arguments("[UdsField(Offset = 0, Length = 2, Type = UdsFieldType.Int16BE)]")]
    [Arguments("[UdsField(Offset = 0, Length = 2, Type = UdsFieldType.UInt16BE, ValidRange = \"oops\")]")]
    [Arguments("[UdsField(Offset = 0, Length = 2, Type = UdsFieldType.UInt16BE, AppliesTo = \"unknown\")]")]
    public async Task InvalidDefinition_ReportsActionableError(string fieldAttribute)
    {
        var result = GeneratorTestHelper.RunGenerator<UdsGenerator>(Schema("", fieldAttribute + " public int Value { get; set; }"));
        await Assert.That(result.Diagnostics.Any(d => d.Id == "OBDUDS001" && d.Severity == DiagnosticSeverity.Error)).IsTrue();
        await Assert.That(result.Results[0].GeneratedSources).IsEmpty();
    }

    [Test]
    [Arguments("[UdsResponse(MinLength = 10, MaxLength = 5)]")]
    [Arguments("[UdsResponseVariant(Length = 4, Model = \"A\")][UdsResponseVariant(Length = 4, Model = \"B\")]")]
    public async Task AmbiguousOrInvalidResponseLayout_ReportsError(string attributes)
    {
        var result = GeneratorTestHelper.RunGenerator<UdsGenerator>(Schema(attributes,
            "[UdsField(Offset = 0, Length = 1, Type = UdsFieldType.UInt8)] public int Value { get; set; }"));
        await Assert.That(result.Diagnostics.Any(d => d.Id == "OBDUDS001")).IsTrue();
    }

    [Test]
    public async Task GeneratedNumbers_AreCultureInvariantAndCompile()
    {
        var input = Schema("", """
            [UdsField(Offset = 0, Length = 2, Type = UdsFieldType.UInt16BE, Scale = 0.01, ValidRange = "0.1..99.9")]
            public double? Intensity { get; set; }
            """);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var expected = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<UdsGenerator>(input));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var actual = GeneratorTestHelper.GetGeneratedSource(GeneratorTestHelper.RunGenerator<UdsGenerator>(input));
            await Assert.That(actual).IsEqualTo(expected);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Test]
    [Arguments("7BB04610100FF", true)]
    [Arguments("7BB0461010100", false)]
    [Arguments("7BB03610101", false)]
    [Arguments("7BB056101000100", false)]
    public async Task ExecutedQuery_EnforcesBoundsAndNarrowing(string line, bool succeeds)
    {
        var source = Schema("[UdsResponse(MinLength = 2, MaxLength = 2)]",
            "[UdsField(Offset = 0, Length = 2, Type = UdsFieldType.UInt16BE)] public byte Number { get; set; }")
            .Replace("Task.FromResult(new string[0])", $"Task.FromResult(new[] {{ \"{line}\" }})");
        await Assert.That(await ExecuteQuery(source)).IsEqualTo(succeeds);
    }

    [Test]
    public async Task ExecutedQuery_RejectsRoundedIntegralOverflow()
    {
        var source = Schema("", """
            [UdsField(Offset = 0, Length = 4, Type = UdsFieldType.UInt32BE, Scale = 4294967296.0)]
            public long Number { get; set; }
            """).Replace("Task.FromResult(new string[0])", "Task.FromResult(new[] { \"7BB06610180000000\" })");
        await Assert.That(await ExecuteQuery(source)).IsFalse();
    }

    private static async Task<bool> ExecuteQuery(string source)
    {
        source += """
            public static class Entry
            {
                public static async System.Threading.Tasks.Task<bool> Run()
                {
                    var result = await new Diagnostics().QueryStatusAsync();
                    return result is not null && result.Value is not null && result.Observation.Query == "2101" &&
                        result.Observation.ObservedAtUtc == new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero);
                }
            }
            """;
        var compilation = GeneratorTestHelper.CreateCompilation(source, includeCore: true);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new UdsGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        using var stream = new MemoryStream();
        var result = output.Emit(stream);
        if (!result.Success)
            throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
        // Reflection is confined to compiler tests, not emitted/runtime library code.
        var assembly = System.Reflection.Assembly.Load(stream.ToArray());
        var run = assembly.GetType("Contracts.Entry")!.GetMethod("Run")!.CreateDelegate<Func<Task<bool>>>();
        return await run();
    }
}
