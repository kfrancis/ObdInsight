using static ObdInsight.Tests.Base.BmsParsingHelpers;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
/// Unit tests for Nissan Leaf ISO-TP frame parsing.
/// These tests use golden sample data and do not require BLE connectivity.
/// </summary>
public class LeafIsoTpParsingTests
{
    [Test]
    public async Task ParseIsoTpFrames_ExtractsConsecutiveFrameCount()
    {
        // Arrange
        var lines = GoldenGroup01Lines;

        // Act
        var frames = ParseIsoTpFrames(lines);
        var consecutiveFrames = frames.Where(f => f.FrameType == 2).ToList();

        // Assert
        await Assert.That(consecutiveFrames).Count().IsEqualTo(6);
    }

    [Test]
    public async Task ParseIsoTpFrames_ExtractsCorrectFrameCount()
    {
        // Arrange
        var lines = GoldenGroup01Lines;

        // Act
        var frames = ParseIsoTpFrames(lines);

        // Assert
        await Assert.That(frames).Count().IsEqualTo(7);
    }

    [Test]
    public async Task ParseIsoTpFrames_ExtractsFirstFrameType()
    {
        // Arrange
        var lines = GoldenGroup01Lines;

        // Act
        var frames = ParseIsoTpFrames(lines);
        var firstFrame = frames.FirstOrDefault(f => f.FrameType == 1);

        // Assert
        await Assert.That(firstFrame).IsNotNull();
        await Assert.That(firstFrame!.FrameType).IsEqualTo(1);
    }

    [Test]
    public async Task ReassembleIsoTpPayload_FirstFrameContainsExpectedLength()
    {
        // Arrange
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);
        var firstFrame = frames.First(f => f.FrameType == 1);

        // Act & Assert
        await Assert.That(firstFrame.SeqOrLen).IsEqualTo(0x2B); // 43 bytes
    }

    [Test]
    public async Task ReassembleIsoTpPayload_HasValidHeader()
    {
        // Arrange
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);

        // Act
        var payload = ReassembleIsoTpPayload(frames);

        // Assert
        await Assert.That(payload[0]).IsEqualTo((byte)0x61);
        await Assert.That(payload[1]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task ReassembleIsoTpPayload_ProducesCorrectLength()
    {
        // Arrange
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);

        // Act
        var payload = ReassembleIsoTpPayload(frames);

        // Assert
        await Assert.That(payload).Count().IsEqualTo(43);
    }
}
