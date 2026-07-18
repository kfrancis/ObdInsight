using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using OdbTestApp.Tests.Fixtures;
using static ObdInsight.IntegrationTests.BmsParsingHelpers;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Integration;

/// <summary>
/// Integration tests for Nissan Leaf BMS Group 01 using a real BLE connection.
/// These tests require a physical Nissan Leaf OBD adapter to be connected.
/// </summary>
[ObdInsight.IntegrationTests.RequiresLeafHardware]
[ClassDataSource<BleSessionFixture>(Shared = SharedType.Keyed)]
public class LeafBmsGroup01IntegrationTests(BleSessionFixture bleFixture)
{
    [Test]
    public async Task QueryBmsGroup01_CapacityInValidRange()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.LbcBms;

        // Act
        var lines = await session.QueryAsync("2101", context, CancellationToken.None);
        var frames = ParseIsoTpFrames(lines);
        var result = ParseGroup01FromFrames(frames);

        // Assert
        await Assert.That(result.CapacityAh).IsNotNull();
        await Assert.That(result.CapacityAh!.Value).IsGreaterThan(10.0); // Minimum plausible capacity
        await Assert.That(result.CapacityAh!.Value).IsLessThan(100.0); // Maximum plausible capacity
    }

    [Test]
    public async Task QueryBmsGroup01_CurrentInReasonableRange()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.LbcBms;

        // Act
        var lines = await session.QueryAsync("2101", context, CancellationToken.None);
        var frames = ParseIsoTpFrames(lines);
        var result = ParseGroup01FromFrames(frames);

        // Assert
        await Assert.That(result.CurrentAmps).IsNotNull();
        await Assert.That(Math.Abs(result.CurrentAmps!.Value)).IsLessThan(500.0); // Reasonable current range
    }

    [Test]
    public async Task QueryBmsGroup01_HxInValidRange()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.LbcBms;

        // Act
        var lines = await session.QueryAsync("2101", context, CancellationToken.None);
        var frames = ParseIsoTpFrames(lines);
        var result = ParseGroup01FromFrames(frames);

        // Assert
        await Assert.That(result.HxPercent).IsNotNull();
        await Assert.That(result.HxPercent!.Value).IsGreaterThan(0.0);
        await Assert.That(result.HxPercent!.Value).IsLessThanOrEqualTo(100.0);
    }

    [Test]
    public async Task QueryBmsGroup01_ReturnsValidData()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.LbcBms;

        // Act
        var lines = await session.QueryAsync("2101", context, CancellationToken.None);

        // Assert
        await Assert.That(lines).IsNotEmpty();

        var frames = ParseIsoTpFrames(lines);
        await Assert.That(frames).IsNotEmpty();

        var result = ParseGroup01FromFrames(frames);
        await Assert.That(result.VoltageVolts).IsNotNull();
        await Assert.That(result.VoltageVolts!.Value).IsGreaterThan(300.0); // Reasonable voltage range
        await Assert.That(result.VoltageVolts!.Value).IsLessThan(450.0);
    }
}
