using CsCheck;
using ObdInsight.Core.Protocols;

namespace OdbTestApp.Tests.Protocols;

/// <summary>
///     Property-based tests (CsCheck) for <see cref="RawCanFrameParser" />: every frame an ELM327 can
///     emit in monitor mode must parse back to the ID and bytes it was rendered from, in all four
///     layouts (11-/29-bit CAN ID x spaced "AT S1" / contiguous "AT S0").
///     The contiguous path carries the risk: it infers ID width from hex-digit parity alone
///     (3 + 2N is odd, 8 + 2N is even), so a wrong inference silently shifts every data byte.
///     On failure CsCheck prints the shrunk counterexample plus a seed to replay it.
/// </summary>
[Timeout(120_000)]
public class RawCanFrameParserPropertyTests
{
    private const int Iterations = 500;

    /// <summary>11-bit identifiers, which the adapter renders as 3 hex digits.</summary>
    private static readonly Gen<int> Id11BitGen = Gen.Int[0, 0x7FF];

    /// <summary>29-bit identifiers, rendered as 8 hex digits.</summary>
    private static readonly Gen<int> Id29BitGen = Gen.Int[0, 0x1FFFFFFF];

    /// <summary>A CAN frame carries 0-8 data bytes.</summary>
    private static readonly Gen<byte[]> DataGen = Gen.Byte.Array[0, 8];

    [Test]
    public async Task Spaced11Bit_RoundTrips(CancellationToken _)
    {
        Id11BitGen.Select(DataGen).Sample(t => RoundTrips(t.Item1, t.Item2, "X3", true), iter: Iterations);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Contiguous11Bit_RoundTrips(CancellationToken _)
    {
        Id11BitGen.Select(DataGen).Sample(t => RoundTrips(t.Item1, t.Item2, "X3", false), iter: Iterations);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Spaced29Bit_RoundTrips(CancellationToken _)
    {
        Id29BitGen.Select(DataGen).Sample(t => RoundTrips(t.Item1, t.Item2, "X8", true), iter: Iterations);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Contiguous29Bit_RoundTrips(CancellationToken _)
    {
        Id29BitGen.Select(DataGen).Sample(t => RoundTrips(t.Item1, t.Item2, "X8", false), iter: Iterations);
        await Task.CompletedTask;
    }

    [Test]
    public async Task SurroundingWhitespace_DoesNotChangeTheResult(CancellationToken _)
    {
        // Monitor-mode lines arrive with stray CR/LF and padding; the parser trims.
        Id11BitGen.Select(DataGen, Gen.Const("  "), Gen.Const(" \t")).Sample(t =>
            {
                var line = Render(t.Item1, t.Item2, "X3", true);
                var bare = RawCanFrameParser.TryParse(line, out var a);
                var padded = RawCanFrameParser.TryParse(t.Item3 + line + t.Item4, out var b);
                return bare == padded && a.CanId == b.CanId && a.Data.Span.SequenceEqual(b.Data.Span);
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ArbitraryInput_NeverThrows(CancellationToken ct)
    {
        // Monitor mode also streams adapter status text ("BUFFER FULL", "CAN ERROR") and partial
        // lines. Parsing must reject them, not throw.
        Gen.Char[' ', 'z'].Array[0, 40].Select(c => new string(c)).Sample(s =>
            {
                RawCanFrameParser.TryParse(s, out var ignored);
                _ = ignored;
                return true;
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    private static bool RoundTrips(int canId, byte[] data, string idFormat, bool spaced)
    {
        if (!RawCanFrameParser.TryParse(Render(canId, data, idFormat, spaced), out var frame))
        {
            return false;
        }

        return frame.CanId == canId && frame.Data.Span.SequenceEqual(data);
    }

    /// <summary>Renders a frame the way an ELM327 does with spaces on ("AT S1") or off ("AT S0").</summary>
    private static string Render(int canId, byte[] data, string idFormat, bool spaced)
    {
        var hex = data.Select(b => b.ToString("X2"));
        return spaced
            ? string.Join(" ", new[] { canId.ToString(idFormat) }.Concat(hex))
            : canId.ToString(idFormat) + string.Concat(hex);
    }
}
