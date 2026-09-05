using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;
using ObdInsight.Simulation;
using ObdInsight.Telemetry;
using ObdInsight.Telemetry.Providers;
using ObdInsight.Tests.Protocols;

namespace ObdInsight.Tests.NissanLeaf.AZE0.Unit;

public class StrictLeafDiagnosticsTests
{
    [Test]
    [Arguments(38)]
    [Arguments(40)]
    [Arguments(42)]
    [Arguments(52)]
    public async Task UnsupportedGroup01Length_CannotSelectClosestVariant(int dataLength)
    {
        var payload = IsoTpParser.ParseIsoTpResponse(LeafGoldenData.GoldenGroup01Lines.AsElmResponse()).ToArray();
        Array.Resize(ref payload, dataLength + 2);
        await using var transport = new ReplayElmTransport();
        transport.Expect("2101", IsoTpWireFormat.Encode(payload, 0x7BB, 0).AsElmResponse());
        var diagnostics = new LeafBmsDiagnostics(new ElmSession(new ElmFramer(transport)), EcuContext.NissanLeafBms);
        var result = await diagnostics.QueryGroup01Async();
        await Assert.That(result.Value).IsNull();
        await Assert.That(result.Observation.Quality).IsEqualTo(ObservationQuality.Invalid);
        await Assert.That(result.Observation.ObservedAtUtc).IsNotNull();
    }

    [Test]
    public async Task WrongResponder_CannotSupplyBatteryStatus()
    {
        await using var transport = new ReplayElmTransport();
        transport.Expect("2101", LeafGoldenData.GoldenGroup01Lines.Select(l => "7BC" + l[3..]).ToArray().AsElmResponse());
        var diagnostics = new LeafBmsDiagnostics(new ElmSession(new ElmFramer(transport)), EcuContext.NissanLeafBms);
        var result = await diagnostics.QueryGroup01Async();
        await Assert.That(result.Value).IsNull();
        await Assert.That(result.Observation.Quality).IsEqualTo(ObservationQuality.Invalid);
    }

    [Test]
    public async Task CompleteButUnsupportedTemperatureLayout_IsNotDecodedWithAze0Offsets()
    {
        var payload = IsoTpParser.ParseIsoTpResponse(LeafGoldenData.GoldenGroup04Lines.AsElmResponse()).ToArray();
        Array.Resize(ref payload, 31); // ZE1's 29 data bytes have different offsets.
        await using var transport = new ReplayElmTransport();
        transport.Expect("2104", IsoTpWireFormat.Encode(payload, 0x7BB, 0).AsElmResponse());
        var diagnostics = new LeafBmsDiagnostics(new ElmSession(new ElmFramer(transport)), EcuContext.NissanLeafBms);
        var result = await diagnostics.QueryGroup04Async();
        await Assert.That(result.Value).IsNull();
        await Assert.That(result.Observation.Quality).IsEqualTo(ObservationQuality.Invalid);
    }

    [Test]
    public async Task InvalidCell_RetainsPhysicalIndexAndBalancingAlignmentThroughSnapshot()
    {
        var payload = IsoTpParser.ParseIsoTpResponse(LeafGoldenData.GoldenGroup02Lines.AsElmResponse()).ToArray();
        payload[2 + 7 * 2] = 0xFF;
        payload[3 + 7 * 2] = 0xFF;
        await using var transport = new ReplayElmTransport();
        transport.AutoRespond("2102", IsoTpWireFormat.Encode(payload, 0x7BB, 0).AsElmResponse());
        transport.AutoRespond("2106", LeafGoldenData.GoldenGroup06Lines.AsElmResponse());
        var commands = new LeafAze0CommandSet(new ElmSession(new ElmFramer(transport)));
        commands.TryGet<IBatteryManagementSystem>(out var bms);
        var cells = (await bms.GetCellVoltagesAsync())!;
        await Assert.That(cells.CellCount).IsEqualTo(96);
        await Assert.That(cells.ValidCellCount).IsEqualTo(95);
        await Assert.That(cells.CellVoltagesMv[7]).IsNull();
        await Assert.That(cells.CellVoltagesMv[8]).IsEqualTo((payload[18] << 8) | payload[19]);
        await Assert.That(cells.BalancingCells![7]).IsTrue();
        await Assert.That(cells.MinVoltageMv).IsNull();
        await Assert.That(cells.MaxVoltageMv).IsNull();
        await Assert.That(cells.AvgVoltageMv).IsNull();

        await using var telemetry = new TelemetrySession([new CellVoltagesTelemetryProvider(bms)]);
        var snapshot = await telemetry.GetSnapshotAsync();
        await Assert.That(snapshot.CellVoltagesV!.Count).IsEqualTo(96);
        await Assert.That(snapshot.CellVoltagesV[7]).IsNull();
        await Assert.That(snapshot.CellVoltagesV[8]).IsEqualTo(cells.CellVoltagesMv[8] / 1000m);
        await Assert.That(snapshot.CellVoltageMinV).IsNull();
        await Assert.That(snapshot.CellVoltageMaxV).IsNull();
        await Assert.That(snapshot.CellVoltageAverageV).IsNull();
    }

    [Test]
    public async Task TruncatedCellPayload_IsNotAPartialCellSet()
    {
        await using var transport = new ReplayElmTransport();
        transport.Expect("2102", LeafGoldenData.GoldenGroup02Lines[..^1].AsElmResponse());
        var commands = new LeafAze0CommandSet(new ElmSession(new ElmFramer(transport)));
        commands.TryGet<IBatteryManagementSystem>(out var bms);
        var result = (await bms.GetCellVoltagesAsync())!;
        await Assert.That(result.CellCount).IsEqualTo(0);
        await Assert.That(result.Observation.Quality).IsEqualTo(ObservationQuality.Invalid);
        await Assert.That(result.Observation.Query).IsEqualTo("2102");
        await Assert.That(result.Observation.ObservedAtUtc).IsNotNull();
    }

    [Test]
    public async Task CorruptVin_IsNotRepairedIntoAnIdentity()
    {
        byte[] payload = [0x61, 0x81, .. System.Text.Encoding.ASCII.GetBytes("1N4A"), 0xE3,
            .. System.Text.Encoding.ASCII.GetBytes("Z0CP7HC000001"), 0, 0];
        await using var transport = new ReplayElmTransport();
        transport.Expect("2181", IsoTpWireFormat.Encode(payload, 0x79A, 0).AsElmResponse());
        var commands = new LeafAze0CommandSet(new ElmSession(new ElmFramer(transport)));
        commands.TryGet<IVehicleIdentification>(out var identification);
        await Assert.That(await identification.GetVinAsync()).IsNull();
    }

    [Test]
    public async Task CellResult_DefensivelyCopiesAndRejectsMisalignedFlags()
    {
        int?[] values = [3900, null, 3902];
        bool[] balancing = [false, true, false];
        var result = new CellVoltageData(values, balancing);
        values[1] = 4000;
        balancing[1] = false;
        await Assert.That(result.CellVoltagesMv[1]).IsNull();
        await Assert.That(result.BalancingCells![1]).IsTrue();
        await Assert.That(() => new CellVoltageData(values, [true])).Throws<ArgumentException>();
    }
}
