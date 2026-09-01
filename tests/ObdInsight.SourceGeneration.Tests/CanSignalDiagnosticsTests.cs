using Microsoft.CodeAnalysis;

namespace ObdInsight.SourceGeneration.Tests
{
    /// <summary>
    ///     Covers the generator's rejection of signal layouts the generated decoder cannot honour.
    ///     Both would otherwise decode the wrong bits silently: the reader's shift count is masked to
    ///     6 bits, so a start bit at or past 64 wraps, and a mask wider than 32 bits is truncated when
    ///     the reader returns uint.
    /// </summary>
    public class CanSignalDiagnosticsTests
    {
        [Test]
        public async Task SignalStartingPastThePayload_IsAnError()
        {
            var result = GeneratorTestHelper.RunGenerator(Frame("[CanSignal(64, 8)]", "int Overflowing"));

            var diagnostic = result.Diagnostics.Single();
            await Assert.That(diagnostic.Id).IsEqualTo("OBDCAN001");
            await Assert.That(diagnostic.Severity).IsEqualTo(DiagnosticSeverity.Error);
            await Assert.That(diagnostic.GetMessage()).Contains("Overflowing");
        }

        [Test]
        public async Task SignalRunningPastThePayload_IsAnError()
        {
            var result = GeneratorTestHelper.RunGenerator(Frame("[CanSignal(60, 8)]", "int Straddling"));

            await Assert.That(result.Diagnostics.Single().Id).IsEqualTo("OBDCAN001");
        }

        [Test]
        public async Task NegativeOrEmptyBitRange_IsAnError()
        {
            // Both are range faults rather than width faults, so both report OBDCAN001.
            var negative = GeneratorTestHelper.RunGenerator(Frame("[CanSignal(-1, 8)]", "int Negative"));
            var empty = GeneratorTestHelper.RunGenerator(Frame("[CanSignal(0, 0)]", "int Empty"));

            await Assert.That(negative.Diagnostics.Single().Id).IsEqualTo("OBDCAN001");
            await Assert.That(empty.Diagnostics.Single().Id).IsEqualTo("OBDCAN001");
        }

        [Test]
        public async Task SignalWiderThanTheDecoder_IsAnError()
        {
            var result = GeneratorTestHelper.RunGenerator(Frame("[CanSignal(0, 40)]", "int TooWide"));

            var diagnostic = result.Diagnostics.Single();
            await Assert.That(diagnostic.Id).IsEqualTo("OBDCAN002");
            await Assert.That(diagnostic.GetMessage()).Contains("40");
        }

        [Test]
        public async Task SignalsFillingThePayloadExactly_AreAccepted()
        {
            // 32..63 is the widest legal signal: it ends on the last payload bit.
            var result = GeneratorTestHelper.RunGenerator(Frame("[CanSignal(32, 32)]", "int Widest"));

            await Assert.That(result.Diagnostics).IsEmpty();
        }

        [Test]
        public async Task TheDiagnosticPointsAtTheDeclaration()
        {
            var result = GeneratorTestHelper.RunGenerator(Frame("[CanSignal(64, 8)]", "int Overflowing"));

            // Rebuilt from the captured span, so it is an external-file location rather than a
            // syntax-tree one (the harness compiles from a string, so the path is empty), but it
            // still resolves to the property declaration - line 8 of the template below.
            var lineSpan = result.Diagnostics.Single().Location.GetLineSpan();
            await Assert.That(lineSpan.StartLinePosition.Line).IsEqualTo(8);
        }

        private static string Frame(string signalAttribute, string declaration) => $$"""
              using ObdInsight.SourceGeneration.Attributes;

              namespace TestNamespace
              {
                  [CanFrame(0x54C)]
                  public partial class TestFrame
                  {
                      {{signalAttribute}}
                      public partial {{declaration}} { get; init; }
                  }
              }
              """;
    }
}
