using ObdTestApp.Vehicles;
using OdbTestApp.Tests.Fixtures;
using static OdbTestApp.Tests.NissanLeaf.LeafBmsParsingHelpers;

namespace OdbTestApp.Tests.NissanLeaf;

/// <summary>
/// Unit tests for Nissan Leaf BMS Group 01 parsing using golden sample data.
/// These tests validate the parsing logic without requiring BLE connectivity.
/// </summary>
public class LeafBmsGroup01ParsingTests
{
    [Test]
    public async Task ParseGroup01_AllFieldsPresent()
    {
        // Arrange
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);

        // Act
        var result = ParseGroup01FromFrames(frames);

        // Assert
        await Assert.That(result.VoltageVolts).IsNotNull();
        await Assert.That(result.CurrentAmps).IsNotNull();
        await Assert.That(result.HxPercent).IsNotNull();
        await Assert.That(result.CapacityAh).IsNotNull();
    }

    [Test]
    public async Task ParseGroup01_ExtractsCapacity()
    {
        // Arrange
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);

        // Act
        var result = ParseGroup01FromFrames(frames);

        // Assert
        var expectedAhr = 0x0805C1 / 10000.0; // 52.58 Ah
        await Assert.That(result.CapacityAh).IsNotNull();
        await Assert.That(Math.Abs(result.CapacityAh!.Value - expectedAhr)).IsLessThan(0.1);
    }

    [Test]
    public async Task ParseGroup01_ExtractsCurrent()
    {
        // Arrange
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);

        // Act
        var result = ParseGroup01FromFrames(frames);

        // Assert
        var expectedCurrent = 0xEB / 1024.0; // ~0.229A
        await Assert.That(result.CurrentAmps).IsNotNull();
        await Assert.That(Math.Abs(result.CurrentAmps!.Value - expectedCurrent)).IsLessThan(0.01);
    }

    [Test]
    public async Task ParseGroup01_ExtractsHx()
    {
        // Arrange
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);

        // Act
        var result = ParseGroup01FromFrames(frames);

        // Assert - 24/30kWh format uses /100 divisor
        var expectedHx = 0x0DD8 / 100.0; // 35.44%
        await Assert.That(result.HxPercent).IsNotNull();
        await Assert.That(Math.Abs(result.HxPercent!.Value - expectedHx)).IsLessThan(0.1);
    }

    [Test]
    public async Task ParseGroup01_ExtractsVoltage()
    {
        // Arrange
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);

        // Act
        var result = ParseGroup01FromFrames(frames);

        // Assert
        var expectedVoltage = 0x8D52 / 100.0; // 361.78V
        await Assert.That(result.VoltageVolts).IsNotNull();
        await Assert.That(Math.Abs(result.VoltageVolts!.Value - expectedVoltage)).IsLessThan(0.01);
    }

    [Test]
    public async Task ParseGroup01_SocIsNull_For24And30kWhLeaf()
    {
        // Arrange
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);

        // Act
        var result = ParseGroup01FromFrames(frames);

        // Assert - SOC should be null for 24/30kWh Leaf (must use passive CAN)
        await Assert.That(result.SocPercent).IsNull();
    }
}

/// <summary>
/// Integration tests for Nissan Leaf BMS Group 01 using a real BLE connection.
/// These tests require a physical Nissan Leaf OBD adapter to be connected.
/// </summary>
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
