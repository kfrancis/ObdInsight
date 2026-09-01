using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Simulation;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Unit;

/// <summary>
///     Roadmap B5: OBD-II Mode 03/07 DTC reading through the production path —
///     LeafAze0CommandSet → ObdDtcReader over replay. Multi-ECU responses, multi-frame
///     ISO-TP, padding, and graceful degradation on NO DATA.
/// </summary>
[Timeout(30_000)]
public class LeafDtcTests
{
    [Test]
    public async Task GetDtcs_DecodesMultiEcuStoredCodes(CancellationToken token)
    {
        var (transport, dtc) = Setup();
        // ECU 7E8: two codes (P0143, P0196). ECU 7EB: one code (P0A80) + zero padding.
        // ECU 7EC: a U-class code (U0155) — exercises the letter map.
        transport.Expect("03", Lines(
            "7E8 06 43 02 01 43 01 96",
            "7EB 06 43 01 0A 80 00 00",
            "7EC 04 43 01 C1 55"));
        transport.Expect("07", Lines("7E8 02 47 00"));

        var result = await dtc.GetDtcsAsync(token);

        await Assert.That(result.StoredCodes).IsEquivalentTo(["P0143", "P0196", "P0A80", "U0155"]);
        await Assert.That(result.PendingCodes).IsEmpty();
    }

    [Test]
    public async Task GetDtcs_ReassemblesMultiFrameResponse(CancellationToken token)
    {
        var (transport, dtc) = Setup();
        // One ECU, three stored codes → ISO-TP FF + CF: payload 43 03 0143 0196 0A80 (8 bytes).
        transport.Expect("03", Lines(
            "7E8 10 08 43 03 01 43 01",
            "7E8 21 96 0A 80 00 00 00"));
        transport.Expect("07", Lines("7E8 02 47 00"));

        var result = await dtc.GetDtcsAsync(token);

        await Assert.That(result.StoredCodes).IsEquivalentTo(["P0143", "P0196", "P0A80"]);
    }

    [Test]
    public async Task GetDtcs_PendingCodesDecodeIndependently(CancellationToken token)
    {
        var (transport, dtc) = Setup();
        transport.Expect("03", Lines("7E8 02 43 00"));
        transport.Expect("07", Lines("7E8 04 47 01 40 35")); // 0x40 high bits 01 = C class

        var result = await dtc.GetDtcsAsync(token);

        await Assert.That(result.StoredCodes).IsEmpty();
        await Assert.That(result.PendingCodes).IsEquivalentTo(["C0035"]);
    }

    [Test]
    public async Task GetDtcs_NoData_YieldsEmptyLists_NoThrow(CancellationToken token)
    {
        var (transport, dtc) = Setup();
        // The session retries an invalid response once before giving up — script both.
        transport.Expect("03", "NO DATA\r\r>");
        transport.Expect("03", "NO DATA\r\r>");
        transport.Expect("07", "NO DATA\r\r>");
        transport.Expect("07", "NO DATA\r\r>");

        var result = await dtc.GetDtcsAsync(token);

        await Assert.That(result.StoredCodes).IsEmpty();
        await Assert.That(result.PendingCodes).IsEmpty();
    }

    [Test]
    public async Task GetDtcs_SendsFunctionalRequests(CancellationToken token)
    {
        var (transport, dtc) = Setup();
        transport.Expect("03", Lines("7E8 02 43 00"));
        transport.Expect("07", Lines("7E8 02 47 00"));

        await dtc.GetDtcsAsync(token);

        var sent = transport.SentCommands;
        await Assert.That(sent).Contains("AT SH 7DF");
        await Assert.That(sent).Contains("AT CRA 7EX");
        await Assert.That(sent).Contains("03");
        await Assert.That(sent).Contains("07");
    }

    private static (ReplayElmTransport transport, IDiagnosticTroubleCodes dtc) Setup()
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        var commands = new LeafAze0CommandSet(session);
        commands.TryGet<IDiagnosticTroubleCodes>(out var dtc);
        return (transport, dtc);
    }

    private static string Lines(params string[] lines) => string.Join("\r", lines) + "\r\r>";
}
