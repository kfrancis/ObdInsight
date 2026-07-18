using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Tests.Base;

namespace OdbTestApp.Tests.Elm327;

/// <summary>
/// Deterministic ElmSession tests running against <see cref="ReplayElmTransport"/> —
/// the production session/framer stack with no BLE hardware.
/// </summary>
[Timeout(30_000)]
public class ElmSessionReplayTests
{
    private static (ReplayElmTransport Transport, ElmSession Session) CreateSession()
    {
        var transport = new ReplayElmTransport();
        var session = new ElmSession(new ElmFramer(transport));
        return (transport, session);
    }

    [Test]
    public async Task InitializeAndLock_ThenQuery_SucceedsOverReplay(CancellationToken token)
    {
        var (transport, session) = CreateSession();

        // Protocol probes: wakeup broadcast, lock probe, lock verification.
        transport.Expect("0100", "41 00 BE 3E B8 11\r\r>");
        transport.Expect("0100", "41 00 BE 3E B8 11\r\r>");
        transport.Expect("0100", "41 00 BE 3E B8 11\r\r>");
        // The actual query under test (Mode 01 PID 0C, engine RPM).
        transport.Expect("010C", "41 0C 1A F8\r\r>");

        await session.InitializeAndLockAsync(token);
        var lines = await session.QueryAsync("010C", token);

        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo("41 0C 1A F8");

        // Init sequence sanity: hard reset first, protocol 6 attempted, all probes consumed.
        var sent = transport.SentCommands;
        await Assert.That(sent[0]).IsEqualTo("AT Z");
        await Assert.That(sent).Contains("AT SP 6");
        await Assert.That(sent.Count(c => c == "0100")).IsEqualTo(3);
    }

    [Test]
    public async Task Query_InvalidResponse_RetriesOnceAndSucceeds(CancellationToken token)
    {
        var (transport, session) = CreateSession();

        transport.Expect("0100", "41 00 BE 3E B8 11\r\r>");
        transport.Expect("0100", "41 00 BE 3E B8 11\r\r>");
        transport.Expect("0100", "41 00 BE 3E B8 11\r\r>");
        // First attempt fails (adapter error), retry succeeds.
        transport.Expect("010D", "NO DATA\r\r>");
        transport.Expect("010D", "41 0D 3C\r\r>");

        await session.InitializeAndLockAsync(token);
        var lines = await session.QueryAsync("010D", token);

        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).IsEqualTo("41 0D 3C");
        await Assert.That(transport.SentCommands.Count(c => c == "010D")).IsEqualTo(2);
    }

    [Test]
    public async Task MonitoringMode_StreamsFrames_AndExitsCleanly(CancellationToken token)
    {
        var (transport, session) = CreateSession();

        // "ATMA" must stay silent (monitoring streams, no OK/prompt) — script overrides
        // the lenient auto-OK for exactly this command.
        transport.Expect("ATMA", "");

        await session.EnterMonitoringModeAsync(EcuContext.NissanLeafHvbatMonitor, token);
        await Assert.That(session.CurrentMode).IsEqualTo(EcuCommunicationMode.PassiveMonitoring);

        // Both frames in ONE chunk — a bursty BLE notification. The framer must yield both;
        // it previously discarded everything after the first delimiter in a read chunk.
        transport.EnqueueIncoming("1DB 10 14 61 01 00 00 00 5C\r1DA 05 C0 24 00 4E 20 00 07\r");

        var frames = new List<RawCanFrame>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        await foreach (var frame in session.MonitorFramesAsync(cts.Token))
        {
            frames.Add(frame);
            if (frames.Count == 2) cts.Cancel();
        }

        await Assert.That(frames.Count).IsEqualTo(2);
        await Assert.That(frames[0].CanId).IsEqualTo(0x1DB);
        await Assert.That(frames[0].Data.Length).IsEqualTo(8);
        await Assert.That(frames[1].CanId).IsEqualTo(0x1DA);

        await session.ExitMonitoringModeAsync(CancellationToken.None);
        await Assert.That(session.CurrentMode).IsEqualTo(EcuCommunicationMode.RequestResponse);
    }
}
