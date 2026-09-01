using System.Text;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;

namespace ObdInsight.Tests.NissanLeaf;

/// <summary>
///     Roadmap B6: VIN-driven vehicle selection over replay — the session connects, reads
///     the VIN via the profile's own mechanism (Leaf: 2181 on the charger ECU), resolves
///     the AZE0-2 command set with no hardcoded vehicle, and degrades to clear statuses
///     (never a crash) for unreadable VINs, unknown vehicles, and unsupported variants.
/// </summary>
[Timeout(30_000)]
public class VehicleResolverTests
{
    private static readonly IReadOnlyList<IVehicleProfile> Profiles =
        [new ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.NissanLeaf()];

    [Test]
    public async Task Resolve_GoldenVin_BuildsAze0CommandSet(CancellationToken token)
    {
        var transport = new ReplayElmTransport();
        transport.Expect("2181", LeafGoldenData.GoldenVinLines.AsElmResponse());
        var session = new ElmSession(new ElmFramer(transport));

        var result = await VehicleResolver.ResolveAsync(session, Profiles, token);

        await Assert.That(result.Status).IsEqualTo(VehicleDetectionStatus.Detected);
        await Assert.That(result.Vin).IsEqualTo("1N4AZ0CP7HC000001");
        await Assert.That(result.VariantId!.Value.Value).IsEqualTo("AZE0-2-2016-2017");
        await Assert.That(result.Commands).IsTypeOf<LeafAze0CommandSet>();
    }

    [Test]
    public async Task Resolve_UnreadableVin_ReturnsVinUnreadable(CancellationToken token)
    {
        var transport = new ReplayElmTransport();
        // Session retries the invalid response once before the capability gives up.
        transport.Expect("2181", "NO DATA\r\r>");
        transport.Expect("2181", "NO DATA\r\r>");
        var session = new ElmSession(new ElmFramer(transport));

        var result = await VehicleResolver.ResolveAsync(session, Profiles, token);

        await Assert.That(result.Status).IsEqualTo(VehicleDetectionStatus.VinUnreadable);
        await Assert.That(result.Commands).IsNull();
    }

    [Test]
    public async Task Resolve_ForeignVin_ReturnsUnsupportedVehicle(CancellationToken token)
    {
        // A VW WMI — VIN reads fine but no registered profile recognizes it.
        var transport = new ReplayElmTransport();
        transport.Expect("2181", VinResponse("WVWZZZE1ZPP123456"));
        var session = new ElmSession(new ElmFramer(transport));

        var result = await VehicleResolver.ResolveAsync(session, Profiles, token);

        await Assert.That(result.Status).IsEqualTo(VehicleDetectionStatus.UnsupportedVehicle);
        await Assert.That(result.Vin).IsEqualTo("WVWZZZE1ZPP123456");
        await Assert.That(result.Commands).IsNull();
    }

    [Test]
    public async Task Resolve_2013Leaf_ReturnsVariantUnsupported(CancellationToken token)
    {
        // Model year D = 2013 → AZE0-0 (Gen2, conservative pick for the VIN-ambiguous
        // 2013-2014 split) — detected, but no command set exists for it yet.
        var transport = new ReplayElmTransport();
        transport.Expect("2181", VinResponse("1N4AZ0CP7DC300000"));
        var session = new ElmSession(new ElmFramer(transport));

        var result = await VehicleResolver.ResolveAsync(session, Profiles, token);

        await Assert.That(result.Status).IsEqualTo(VehicleDetectionStatus.VariantUnsupported);
        await Assert.That(result.VariantId!.Value.Value).IsEqualTo("AZE0-0-2013-2014");
        await Assert.That(result.Commands).IsNull();
    }

    /// <summary>Builds a charger-ECU ISO-TP Mode 21 PID 81 response for an arbitrary VIN.</summary>
    private static string VinResponse(string vin)
    {
        var payload = new byte[2 + 19];
        payload[0] = 0x61;
        payload[1] = 0x81;
        Encoding.ASCII.GetBytes(vin, payload.AsSpan(2));

        var lines = new List<string>();
        var first = 6;
        lines.Add($"79A10{payload.Length:X2}{Hex(payload.AsSpan(0, first))}");
        var offset = first;
        var seq = 1;
        while (offset < payload.Length)
        {
            var take = Math.Min(7, payload.Length - offset);
            lines.Add($"79A2{seq & 0xF:X1}{Hex(payload.AsSpan(offset, take))}");
            offset += take;
            seq++;
        }

        return string.Join("\r", lines) + "\r\r>";
    }

    private static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("X2"));
        }

        return sb.ToString();
    }
}
