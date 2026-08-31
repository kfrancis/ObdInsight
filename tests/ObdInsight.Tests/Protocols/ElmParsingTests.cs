using CsCheck;
using ObdInsight.Core.Protocols;

namespace OdbTestApp.Tests.Protocols;

/// <summary>
/// Unit and property tests for <see cref="ElmParsing"/>. The Mode 01 tests pin the PID match:
/// the check used to be written with &amp;&amp; instead of ||, which accepted another PID's data
/// and also accepted an unparseable PID field whenever the caller asked for PID 0.
/// </summary>
[Timeout(30_000)]
public class ElmParsingTests
{
    private const int Iterations = 500;

    [Test]
    public async Task TryParseMode01Response_MatchingPid_ReturnsData(CancellationToken _)
    {
        var ok = ElmParsing.TryParseMode01Response("41 0C 1A F8", 0x0C, out var data);

        await Assert.That(ok).IsTrue();
        await Assert.That(data).IsEquivalentTo(new byte[] { 0x1A, 0xF8 });
    }

    [Test]
    public async Task TryParseMode01Response_DifferentPid_IsRejected(CancellationToken _)
    {
        var ok = ElmParsing.TryParseMode01Response("41 0C 1A F8", 0x0D, out var data);

        await Assert.That(ok).IsFalse();
        await Assert.That(data).IsEmpty();
    }

    [Test]
    public async Task TryParseMode01Response_UnparseablePid_IsRejected(CancellationToken _)
    {
        // Asking for PID 0 must not let a non-hex PID field through on a failed parse.
        var ok = ElmParsing.TryParseMode01Response("41 ZZ 1A F8", 0x00, out var data);

        await Assert.That(ok).IsFalse();
        await Assert.That(data).IsEmpty();
    }

    [Test]
    public async Task TryParseMode01Response_NotAMode01Reply_IsRejected(CancellationToken ct)
    {
        await Assert.That(ElmParsing.TryParseMode01Response("7F 01 12", 0x01, out _)).IsFalse();
        await Assert.That(ElmParsing.TryParseMode01Response("41", 0x00, out _)).IsFalse();
        await Assert.That(ElmParsing.TryParseMode01Response("NO DATA", 0x0C, out _)).IsFalse();
    }

    [Test]
    public async Task TryParseMode01Response_SucceedsOnlyForTheRequestedPid(CancellationToken _)
    {
        Check.Sample(
            Gen.Select(Gen.Byte, Gen.Byte, Gen.Byte.Array[0, 6]),
            t =>
            {
                var (responsePid, requestedPid, payload) = t;
                var line = string.Join(
                    " ",
                    new[] { "41", responsePid.ToString("X2") }.Concat(payload.Select(b => b.ToString("X2"))));

                var ok = ElmParsing.TryParseMode01Response(line, requestedPid, out var data);

                return ok == (responsePid == requestedPid)
                       && (!ok || data.SequenceEqual(payload));
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task NormalizeLines_DropsNullsAndEmptyLines(CancellationToken _)
    {
        var lines = ElmParsing.NormalizeLines("7BB\0102B\r\r\n  \r61 01 \n");

        await Assert.That(lines).IsEquivalentTo(new[] { "7BB102B", "61 01" });
    }

    [Test]
    public async Task NormalizeLines_NeverYieldsBlankOrUntrimmedLines(CancellationToken _)
    {
        Check.Sample(
            Gen.Char[' ', 'z'].Array[0, 60].Select(c => new string(c) + "\r\n\0 \t"),
            s => ElmParsing.NormalizeLines(s).All(l => l.Length > 0 && l == l.Trim() && !l.Contains('\0')),
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task LooksLikeAdapterError_ClassifiesAdapterChatter(CancellationToken _)
    {
        await Assert.That(ElmParsing.LooksLikeAdapterError("NO DATA")).IsTrue();
        await Assert.That(ElmParsing.LooksLikeAdapterError("?")).IsTrue();
        await Assert.That(ElmParsing.LooksLikeAdapterError("UNABLE TO CONNECT")).IsTrue();
        await Assert.That(ElmParsing.LooksLikeAdapterError("STOPPED")).IsTrue();
        await Assert.That(ElmParsing.LooksLikeAdapterError("CAN ERROR")).IsTrue();

        // "SEARCHING..." means the adapter is still trying protocols, not that it failed.
        await Assert.That(ElmParsing.LooksLikeAdapterError("SEARCHING...")).IsFalse();
        await Assert.That(ElmParsing.LooksLikeAdapterError("41 0C 1A F8")).IsFalse();
    }
}
