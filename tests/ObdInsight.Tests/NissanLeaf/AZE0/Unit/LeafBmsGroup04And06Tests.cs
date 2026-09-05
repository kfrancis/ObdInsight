using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
///     BMS Group 04 (pack temperatures) and Group 06 (cell shunt/balancing) parsing over the
///     production path (LeafAze0CommandSet → LeafAze0Bms → generated LeafBmsDiagnostics).
///     Golden bytes captured 2025-12-06 on the same 30kWh AZE0 (third-party app log);
///     layouts verified against OVMS PollReply_BMS_Temp / PollReply_BMS_Shunt.
/// </summary>
[Timeout(30_000)]
public class LeafBmsGroup04And06Tests
{
    private static (ReplayElmTransport Transport, IBatteryManagementSystem Bms) CreateBms()
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var commands = new LeafAze0CommandSet(session);
        commands.TryGet<IBatteryManagementSystem>(out var bms);
        return (transport, bms!);
    }

    [Test]
    public async Task GetStatus_WithGroup04_PopulatesPackTemperatures(CancellationToken token)
    {
        var (transport, bms) = CreateBms();
        transport.Expect("2101", LeafGoldenData.GoldenGroup01Lines.AsElmResponse());
        transport.Expect("2104", LeafGoldenData.GoldenGroup04Lines.AsElmResponse());

        var status = await bms.GetStatusAsync(token);

        // Sensors (Dec capture): ADC 691 => 1.938 °C, 686 => 2.448, absent (FFFF), 697 => 1.326.
        await Assert.That(status.TemperatureC).IsNotNull();
        await Assert.That(status.TemperatureC!.Value).IsEqualTo(1.904).Within(0.001);
        await Assert.That(status.MinTemperatureC!.Value).IsEqualTo(1.326).Within(0.001);
        await Assert.That(status.MaxTemperatureC!.Value).IsEqualTo(2.448).Within(0.001);
        // Core group-01 values still intact alongside the second query.
        await Assert.That(status.VoltageVolts).IsNotNull();
    }

    [Test]
    public async Task GetStatus_WhenGroup04Unavailable_TemperaturesNullNotThrow(CancellationToken token)
    {
        var (transport, bms) = CreateBms();
        // An actual adapter no-data response is missing data, not a programming error.
        transport.Expect("2101", LeafGoldenData.GoldenGroup01Lines.AsElmResponse());
        transport.AutoRespond("2104", "NO DATA\r\r>");

        var status = await bms.GetStatusAsync(token);

        await Assert.That(status.VoltageVolts).IsNotNull();
        await Assert.That(status.TemperatureC).IsNull();
        await Assert.That(status.MinTemperatureC).IsNull();
        await Assert.That(status.MaxTemperatureC).IsNull();
    }

    [Test]
    public async Task GetCellVoltages_WithGroup06_PopulatesBalancing(CancellationToken token)
    {
        var (transport, bms) = CreateBms();
        transport.Expect("2102", LeafGoldenData.GoldenGroup02Lines.AsElmResponse());
        transport.Expect("2106", LeafGoldenData.GoldenGroup06Lines.AsElmResponse());

        var cells = await bms.GetCellVoltagesAsync(token);

        await Assert.That(cells).IsNotNull();
        await Assert.That(cells!.CellCount).IsEqualTo(96);
        await Assert.That(cells.CellVoltagesMv[0]).IsEqualTo(0x0F3D); // 3901 mV
        await Assert.That(cells.CellVoltagesMv[1]).IsEqualTo(0x0F42); // 3906 mV
        await Assert.That(cells.MinVoltageMv).IsEqualTo(0x0F3B); // 3899 mV
        await Assert.That(cells.MaxVoltageMv).IsEqualTo(0x0F47); // 3911 mV

        // Shunt bytes: 0F 0E 0E 0E 0F 0A 07 ... 06 0E 06 06 0E 0F.
        // OVMS convention: balancing = wire bit CLEAR.
        await Assert.That(cells.BalancingCells).IsNotNull();
        await Assert.That(cells.BalancingCells!.Count).IsEqualTo(96);
        await Assert.That(cells.BalancingCells[0]).IsFalse(); // byte0 0x0F: all bits set
        await Assert.That(cells.BalancingCells[3]).IsFalse();
        await Assert.That(cells.BalancingCells[7]).IsTrue(); // byte1 0x0E: 0x01 clear => cell 7
        await Assert.That(cells.BalancingCells[72]).IsTrue(); // byte18 0x06: 0x08 clear => cell 72
        await Assert.That(cells.BalancingCells[73]).IsFalse();
        await Assert.That(cells.BalancingCells[75]).IsTrue(); // byte18 0x06: 0x01 clear => cell 75
        await Assert.That(cells.BalancingCellCount).IsEqualTo(18);
    }

    [Test]
    public async Task GetCellVoltages_WhenGroup06Unavailable_BalancingNullNotThrow(CancellationToken token)
    {
        var (transport, bms) = CreateBms();
        transport.Expect("2102", LeafGoldenData.GoldenGroup02Lines.AsElmResponse());
        transport.AutoRespond("2106", "NO DATA\r\r>");

        var cells = await bms.GetCellVoltagesAsync(token);

        await Assert.That(cells).IsNotNull();
        await Assert.That(cells!.CellCount).IsEqualTo(96);
        await Assert.That(cells.BalancingCells).IsNull();
        await Assert.That(cells.BalancingCellCount).IsNull();
    }
}
