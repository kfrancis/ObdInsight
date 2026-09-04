using ObdInsight.Core.Communication.Slcan;

namespace ObdInsight.Tests.Elm327;

/// <summary>
///     Tests for the SLCAN line protocol used by CANable-class USB-CAN adapters.
///     Written before the hardware arrived: the protocol layer is pure text, so it can be pinned
///     against the specification now and the only thing left to discover on the device is the serial
///     plumbing.
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
        await Assert.That(frame.Data.ToArray())
            .IsEquivalentTo(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77 });
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
    ///     The DLC nibble is a code, not a length. Code 15 means 64 bytes; reading it literally would
    ///     silently truncate an FD frame to 15 bytes.
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
    ///     Adapter chatter must be skipped, not thrown on: a capture loop that dies on a version
    ///     banner or a bell is useless.
    /// </summary>
    [Test]
    [Arguments("")]
    [Arguments("\r")]
    [Arguments("V1013")] // version banner
    [Arguments("z")] // transmit ack
    [Arguments("Z")]
    [Arguments("\a")] // BEL - error response
    [Arguments("garbage")]
    [Arguments("t1D")] // truncated mid-id
    [Arguments("t1DB8")] // header only, no payload
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
    ///     CAN FD with bit-rate switch uses its own prefix letters. A CANable 2.0 emits these for
    ///     every BRS frame on a real FD bus; not parsing them would count the whole bus as chatter.
    /// </summary>
    [Test]
    [Arguments("b1DB80102030405060708", 0x1DB, 8)]
    [Arguments("B18DAF11080102030405060708", 0x18DAF110, 8)]
    public async Task Parses_CanFdBrsFrames(string line, int expectedId, int expectedLength, CancellationToken token)
    {
        var ok = SlcanProtocol.TryParseFrame(line, out var frame, out var isFd);

        await Assert.That(ok).IsTrue();
        await Assert.That(isFd).IsTrue();
        await Assert.That(frame.CanId).IsEqualTo(expectedId);
        await Assert.That(frame.Data.Length).IsEqualTo(expectedLength);
    }

    /// <summary>Remote frames carry no data and are not surfaced as data frames.</summary>
    [Test]
    [Arguments("r1DB8")]
    [Arguments("R18DAF1108")]
    public async Task Ignores_RemoteFrames(string line, CancellationToken token)
    {
        await Assert.That(SlcanProtocol.TryParseFrame(line, out _, out _)).IsFalse();
    }

    /// <summary>
    ///     Dialect detection from the <c>V</c> banner. The CANable string is the one captured
    ///     from the bench device (2026-09-03); the others follow the respective firmware sources.
    /// </summary>
    [Test]
    [Arguments("16e7497-dirty github.com/normaldotcom/canable2.git", SlcanDialect.Canable)]
    [Arguments("b158aa4 github.com/normaldotcom/cantact-fw.git", SlcanDialect.Canable)]
    [Arguments("V1013", SlcanDialect.Lawicel)]
    [Arguments("V1013\r", SlcanDialect.Lawicel)]
    // Captured from the bench device after flashing ElmüSoft slcan 2.5 (2026-09-03).
    [Arguments("+Board: Multiboard\tMCU: STM32G431\tDevID: 1128\tFirmware: 2492419\tSlcan: 105\tClock: 160\tChannels: 1\tQuartz: No\tLimits: 512,256,128,128,32,32,16,16\tHAL: 1.2.5\tSerial: 209F336F4E4D5018", SlcanDialect.ElmueSoft)]
    [Arguments("Board=CANable 2.0\tMCU=STM32G431\tFirmware=250914\tSlcan=101", SlcanDialect.ElmueSoft)]
    [Arguments("", SlcanDialect.Unknown)]
    [Arguments("\r", SlcanDialect.Unknown)]
    [Arguments("\a", SlcanDialect.Unknown)]
    [Arguments("garbage", SlcanDialect.Unknown)]
    public async Task DetectDialect_ClassifiesKnownBanners(string banner, SlcanDialect expected, CancellationToken token)
    {
        await Assert.That(SlcanProtocol.DetectDialect(banner)).IsEqualTo(expected);
    }

    /// <summary>
    ///     The listen-only difference between firmwares, pinned: Lawicel takes <c>L</c>; CANable
    ///     (and its ElmüSoft successor) take <c>M1</c> then <c>O</c> and ignore <c>L</c>. An unknown
    ///     device gets <c>L</c> because that is the request that cannot open normal mode by mistake.
    /// </summary>
    [Test]
    [Arguments(SlcanDialect.Lawicel, true, "L")]
    [Arguments(SlcanDialect.Lawicel, false, "O")]
    [Arguments(SlcanDialect.Unknown, true, "L")]
    [Arguments(SlcanDialect.Unknown, false, "O")]
    [Arguments(SlcanDialect.Canable, true, "M1,O")]
    [Arguments(SlcanDialect.Canable, false, "M0,O")]
    [Arguments(SlcanDialect.ElmueSoft, true, "M1,O")]
    [Arguments(SlcanDialect.ElmueSoft, false, "M0,O")]
    public async Task OpenCommands_MatchTheDialect(SlcanDialect dialect, bool listenOnly, string expected, CancellationToken token)
    {
        var commands = SlcanProtocol.OpenCommands(dialect, listenOnly).Select(c => c.TrimEnd('\r'));

        await Assert.That(string.Join(",", commands)).IsEqualTo(expected);
    }

    /// <summary>
    ///     Only the <c>S</c> codes that mean the same rate on every firmware are offered.
    ///     <c>S7</c> is 800 kbit/s on Lawicel and 750 kbit/s on CANable, so it is refused.
    /// </summary>
    [Test]
    [Arguments(500, "S6")]
    [Arguments(250, "S5")]
    [Arguments(125, "S4")]
    [Arguments(1000, "S8")]
    [Arguments(10, "S0")]
    public async Task BitrateCommand_MapsCommonRates(int kbps, string expected, CancellationToken token)
    {
        await Assert.That(SlcanProtocol.BitrateCommand(kbps).TrimEnd('\r')).IsEqualTo(expected);
    }

    [Test]
    [Arguments(750)]
    [Arguments(800)]
    [Arguments(0)]
    [Arguments(83)]
    public async Task BitrateCommand_RefusesAmbiguousOrUnknownRates(int kbps, CancellationToken token)
    {
        await Assert.That(() => SlcanProtocol.BitrateCommand(kbps)).Throws<ArgumentOutOfRangeException>();
    }
}
