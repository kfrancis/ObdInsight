using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ObdInsight.SourceGeneration.Tests
{
    public class CanSignalGeneratorTests
    {
        [Test]
        public async Task CompilationSucceedsWithGeneratedCode()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x54C)]
                public partial class TestFrame
                {
                    [CanSignal(0, 8, Factor = 0.25, Unit = "°C")]
                    public partial double Temperature { get; init; }

                    [CanSignal(9, 1)]
                    public partial bool IsEnabled { get; init; }
                }
            }
            """;

            var compilation = GeneratorTestHelper.CreateCompilation(source);
            var generator = new CanSignalGenerator();

            var driver = CSharpGeneratorDriver.Create(generator);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var diagnostics);

            // Check that the output compilation has no errors
            var compilationDiagnostics = outputCompilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            await Assert.That(compilationDiagnostics.Count).IsEqualTo(0);
        }

        [Test]
        public async Task GeneratesBoolSignal()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x54C)]
                public partial class TestFrame
                {
                    [CanSignal(9, 1)]
                    public partial bool IsEnabled { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);

            // Verify no diagnostics
            await Assert.That(result.Diagnostics.Length).IsEqualTo(0);

            // Verify source was generated
            await Assert.That(result.Results[0].GeneratedSources.Length).IsEqualTo(3);

            // Snapshot test the generated code
            await Verify(result);
        }

        [Test]
        public async Task GeneratesDoubleSignal()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x54C)]
                public partial class HvacFrame
                {
                    [CanSignal(0, 8, Factor = 0.25, Unit = "°C",
                        Description = "Evaporator temperature")]
                    public partial double Temperature { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);
            await Verify(result);
        }

        [Test]
        public async Task GeneratesMultipleSignals()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x54C, Description = "HVAC status frame")]
                public partial class HvacFrame
                {
                    [CanSignal(0, 8, Factor = 0.25, Unit = "°C",
                        Description = "Evaporator temperature")]
                    public partial double EvaporatorTemp { get; }

                    [CanSignal(9, 1, Description = "Rear defrost active")]
                    public partial bool RearDefrostOn { get; }

                    [CanSignal(10, 1, Description = "Climate control enabled")]
                    public partial bool ClimateControlOn { get; }

                    [CanSignal(11, 1, Description = "A/C compressor active")]
                    public partial bool AcOn { get; }

                    [CanSignal(40, 8, Factor = 0.05, Unit = "V",
                        Description = "Fan voltage")]
                    public partial double FanVoltage { get; }

                    [CanSignal(48, 8, Factor = 0.5, Offset = -40.0, Unit = "°C",
                        Description = "Outside ambient temperature")]
                    public partial double OutsideTemp { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);
            await Verify(result);
        }

        [Test]
        public async Task GeneratesSignalWithFactorAndOffset()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x54C)]
                public partial class TestFrame
                {
                    [CanSignal(48, 8, Factor = 0.5, Offset = -40.0, Unit = "°C",
                        Description = "Outside ambient temperature")]
                    public partial double OutsideTemp { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);
            await Verify(result);
        }

        [Test]
        public async Task GeneratesSignalWithFactorOnly()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x54C)]
                public partial class TestFrame
                {
                    [CanSignal(40, 8, Factor = 0.05, Unit = "V")]
                    public partial double Voltage { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);
            await Verify(result);
        }

        [Test]
        public async Task GeneratesSignalWithOffsetOnly()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x54C)]
                public partial class TestFrame
                {
                    [CanSignal(48, 8, Offset = -40.0, Unit = "°C")]
                    public partial double Temperature { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);
            await Verify(result);
        }

        [Test]
        public async Task GeneratesSignedIntSignal()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x1DB)]
                public partial class BatteryFrame
                {
                    [CanSignal(0, 16, IsSigned = true, Unit = "A",
                        Description = "Battery current (positive=discharge)")]
                    public partial int Current { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);
            await Verify(result);
        }

        [Test]
        public async Task GeneratesUnsignedIntSignal()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x1DB)]
                public partial class BatteryFrame
                {
                    [CanSignal(32, 10, Unit = "Gids", Description = "Available capacity")]
                    public partial int AvailableCapacity { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);
            await Verify(result);
        }

        [Test]
        public async Task HandlesNestedNamespace()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace Vehicles.Nissan.Leaf
            {
                [CanFrame(0x1DB)]
                public partial class BatteryFrame
                {
                    [CanSignal(0, 16, Factor = 0.5, Unit = "A")]
                    public partial double Current { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);

            var generated = GeneratorTestHelper.GetGeneratedSource(result);
            await Assert.That(generated).Contains("namespace Vehicles.Nissan.Leaf");

            await Verify(result);
        }

        [Test]
        public async Task IgnoresNonPartialClass()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x54C)]
                public class NonPartialFrame  // Missing 'partial' keyword
                {
                    [CanSignal(0, 8)]
                    public partial double Temperature { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);

            // Should generate nothing for non-partial class
            await Assert.That(result.Results[0].GeneratedSources.Length).IsEqualTo(0);
        }

        [Test]
        public async Task NoErrorsForValidFrame()
        {
            var source = """
            using ObdInsight.SourceGeneration;

            namespace TestNamespace
            {
                [CanFrame(0x54C)]
                public partial class ValidFrame
                {
                    [CanSignal(0, 8, Factor = 0.25)]
                    public partial double Temperature { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);

            // Should have no errors or warnings
            var diagnostics = result.Diagnostics
                .Where(d => d.Severity >= DiagnosticSeverity.Warning)
                .ToList();

            await Assert.That(diagnostics.Count).IsEqualTo(0);
        }
    }
}
