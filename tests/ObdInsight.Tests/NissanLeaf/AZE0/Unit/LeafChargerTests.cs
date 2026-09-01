using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
///     Unit tests for Nissan Leaf VIN parsing using golden sample data.
///     Exercises the PRODUCTION path — LeafAze0CommandSet → LeafAze0VehicleIdentification
///     (Mode 21 PID 81 to the IDENT/Charger ECU) — over a replay transport. No BLE required.
/// </summary>
[Timeout(30_000)]
public class LeafChargerVinParsingTests
{
    private static (ReplayElmTransport Transport, IVehicleIdentification Ident) CreateIdent()
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var commands = new LeafAze0CommandSet(session);
        commands.TryGet<IVehicleIdentification>(out var ident);
        return (transport, ident);
    }

    [Test]
    public async Task GetVin_ExtractsCorrectVin(CancellationToken token)
    {
        var (transport, ident) = CreateIdent();
        transport.Expect("2181", LeafGoldenData.GoldenVinLines.AsElmResponse());

        var vin = await ident.GetVinAsync(token);

        await Assert.That(vin).IsEqualTo("1N4AZ0CP7HC000001");
    }

    [Test]
    public async Task GetVin_QueriesIdentEcu(CancellationToken token)
    {
        var (transport, ident) = CreateIdent();
        transport.Expect("2181", LeafGoldenData.GoldenVinLines.AsElmResponse());

        await ident.GetVinAsync(token);

        // IDENT (Charger) ECU: TX 797, RX filter 79A.
        var sent = transport.SentCommands;
        await Assert.That(sent).Contains("AT SH 797");
        await Assert.That(sent).Contains("AT CRA 79A");
        await Assert.That(sent).Contains("2181");
    }

    [Test]
    public async Task GetVin_InvalidHeader_ReturnsNull(CancellationToken token)
    {
        var (transport, ident) = CreateIdent();
        // Negative response (7F 21 31) instead of 61 81 — parseable frame, wrong header.
        transport.Expect("2181", "79A037F2131\r\r>");

        var vin = await ident.GetVinAsync(token);

        await Assert.That(vin).IsNull();
    }

    [Test]
    public async Task GetVin_TruncatedVin_ReturnsNull(CancellationToken token)
    {
        var (transport, ident) = CreateIdent();
        // Valid 61 81 header but only 4 VIN characters — fewer than the required 17.
        transport.Expect("2181", "79A0661813134353600\r\r>");

        var vin = await ident.GetVinAsync(token);

        await Assert.That(vin).IsNull();
    }

    [Test]
    public async Task GetVin_AdapterError_ReturnsNullAfterRetry(CancellationToken token)
    {
        var (transport, ident) = CreateIdent();
        // Both the query and its automatic retry return an adapter error. Under the
        // B7 degradation contract the capability absorbs the session's IOException
        // and reports data absence as null.
        transport.Expect("2181", "NO DATA\r\r>");
        transport.Expect("2181", "NO DATA\r\r>");

        var vin = await ident.GetVinAsync(token);

        await Assert.That(vin).IsNull();
    }
}
