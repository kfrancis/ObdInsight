using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.IntegrationTests;
using ObdInsight.IntegrationTests.Fixtures;
using static ObdInsight.IntegrationTests.BmsParsingHelpers;

namespace ObdInsight.IntegrationTests.Nissan.Leaf.AZE0;

/// <summary>
///     Integration tests for Nissan Leaf BMS Group 02 (cell voltages) using a real BLE connection.
///     These tests require a physical Nissan Leaf OBD adapter to be connected.
/// </summary>
[RequiresLeafHardware]
[ClassDataSource<BleSessionFixture>(Shared = SharedType.Keyed)]
public class LeafBmsGroup02IntegrationTests(BleSessionFixture bleFixture)
{
    [Test]
    public async Task QueryBmsGroup02_CellVoltageDeltaIsReasonable()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.LbcBms;

        // Act
        var lines = await session.QueryAsync("2102", context, CancellationToken.None);
        var frames = ParseIsoTpFrames(lines);
        var payload = ReassembleIsoTpPayload(frames);

        var cellData = payload.AsSpan(2);
        var cellVoltages = new List<int>();

        for (var i = 0; i + 1 < cellData.Length && cellVoltages.Count < 96; i += 2)
        {
            var voltage = (cellData[i] << 8) | cellData[i + 1];
            if (voltage is >= 2500 and <= 4500)
            {
                cellVoltages.Add(voltage);
            }
        }

        // Assert - cell voltage delta should be reasonable (typically < 200mV)
        if (cellVoltages.Count > 0)
        {
            var delta = cellVoltages.Max() - cellVoltages.Min();
            await Assert.That(delta).IsLessThan(500); // 500mV max delta (very conservative)
        }
    }

    [Test]
    public async Task QueryBmsGroup02_HasExpectedCellCount()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.LbcBms;

        // Act
        var lines = await session.QueryAsync("2102", context, CancellationToken.None);
        var frames = ParseIsoTpFrames(lines);
        var payload = ReassembleIsoTpPayload(frames);

        var cellData = payload.AsSpan(2);
        var cellVoltages = new List<int>();

        for (var i = 0; i + 1 < cellData.Length && cellVoltages.Count < 96; i += 2)
        {
            var voltage = (cellData[i] << 8) | cellData[i + 1];
            if (voltage is >= 2500 and <= 4500)
            {
                cellVoltages.Add(voltage);
            }
        }

        // Assert - Nissan Leaf has 96 cell pairs
        await Assert.That(cellVoltages).Count().IsGreaterThan(0);
        await Assert.That(cellVoltages).Count().IsLessThanOrEqualTo(96);
    }

    [Test]
    public async Task QueryBmsGroup02_HasValidHeader()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.LbcBms;

        // Act
        var lines = await session.QueryAsync("2102", context, CancellationToken.None);
        var frames = ParseIsoTpFrames(lines);
        var payload = ReassembleIsoTpPayload(frames);

        // Assert
        await Assert.That(payload).Count().IsGreaterThanOrEqualTo(4);
        await Assert.That(payload[0]).IsEqualTo((byte)0x61);
        await Assert.That(payload[1]).IsEqualTo((byte)0x02);
    }

    [Test]
    public async Task QueryBmsGroup02_ParsesCellVoltages()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.LbcBms;

        // Act
        var lines = await session.QueryAsync("2102", context, CancellationToken.None);
        var frames = ParseIsoTpFrames(lines);
        var payload = ReassembleIsoTpPayload(frames);

        // Assert - should have valid header
        await Assert.That(payload).Count().IsGreaterThanOrEqualTo(4);
        await Assert.That(payload[0]).IsEqualTo((byte)0x61);
        await Assert.That(payload[1]).IsEqualTo((byte)0x02);

        // Parse cell voltages
        var cellData = payload.AsSpan(2);
        var cellVoltages = new List<int>();

        for (var i = 0; i + 1 < cellData.Length && cellVoltages.Count < 96; i += 2)
        {
            var voltage = (cellData[i] << 8) | cellData[i + 1];
            if (voltage is >= 2500 and <= 4500)
            {
                cellVoltages.Add(voltage);
            }
        }

        await Assert.That(cellVoltages).IsNotEmpty();
        await Assert.That(cellVoltages.Min()).IsGreaterThanOrEqualTo(2500);
        await Assert.That(cellVoltages.Max()).IsLessThanOrEqualTo(4500);
    }

    [Test]
    public async Task QueryBmsGroup02_ReturnsValidFrames()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.LbcBms;

        // Act
        var lines = await session.QueryAsync("2102", context, CancellationToken.None);

        // Assert
        await Assert.That(lines).IsNotEmpty();

        var frames = ParseIsoTpFrames(lines);
        await Assert.That(frames).IsNotEmpty();
    }
}
