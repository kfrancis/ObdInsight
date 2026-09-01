using ObdInsight.Core.Protocols;

namespace OdbTestApp.Tests.Protocols;

/// <summary>
///     ATMA monitor-mode line parsing: 11-bit/29-bit CAN IDs, spaced ("AT S1") and contiguous
///     ("AT S0") byte formats. Pure parser — no transport/session involved.
/// </summary>
[Timeout(30_000)]
public class RawCanFrameParserTests
{
    [Test]
    public async Task Spaced11Bit_WithData_Parses(CancellationToken _)
    {
        var ok = RawCanFrameParser.TryParse("1DB 10 14 61 01 00 00 00 08", out var frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.CanId).IsEqualTo(0x1DB);
        await Assert.That(frame.Data.ToArray())
            .IsEquivalentTo(new byte[] { 0x10, 0x14, 0x61, 0x01, 0x00, 0x00, 0x00, 0x08 });
    }

    [Test]
    public async Task Spaced11Bit_NoData_Parses(CancellationToken _)
    {
        var ok = RawCanFrameParser.TryParse("7E8", out var frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.CanId).IsEqualTo(0x7E8);
        await Assert.That(frame.Data.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Contiguous11Bit_Parses(CancellationToken _)
    {
        // 3-digit ID + 4 data bytes, no spaces (AT S0 format).
        var ok = RawCanFrameParser.TryParse("7E80341000102", out var frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.CanId).IsEqualTo(0x7E8);
        await Assert.That(frame.Data.ToArray()).IsEquivalentTo(new byte[] { 0x03, 0x41, 0x00, 0x01, 0x02 });
    }

    [Test]
    public async Task Spaced29Bit_Parses(CancellationToken _)
    {
        var ok = RawCanFrameParser.TryParse("18DAF110 02 10 03", out var frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.CanId).IsEqualTo(0x18DAF110);
        await Assert.That(frame.Data.ToArray()).IsEquivalentTo(new byte[] { 0x02, 0x10, 0x03 });
    }

    [Test]
    public async Task Contiguous29Bit_Parses(CancellationToken _)
    {
        // 8-digit ID + 3 data bytes, no spaces => total length 14 (even).
        var ok = RawCanFrameParser.TryParse("18DAF110021003", out var frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.CanId).IsEqualTo(0x18DAF110);
        await Assert.That(frame.Data.ToArray()).IsEquivalentTo(new byte[] { 0x02, 0x10, 0x03 });
    }

    [Test]
    public async Task Contiguous29Bit_NoData_Parses(CancellationToken _)
    {
        // 8-digit ID only, no data => total length 8 (even).
        var ok = RawCanFrameParser.TryParse("18DAF110", out var frame);

        await Assert.That(ok).IsTrue();
        await Assert.That(frame.CanId).IsEqualTo(0x18DAF110);
        await Assert.That(frame.Data.Length).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidLines_FailToParse(CancellationToken token)
    {
        string[] invalidLines =
        [
            "",
            "   ",
            "ZZZ 01 02", // non-hex ID
            "1DB 01 ZZ", // non-hex data byte
            "1DB 01 02 03 04 05 06 07 08 09", // 9 data bytes - exceeds max
            "12 01 02", // wrong ID width (not 3 or 8)
            "1DB0" // contiguous, even length but too short for an 8-digit (29-bit) ID
        ];

        foreach (var line in invalidLines)
        {
            var ok = RawCanFrameParser.TryParse(line, out _);
            await Assert.That(ok).IsFalse();
        }
    }

    [Test]
    public async Task OutOfRange11BitId_FailsToParse(CancellationToken token)
    {
        // 0x800 exceeds the 11-bit range (0x000-0x7FF) despite being a valid 3-digit hex token.
        var ok = RawCanFrameParser.TryParse("800 01 02", out _);

        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task RawCanFrame_CanIdHex_FormatsWidthByMagnitude(CancellationToken _)
    {
        RawCanFrameParser.TryParse("1DB", out var narrow);
        RawCanFrameParser.TryParse("18DAF110", out var wide);

        await Assert.That(narrow.CanIdHex).IsEqualTo("1DB");
        await Assert.That(wide.CanIdHex).IsEqualTo("18DAF110");
    }
}
