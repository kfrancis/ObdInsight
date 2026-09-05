using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
///     Unit tests for Nissan Leaf BMS Group 01 parsing using golden sample data.
///     Exercises the PRODUCTION path — LeafAze0CommandSet → LeafAze0Bms → generated
///     LeafBmsDiagnostics.QueryGroup01Async — over a replay transport. No BLE required.
/// </summary>
[Timeout(30_000)]
public class LeafBmsGroup01ParsingTests
{
    private static async Task<BatteryStatus> QueryGoldenStatusAsync(CancellationToken token)
    {
        var transport = new ReplayElmTransport();
        transport.Expect("2101", LeafGoldenData.GoldenGroup01Lines.AsElmResponse());

        var session = new ElmSession(new ElmFramer(transport));
        var commands = new LeafAze0CommandSet(session);

        await Assert.That(commands.TryGet<IBatteryManagementSystem>(out var bms)).IsTrue();
        return await bms.GetStatusAsync(token);
    }

    [Test]
    public async Task GetStatus_ExtractsVoltage(CancellationToken token)
    {
        var result = await QueryGoldenStatusAsync(token);

        var expectedVoltage = 0x8D52 / 100.0; // 361.78 V, from CF3 bytes 0-1
        await Assert.That(result.VoltageVolts).IsNotNull();
        await Assert.That(Math.Abs(result.VoltageVolts!.Value - expectedVoltage)).IsLessThan(0.01);
    }

    [Test]
    public async Task GetStatus_ExtractsCurrent(CancellationToken token)
    {
        var result = await QueryGoldenStatusAsync(token);

        var expectedCurrent = 0xEB / 1024.0; // ~0.229 A
        await Assert.That(result.CurrentAmps).IsNotNull();
        await Assert.That(Math.Abs(result.CurrentAmps!.Value - expectedCurrent)).IsLessThan(0.01);
    }

    [Test]
    public async Task GetStatus_DoesNotPublishHxAsStateOfHealth(CancellationToken token)
    {
        var result = await QueryGoldenStatusAsync(token);

        await Assert.That(result.StateOfHealthPercent).IsNull();
    }

    [Test]
    public async Task Group01_PreservesNissanHxAsDistinctMetric(CancellationToken token)
    {
        await using var transport = new ReplayElmTransport();
        transport.Expect("2101", LeafGoldenData.GoldenGroup01Lines.AsElmResponse());
        var diagnostics = new LeafBmsDiagnostics(new ElmSession(new ElmFramer(transport)), EcuContext.NissanLeafBms);
        var response = await diagnostics.QueryGroup01Async(token);
        await Assert.That(response!.HxPercent).IsEqualTo(35.44);
    }

    [Test]
    public async Task GetStatus_ExtractsCapacity(CancellationToken token)
    {
        var result = await QueryGoldenStatusAsync(token);

        var expectedAhr = 0x0805C1 / 10000.0; // 52.58 Ah
        await Assert.That(result.CapacityAh).IsNotNull();
        await Assert.That(Math.Abs(result.CapacityAh!.Value - expectedAhr)).IsLessThan(0.1);
    }

    [Test]
    public async Task GetStatus_ExtractsSoc_For30kWhLeaf(CancellationToken token)
    {
        var result = await QueryGoldenStatusAsync(token);

        // 24/30 kWh layout: SOC at payload offset 29-31, UInt24BE, 0.0001 %/bit
        // (ZE1 = AZE0 + 2 shift; see Group01Response remarks). 41.92 % at pack
        // 361.78 V ≈ 3.77 V/cell — plausible mid-charge. Hardware dash check pending.
        var expectedSoc = 0x06658A / 10000.0; // 41.92 %
        await Assert.That(result.SocPercent).IsNotNull();
        await Assert.That(Math.Abs(result.SocPercent!.Value - expectedSoc)).IsLessThan(0.01);
    }

    [Test]
    public async Task GetStatus_SendsBmsQueryToBmsEcu(CancellationToken token)
    {
        var transport = new ReplayElmTransport();
        transport.Expect("2101", LeafGoldenData.GoldenGroup01Lines.AsElmResponse());

        var session = new ElmSession(new ElmFramer(transport));
        var commands = new LeafAze0CommandSet(session);
        commands.TryGet<IBatteryManagementSystem>(out var bms);
        await bms.GetStatusAsync(token);

        // ECU context must target the LBC/BMS (TX 79B, RX filter 7BB) before the Mode 21 query.
        var sent = transport.SentCommands;
        await Assert.That(sent).Contains("AT SH 79B");
        await Assert.That(sent).Contains("AT CRA 7BB");
        await Assert.That(sent).Contains("2101");
    }
}
