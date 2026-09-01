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
            using ObdInsight.SourceGeneration.Attributes;

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
        public async Task SingleBitNumericSignal_Compiles()
        {
            // A 1-bit signal declared as a number, not a bool: only ReadBool takes a bit position
            // without a length, so the decoder has to use the three-argument read here.
            var source = """
            using ObdInsight.SourceGeneration.Attributes;

            namespace TestNamespace
            {
                [CanFrame(0x54C)]
                public partial class TestFrame
                {
                    [CanSignal(3, 1)]
                    public partial int Flag { get; init; }

                    [CanSignal(4, 1, IsSigned = true)]
                    public partial int SignedFlag { get; init; }

                    [CanSignal(5, 1, Factor = 0.5)]
                    public partial double ScaledFlag { get; init; }
                }
            }
            """;

            var compilation = GeneratorTestHelper.CreateCompilation(source);

            CSharpGeneratorDriver.Create(new CanSignalGenerator())
                .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

            var errors = outputCompilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            await Assert.That(errors).IsEmpty();
        }

        [Test]
        public async Task GeneratesBoolSignal()
        {
            var source = """
            using ObdInsight.SourceGeneration.Attributes;

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
            using ObdInsight.SourceGeneration.Attributes;

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
            using ObdInsight.SourceGeneration.Attributes;

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
            using ObdInsight.SourceGeneration.Attributes;

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
            using ObdInsight.SourceGeneration.Attributes;

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
            using ObdInsight.SourceGeneration.Attributes;

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
            using ObdInsight.SourceGeneration.Attributes;

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

        /// <summary>
        /// Motorola signals must emit the *Be readers. The interesting part is not that they
        /// differ but that reading the attribute works at all: Roslyn delivers an enum named
        /// argument as its boxed underlying int, so a generator that casts to the enum type
        /// throws and one that matches on the member name silently never matches - defaulting
        /// every signal to Intel while the snapshot still looks plausible.
        ///
        /// The frame mixes both orders so the snapshot shows the two call forms side by side.
        /// LB_Current here is the real 0x1DB definition from EV-can_AZE0.dbc: big-endian,
        /// start 7, length 11, signed, 0.5 A per bit.
        /// </summary>
        [Test]
        public async Task GeneratesMotorolaSignals()
        {
            var source = """
            using ObdInsight.SourceGeneration.Attributes;

            namespace TestNamespace
            {
                [CanFrame(0x1DB)]
                public partial class BatteryFrame
                {
                    [CanSignal(7, 11, ByteOrder = CanByteOrder.Motorola, IsSigned = true,
                        Factor = 0.5, Unit = "A", Description = "Pack current, DBC big-endian")]
                    public partial double Current { get; }

                    [CanSignal(7, 10, ByteOrder = CanByteOrder.Motorola,
                        Description = "State of charge, DBC big-endian")]
                    public partial int Soc { get; }

                    [CanSignal(11, 2, ByteOrder = CanByteOrder.Motorola,
                        Description = "Relay cut request")]
                    public partial bool RelayCutRequested { get; }

                    [CanSignal(32, 8, Description = "Left as Intel, to prove the default holds")]
                    public partial int IntelByte { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);
            await Verify(result);
        }

        /// <summary>
        /// A multiplexed frame reuses the same bit positions for different signals depending on a
        /// selector. Modelled on Leaf 0x5C0, where a two-bit flag says whether the frame carries
        /// the minimum, maximum or average of the battery's recorded history - the same bytes
        /// meaning three different things.
        ///
        /// The generated Parse must read the selector first and populate only the matching
        /// variant, leaving the others null. Null rather than default matters: zero is a
        /// legitimate reading for these fields, so a default would be indistinguishable from a
        /// real measurement.
        /// </summary>
        [Test]
        public async Task GeneratesMultiplexedSignals()
        {
            var source = """
            using ObdInsight.SourceGeneration.Attributes;

            namespace TestNamespace
            {
                [CanFrame(0x5C0)]
                public partial class HistoryFrame
                {
                    [CanSignal(6, 2, IsMultiplexor = true,
                        Description = "Selects which history variant this frame carries")]
                    public partial int HistoricalDataSwitchFlag { get; }

                    [CanSignal(17, 7, MuxValue = 1, Offset = -40.0, Unit = "degC",
                        Description = "Highest recorded pack temperature")]
                    public partial double? TemperatureMax { get; }

                    [CanSignal(17, 7, MuxValue = 3, Offset = -40.0, Unit = "degC",
                        Description = "Lowest recorded pack temperature")]
                    public partial double? TemperatureMin { get; }

                    [CanSignal(42, 6, MuxValue = 2, Factor = 40.0, Offset = 1900.0, Unit = "mV",
                        Description = "Average recorded cell voltage")]
                    public partial int? CellVoltageAvg { get; }

                    [CanSignal(24, 8,
                        Description = "Present in every frame regardless of the selector")]
                    public partial int AlwaysPresent { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);
            await Verify(result);
        }

        [Test]
        public async Task GeneratesSignedScaledDoubleSignal()
        {
            // Regression case: signed signal with Factor decoding into a double property.
            // The signed raw value must stay negative through scaling (Leaf 0x1DB battery
            // current shape: negative = charging).
            var source = """
            using ObdInsight.SourceGeneration.Attributes;

            namespace TestNamespace
            {
                [CanFrame(0x1DB)]
                public partial class BatteryFrame
                {
                    [CanSignal(13, 11, IsSigned = true, Factor = 0.5, Unit = "A",
                        Description = "Battery current (positive=discharge, negative=charge)")]
                    public partial double Current { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);
            await Verify(result);
        }

        [Test]
        public async Task ImplementsICanFrame_WhenCoreInterfaceIsPresent()
        {
            // When the compilation defines ObdInsight.Core.Protocols.ICanFrame<TSelf>,
            // generated frames must implement it (typed CanMonitor subscriptions).
            // All other snapshot tests compile WITHOUT the interface and verify the
            // interface-free output stays unchanged.
            var source = """
            using System;
            using ObdInsight.SourceGeneration.Attributes;

            namespace ObdInsight.Core.Protocols
            {
                public interface ICanFrame<TSelf> where TSelf : ICanFrame<TSelf>
                {
                    static abstract int FrameCanId { get; }
                    static abstract int MinimumLength { get; }
                    static abstract TSelf Parse(ReadOnlySpan<byte> data);
                }
            }

            namespace TestNamespace
            {
                [CanFrame(0x1DB)]
                public partial class BatteryFrame
                {
                    [CanSignal(30, 10, Factor = 0.5, Unit = "V")]
                    public partial double Voltage { get; }
                }
            }
            """;

            var result = GeneratorTestHelper.RunGenerator(source);

            var generated = GeneratorTestHelper.GetGeneratedSource(result);
            await Assert.That(generated).Contains("ICanFrame<BatteryFrame>");
            await Assert.That(generated).Contains("public static int FrameCanId => 0x1DB;");
            // Signal at bits 30-39 reaches byte 4, so the frame decodes from 5 bytes up.
            await Assert.That(generated).Contains("public static int MinimumLength => 5;");

            await Verify(result);
        }

        [Test]
        public async Task GeneratesUnsignedIntSignal()
        {
            var source = """
            using ObdInsight.SourceGeneration.Attributes;

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
            using ObdInsight.SourceGeneration.Attributes;

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
            using ObdInsight.SourceGeneration.Attributes;

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
            using ObdInsight.SourceGeneration.Attributes;

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
