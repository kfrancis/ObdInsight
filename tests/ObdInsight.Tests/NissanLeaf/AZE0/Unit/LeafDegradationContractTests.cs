using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
///     Roadmap B7 — the unified degradation contract: data absence yields null / all-null
///     results from every capability, never an exception. Each scenario scripts the
///     adapter answering "NO DATA" twice (the session retries an invalid response once
///     before surfacing an IOException — which capabilities must absorb).
/// </summary>
[Timeout(30_000)]
public class LeafDegradationContractTests
{
    [Test]
    public async Task BmsStatus_DataAbsent_ReturnsAllNullStatus_NoThrow(CancellationToken token)
    {
        var (transport, commands) = Setup();
        transport.Expect("2101", "NO DATA\r\r>");
        transport.Expect("2101", "NO DATA\r\r>");

        commands.TryGet<IBatteryManagementSystem>(out var bms);
        var status = await bms.GetStatusAsync(token);

        await Assert.That(status).IsNotNull();
        await Assert.That(status.SocPercent).IsNull();
        await Assert.That(status.VoltageVolts).IsNull();
        await Assert.That(status.CurrentAmps).IsNull();
        await Assert.That(status.HealthPercent).IsNull();
        await Assert.That(status.TemperatureC).IsNull();
    }

    [Test]
    public async Task CellVoltages_DataAbsent_ReturnsNull_NoThrow(CancellationToken token)
    {
        var (transport, commands) = Setup();
        transport.Expect("2102", "NO DATA\r\r>");
        transport.Expect("2102", "NO DATA\r\r>");

        commands.TryGet<IBatteryManagementSystem>(out var bms);
        var cells = await bms.GetCellVoltagesAsync(token);

        await Assert.That(cells).IsNull();
    }

    [Test]
    public async Task Vin_DataAbsent_ReturnsNull_NoThrow(CancellationToken token)
    {
        var (transport, commands) = Setup();
        transport.Expect("2181", "NO DATA\r\r>");
        transport.Expect("2181", "NO DATA\r\r>");

        commands.TryGet<IVehicleIdentification>(out var ident);
        var vin = await ident.GetVinAsync(token);

        await Assert.That(vin).IsNull();
    }

    [Test]
    public async Task BmsStatus_Cancellation_StillPropagatesAsOce(CancellationToken token)
    {
        var (_, commands) = Setup();
        commands.TryGet<IBatteryManagementSystem>(out var bms);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.That(async () => await bms.GetStatusAsync(cts.Token))
            .Throws<OperationCanceledException>();
    }

    private static (ReplayElmTransport transport, LeafAze0CommandSet commands) Setup()
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        return (transport, new LeafAze0CommandSet(session));
    }
}
