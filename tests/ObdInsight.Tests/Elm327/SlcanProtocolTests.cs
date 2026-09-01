using ObdInsight.Core.Communication.Slcan;

namespace OdbTestApp.Tests.Elm327;

/// <summary>
/// Tests for the SLCAN line protocol used by CANable-class USB-CAN adapters.
///
/// Written before the hardware arrived: the protocol layer is pure text, so it can be pinned
/// against the specification now and the only thing left to discover on the device is the serial
/// plumbing.
/// </summary>
[Timeout(30_000)]
public class SlcanProtocolTests
{
    [Test]
    public async Task Parses_StandardClassicFrame(CancellationToken token)
    {
        var ok = SlcanProtocol.TryParseFrame("t1DB80011223344556677", out var frame, out var isFd);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.CanId).IsEqualTo(0x1DB);
        await Assert.That(frame.Data.Length).IsEqualTo(8);
        await Assert.That(frame.Data.ToArray()).IsEquivalentTo(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 });
        await Assert.That(isFd).IsFalse();
    }

    /// <summary>Short frames are ordinary on this vehicle - 0x300 is a single byte.</summary>
    [Test]
    public async Task Parses_ShortFrame(CancellationToken token)
    {
        var ok = SlcanProtocol.TryParseFrame("t300100", out var frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.CanId).IsEqualTo(0x300);
        await Assert.That(frame.Data.Length).IsEqualTo(1);
        await Assert.That(frame.Data.Span[0]).IsEqualTo((byte)0x00);
    }

    [Test]
    public async Task Parses_ExtendedFrame(CancellationToken token)
    {
        var ok = SlcanProtocol.TryParseFrame("T18DAF110300112233", out var frame, out _);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.CanId).IsEqualTo(0x18DAF110);
        await Assert.That(frame.Data.Length).IsEqualTo(3);
    }

    /// <summary>
    /// The DLC nibble is a code, not a length. Code 15 means 64 bytes; reading it literally would
    /// silently truncate an FD frame to 15 bytes.
    /// </summary>
    [Test]
    [Arguments(0, 0)]
    [Arguments(8, 8)]
    [Arguments(9, 12)]
    [Arguments(10, 16)]
    [Arguments(11, 20)]
    [Arguments(12, 24)]
    [Arguments(13, 32)]
    [Arguments(14, 48)]
    [Arguments(15, 64)]
    public async Task DlcCode_MapsToCanFdLength(int dlc, int expected, CancellationToken token)
    {
        await Assert.That(SlcanProtocol.DlcToLength(dlc)).IsEqualTo(expected);
    }

    [Test]
    public async Task Parses_CanFdFrame_WithLengthCode(CancellationToken token)
    {
        // 'd' = standard-id CAN FD; DLC code 9 => 12 data bytes.
        var payload = string.Concat(Enumerable.Range(0, 12).Select(i => i.ToString("X2")));
        var ok = SlcanProtocol.TryParseFrame($"d1DB9{payload}", out var frame, out var isFd);

        await Assert.That(ok).IsTrue();
        await Assert.That(isFd).IsTrue();
        await Assert.That(frame.Data.Length).IsEqualTo(12);
    }

    /// <summary>
    /// Adapter chatter must be skipped, not thrown on: a capture loop that dies on a version
    /// banner or a bell is useless.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments("\r")]
    [Arguments("V1013")]        // version banner
    [Arguments("z")]            // transmit ack
    [Arguments("Z")]
    [Arguments("\a")]           // BEL - error response
    [Arguments("garbage")]
    [Arguments("t1D")]          // truncated mid-id
    [Arguments("t1DB8")]        // header only, no payload
    [Arguments("t1DB800112233")] // DLC says 8, only 5 bytes present
    public async Task Ignores_NonFrameLines(string line, CancellationToken token)
    {
        var ok = SlcanProtocol.TryParseFrame(line, out _, out _);

        await Assert.That(ok).IsFalse();
    }

    /// <summary>Surrounding whitespace and CR/LF must not defeat parsing.</summary>
    [Test]
    [Arguments("t300100\r")]
    [Arguments("  t300100  ")]
    [Arguments("t300100\r\n")]
    public async Task Tolerates_LineEndingsAndWhitespace(string line, CancellationToken token)
    {
        await Assert.That(SlcanProtocol.TryParseFrame(line, out _, out _)).IsTrue();
    }

    /// <summary>
    /// Listen-only is a distinct open command, which is the whole safety argument for preferring
    /// this adapter on a powertrain bus over an ELM327's version-dependent AT CSM.
    /// </summary>
    // Note: the listen-only vs normal open distinction is not asserted here - both are compile-time
    // constants, so any such test folds away and the analyser rightly flags it. The contract lives
    // in the XML docs on SlcanProtocol.OpenListenOnly, and is enforced where it matters: at the
    // call site that must choose L over O before touching a powertrain bus.
}
