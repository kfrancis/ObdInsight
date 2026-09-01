using ObdInsight.Core.Protocols;
using ObdInsight.Simulation;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
///     Unit tests for the PRODUCTION ISO-TP parser (ObdInsight.Core.Protocols.IsoTpParser)
///     using golden Nissan Leaf sample data. No BLE required.
/// </summary>
public class LeafIsoTpParsingTests
{
    [Test]
    public async Task ParseIsoTpResponse_ReassemblesGroup01Payload()
    {
        var response = string.Join("\r", LeafGoldenData.GoldenGroup01Lines);

        var payload = IsoTpParser.ParseIsoTpResponse(response);

        // First Frame declares 0x2B (43) bytes; reassembly must trim to exactly that.
        await Assert.That(payload).Count().IsEqualTo(43);
    }

    [Test]
    public async Task ParseIsoTpResponse_Group01_HasValidHeader()
    {
        var response = string.Join("\r", LeafGoldenData.GoldenGroup01Lines);

        var payload = IsoTpParser.ParseIsoTpResponse(response);

        // Positive response to Mode 21 PID 01 = [61 01]
        await Assert.That(payload[0]).IsEqualTo((byte)0x61);
        await Assert.That(payload[1]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task ParseIsoTpResponse_ReassemblesVinPayload()
    {
        var response = string.Join("\r", LeafGoldenData.GoldenVinLines);

        var payload = IsoTpParser.ParseIsoTpResponse(response);

        // First Frame declares 0x15 (21) bytes, header [61 81].
        await Assert.That(payload).Count().IsEqualTo(21);
        await Assert.That(payload[0]).IsEqualTo((byte)0x61);
        await Assert.That(payload[1]).IsEqualTo((byte)0x81);
    }

    [Test]
    public async Task ParseIsoTpResponse_HandlesFramesConcatenatedOnOneLine()
    {
        // Some adapters emit multiple frames on a single line with no separator.
        var response = string.Concat(LeafGoldenData.GoldenGroup01Lines);

        var payload = IsoTpParser.ParseIsoTpResponse(response);

        await Assert.That(payload).Count().IsEqualTo(43);
        await Assert.That(payload[0]).IsEqualTo((byte)0x61);
        await Assert.That(payload[1]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task ParseIsoTpResponse_EmptyInput_ReturnsEmpty()
    {
        await Assert.That(IsoTpParser.ParseIsoTpResponse("")).Count().IsEqualTo(0);
        await Assert.That(IsoTpParser.ParseIsoTpResponse("   ")).Count().IsEqualTo(0);
    }
}
