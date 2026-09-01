namespace ObdInsight.SourceGeneration.Tests;

public class UdsGeneratorTests
{
    [Test]
    public async Task GeneratesSimpleUdsQuery()
    {
        var source = """
                     using ObdInsight.SourceGeneration.Attributes;
                     using System.Threading;
                     using System.Threading.Tasks;
                     using System.Collections.Generic;

                     namespace TestNamespace
                     {
                         public interface IElmSession
                         {
                             Task<string[]> QueryAsync(string command, object context, CancellationToken ct);
                         }

                         public class EcuContext { }

                         [UdsService(0x21, EcuType = "BMS")]
                         public partial class TestDiagnostics
                         {
                             private readonly IElmSession _session;
                             private readonly EcuContext _context;

                             public static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFrames(string[] lines) => new();
                             public static byte[] ReassembleIsoTpPayload(List<(int FrameType, int SeqOrLen, byte[] Data)> frames) => [];

                             [UdsPid(0x01, Name = "Status")]
                             [UdsResponse(MinLength = 10, MaxLength = 50)]
                             public partial class StatusResponse
                             {
                                 [UdsField(Offset = 0, Length = 2, Type = UdsFieldType.UInt16BE, Scale = 0.01)]
                                 public double Voltage { get; set; }
                             }
                         }
                     }
                     """;

        var result = GeneratorTestHelper.RunGenerator<UdsGenerator>(source);

        // Verify no diagnostics
        await Assert.That(result.Diagnostics.Length).IsEqualTo(0);

        // Verify source was generated
        await Assert.That(result.Results[0].GeneratedSources.Length).IsGreaterThanOrEqualTo(1);

        // Snapshot test the generated code
        await Verify(result);
    }

    [Test]
    public async Task GeneratesArrayFieldExtraction()
    {
        var source = """
                     using ObdInsight.SourceGeneration.Attributes;
                     using System.Threading;
                     using System.Threading.Tasks;
                     using System.Collections.Generic;

                     namespace TestNamespace
                     {
                         public interface IElmSession
                         {
                             Task<string[]> QueryAsync(string command, object context, CancellationToken ct);
                         }

                         public class EcuContext { }

                         [UdsService(0x21)]
                         public partial class CellDiagnostics
                         {
                             private readonly IElmSession _session;
                             private readonly EcuContext _context;

                             public static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFrames(string[] lines) => new();
                             public static byte[] ReassembleIsoTpPayload(List<(int FrameType, int SeqOrLen, byte[] Data)> frames) => [];

                             [UdsPid(0x02)]
                             public partial class CellVoltagesResponse
                             {
                                 [UdsArrayField(Offset = 0, ElementCount = 96, ElementLength = 2, Type = UdsFieldType.UInt16BE, ValidRange = "2500..4500")]
                                 public int[] CellVoltagesMv { get; set; } = [];

                                 [UdsComputed]
                                 public int MinVoltageMv => CellVoltagesMv.Length > 0 ? CellVoltagesMv.Min() : 0;
                             }
                         }
                     }
                     """;

        var result = GeneratorTestHelper.RunGenerator<UdsGenerator>(source);
        await Verify(result);
    }

