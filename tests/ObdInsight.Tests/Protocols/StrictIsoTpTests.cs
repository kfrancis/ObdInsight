using ObdInsight.Core.Protocols;

namespace ObdInsight.Tests.Protocols;

public class StrictIsoTpTests
{
    [Test]
    [Arguments("7BB056101")]
    [Arguments("7BB0361010")]
    [Arguments("7BB036101GG")]
    [Arguments("7BB100A610100000000\r7BB2201020304050607")]
    [Arguments("7BB100A610100000000\r7BC2101020304050607")]
    [Arguments("7BB100A610100000000")]
    [Arguments("7BB03610101\r7BB03610101")]
    [Arguments("610100")]
    [Arguments("7BB03610101\rBUFFER FULL")]
    public async Task Corruption_ExposesNoUsableSinglePayload(string input)
    {
        await Assert.That(IsoTpParser.TryReadPayload([input], out var payload)).IsFalse();
        await Assert.That(payload).IsEmpty();
        await Assert.That(IsoTpParser.ParseIsoTpResponse(input)).IsEmpty();
    }

    [Test]
    public async Task Batch_PreservesResponderFailureAndExpectedLength()
    {
        var result = IsoTpParser.ParseResponses(["7BB100A610100000000", "7BC03610101"]);
        var failed = result.Responses.Single(r => r.CanId == 0x7BB);
        await Assert.That(failed.ExpectedLength).IsEqualTo(10);
        await Assert.That(failed.Error).IsEqualTo(IsoTpError.Incomplete);
        await Assert.That(failed.Payload.IsEmpty).IsTrue();
        await Assert.That(result.Responses.Single(r => r.CanId == 0x7BC).Error).IsEqualTo(IsoTpError.None);
    }

    [Test]
    public async Task MultipleValidResponders_AreNotAnUnambiguousSinglePayload()
    {
        await Assert.That(IsoTpParser.TryReadPayload(["7BB03610101", "7BC03610102"], out _)).IsFalse();
    }

    [Test]
    public async Task WrongOrWildcardExpectedResponder_FailsClosed()
    {
        await Assert.That(IsoTpParser.TryReadPayload(["7BC03610101"], out _, "7BB")).IsFalse();
        await Assert.That(IsoTpParser.TryReadPayload(["7BB03610101"], out _, "7BX")).IsFalse();
    }

    [Test]
    public async Task ExplicitEchoAndSpacedFrames_AreSupported()
    {
        await Assert.That(IsoTpParser.TryReadPayload(["2101", "SEARCHING...", "7BB 03 61 01 01\r>"],
            out var payload, "7BB", "2101")).IsTrue();
        await Assert.That(payload).IsEquivalentTo(new byte[] { 0x61, 0x01, 0x01 });
    }

    [Test]
    public async Task MalformedRawHex_DoesNotReturnAPrefix()
    {
        await Assert.That(IsoTpParser.ParseHexString("0102Z3")).IsEmpty();
        await Assert.That(IsoTpParser.ParseHexString("01020")).IsEmpty();
    }
}
