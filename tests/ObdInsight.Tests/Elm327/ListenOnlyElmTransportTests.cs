using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Simulation;

namespace OdbTestApp.Tests.Elm327;

/// <summary>
/// Tests for <see cref="ListenOnlyElmTransport"/>, the guard that keeps request frames off a
/// powertrain bus.
///
/// This is safety logic, not convenience logic: on a Leaf EV-CAN pair the bus carries motor
/// torque demand (0x1D4) and main-relay control (0x1DB), and the normal ELM327 bring-up path
/// transmits <c>0100</c> requests and runs <c>AT SP 0</c> auto-detect. The guard has to fail
/// loudly rather than drop a write silently, so a caller that believes it configured the adapter
/// and did not cannot proceed unaware.
/// </summary>
[Timeout(30_000)]
public class ListenOnlyElmTransportTests
{
    private static (ReplayElmTransport Inner, ListenOnlyElmTransport Guarded) Create()
    {
        var inner = new ReplayElmTransport();
        return (inner, new ListenOnlyElmTransport(inner));
    }

    private static ValueTask WriteAsync(ListenOnlyElmTransport transport, string text, CancellationToken ct) =>
        transport.WriteAsync(System.Text.Encoding.ASCII.GetBytes(text), ct);

    [Test]
    [Arguments("ATZ\r")]
    [Arguments("ATE0\r")]
    [Arguments("ATL0\r")]
    [Arguments("ATS0\r")]
    [Arguments("ATH1\r")]
    [Arguments("ATCAF0\r")]
    [Arguments("ATSP6\r")]
    [Arguments("ATCSM1\r")]
    [Arguments("ATCRA\r")]
    [Arguments("AT MA\r")]
    public async Task Allows_ListenOnlyBringUpCommands(string command, CancellationToken token)
    {
        var (_, guarded) = Create();

        await WriteAsync(guarded, command, token);

        await Assert.That(guarded.BlockedAttempts).IsEmpty();
    }

    [Test]
    [Arguments("0100\r")]          // OBD-II mode 01 request - a transmitted CAN frame
    [Arguments("2101\r")]          // Nissan UDS group read
    [Arguments("ATSP0\r")]         // protocol auto-detect, which probes the bus
    [Arguments("AT SP 0\r")]       // same, spaced spelling
    [Arguments("ATSH 79B\r")]      // setting a tx header implies intent to transmit
    [Arguments("ATFCSH 79B\r")]    // flow-control header
    [Arguments("ATCRA 7BB\r")]     // CRA *with* an argument is a filter set, not the reset
    public async Task Blocks_AnythingThatCouldTransmit(string command, CancellationToken token)
    {
        var (_, guarded) = Create();

        await Assert.That(async () => await WriteAsync(guarded, command, token))
            .Throws<InvalidOperationException>();

        await Assert.That(guarded.BlockedAttempts).Count().IsEqualTo(1);
    }

    /// <summary>
    /// Any character terminates <c>AT MA</c>, so a bare CR is how monitoring is stopped. It must
    /// pass, or the capture command cannot exit monitoring mode.
    /// </summary>
    [Test]
    public async Task Allows_BareCarriageReturn_ToStopMonitoring(CancellationToken token)
    {
        var (_, guarded) = Create();

        await WriteAsync(guarded, "\r", token);

        await Assert.That(guarded.BlockedAttempts).IsEmpty();
    }

    /// <summary>
    /// Whitelisting compares normalized text, so spacing and casing must not open a hole - or
    /// close one. "at cra" is the same command as "ATCRA".
    /// </summary>
    [Test]
    [Arguments("at cra\r")]
    [Arguments("At Cra\r")]
    [Arguments("  ATCRA  \r")]
    public async Task Normalizes_CaseAndWhitespace(string command, CancellationToken token)
    {
        var (_, guarded) = Create();

        await WriteAsync(guarded, command, token);

        await Assert.That(guarded.BlockedAttempts).IsEmpty();
    }

    /// <summary>
    /// A blocked write must not reach the wire at all. Verified against the replay transport,
    /// which records what it was actually asked to send.
    /// </summary>
    [Test]
    public async Task BlockedWrite_NeverReachesTheInnerTransport(CancellationToken token)
    {
        var (inner, guarded) = Create();

        await Assert.That(async () => await WriteAsync(guarded, "0100\r", token))
            .Throws<InvalidOperationException>();

        // The replay transport records everything it was actually asked to send.
        await Assert.That(inner.SentCommands).IsEmpty();
    }

    /// <summary>
    /// Ownership of the wrapped transport stays with the caller. Disposing the decorator must
    /// not tear down a connection the session still believes it holds.
    /// </summary>
    [Test]
    public async Task Dispose_DoesNotDisposeInnerTransport(CancellationToken token)
    {
        var (inner, guarded) = Create();
        await inner.OpenAsync(token);

        await guarded.DisposeAsync();

        await Assert.That(inner.IsOpen).IsTrue();
    }
}