    [Test]
    public async Task GeneratesVariantDetection()
    {
        var source = """
                     using ObdInsight.SourceGeneration.Attributes;
                     using System.Threading;
                     using System.Threading.Tasks;
                     using System.Collections.Generic;

                     namespace TestNamespace
                     {
                         public interface IElmSession
                         {
                             Task<string[]> QueryAsync(string command, object context, CancellationToken ct);
                         }

                         public class EcuContext { }

                         [UdsService(0x21)]
                         public partial class VariantDiagnostics
                         {
                             private readonly IElmSession _session;
                             private readonly EcuContext _context;

                             public static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFrames(string[] lines) => new();
                             public static byte[] ReassembleIsoTpPayload(List<(int FrameType, int SeqOrLen, byte[] Data)> frames) => [];

                             [UdsPid(0x01)]
                             [UdsResponseVariant(Length = 39, Model = "24kWh")]
                             [UdsResponseVariant(Length = 49, Model = "40kWh")]
                             public partial class StatusResponse
                             {
                                 [UdsField(Offset = 26, Length = 2, Type = UdsFieldType.UInt16BE, Scale = 0.01, AppliesTo = "24kWh")]
                                 [UdsField(Offset = 28, Length = 2, Type = UdsFieldType.UInt16BE, Scale = 1.0/102.4, AppliesTo = "40kWh")]
                                 public double HealthPercent { get; set; }
                             }
                         }
                     }
                     """;

        var result = GeneratorTestHelper.RunGenerator<UdsGenerator>(source);
        await Verify(result);
    }

    [Test]
    public async Task GeneratesConsecutiveFrameSourcedField()
    {
        // Regression: FrameType is an enum named argument, which Roslyn surfaces as a boxed
        // int. The generator must map it back to the member name — otherwise the
        // "ConsecutiveFrame" branch never matches and the field silently decodes from the
        // wrong bytes (payload offset 0). This is how Leaf BMS VoltageVolts read as 0.
        var source = """
                     using ObdInsight.SourceGeneration.Attributes;
                     using System.Threading;
                     using System.Threading.Tasks;
                     using System.Collections.Generic;

                     namespace TestNamespace
                     {
                         public interface IElmSession
                         {
                             Task<string[]> QueryAsync(string command, object context, CancellationToken ct);
                         }

                         public class EcuContext { }

                         [UdsService(0x21)]
                         public partial class FrameSourceDiagnostics
                         {
                             private readonly IElmSession _session;
                             private readonly EcuContext _context;

                             public static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFrames(string[] lines) => new();
                             public static byte[] ReassembleIsoTpPayload(List<(int FrameType, int SeqOrLen, byte[] Data)> frames) => [];

                             [UdsPid(0x01)]
                             public partial class StatusResponse
                             {
                                 [UdsField(FrameType = FrameSource.ConsecutiveFrame, FrameSequence = 3, Offset = 0, Length = 2,
                                     Type = UdsFieldType.UInt16BE, Scale = 0.01)]
                                 public double VoltageVolts { get; set; }
                             }
                         }
                     }
                     """;

        var result = GeneratorTestHelper.RunGenerator<UdsGenerator>(source);

        var generated = GeneratorTestHelper.GetGeneratedSource(result);
        await Assert.That(generated).Contains("f.FrameType == 2 && f.SeqOrLen == 3");

        await Verify(result);
    }

    [Test]
    public async Task GeneratesSignedInt32Conversion()
    {
        var source = """
                     using ObdInsight.SourceGeneration.Attributes;
                     using System.Threading;
                     using System.Threading.Tasks;
                     using System.Collections.Generic;

                     namespace TestNamespace
                     {
                         public interface IElmSession
                         {
                             Task<string[]> QueryAsync(string command, object context, CancellationToken ct);
                         }

                         public class EcuContext { }

                         [UdsService(0x21)]
                         public partial class CurrentDiagnostics
                         {
                             private readonly IElmSession _session;
                             private readonly EcuContext _context;

                             public static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFrames(string[] lines) => new();
                             public static byte[] ReassembleIsoTpPayload(List<(int FrameType, int SeqOrLen, byte[] Data)> frames) => [];

                             [UdsPid(0x01)]
                             public partial class StatusResponse
                             {
                                 [UdsField(Offset = 0, Length = 4, Type = UdsFieldType.Int32BE, Scale = 1.0/1024.0)]
                                 public double CurrentAmps { get; set; }
                             }
                         }
                     }
                     """;

        var result = GeneratorTestHelper.RunGenerator<UdsGenerator>(source);
        await Verify(result);
    }
}
