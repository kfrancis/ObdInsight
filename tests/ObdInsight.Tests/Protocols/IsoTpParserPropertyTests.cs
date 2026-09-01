using CsCheck;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Tests.Protocols;

/// <summary>
///     Property-based tests (CsCheck) for <see cref="IsoTpParser" />: any payload encoded to the
///     ELM327 wire format must come back byte-identical, whatever the line layout.
///     The parser splits run-together frames by scanning for a CAN-ID-shaped prefix (0x700–0x7FF)
///     followed by a frame-type nibble — a heuristic that payload data can imitate. Example tests
///     use real captures, which by luck contain no such sequence; generated payloads do.
///     On failure CsCheck prints the shrunk counterexample plus a seed to replay it.
/// </summary>
[Timeout(120_000)]
public class IsoTpParserPropertyTests
{
    private const int Iterations = 500;

    /// <summary>ISO-TP responder IDs the parser accepts (Leaf BMS answers on 7BB).</summary>
    private static readonly Gen<int> CanIdGen = Gen.Int[0x700, 0x7FF];

    /// <summary>Padding adapters use for the unused tail of the last frame.</summary>
    private static readonly Gen<byte> PaddingGen = Gen.OneOfConst<byte>(0x00, 0xAA, 0xFF);

    /// <summary>Single-frame payloads: 1–7 bytes, no consecutive frames involved.</summary>
    private static readonly Gen<byte[]> SingleFramePayloadGen = Gen.Byte.Array[1, 7];

    /// <summary>Multi-frame payloads, up to Group 02's 198 bytes and beyond.</summary>
    private static readonly Gen<byte[]> MultiFramePayloadGen = Gen.Byte.Array[8, 300];

    [Test]
    public async Task SingleFrame_RoundTrips(CancellationToken _)
    {
        SingleFramePayloadGen.Select(CanIdGen, PaddingGen).Sample(t =>
            {
                var (payload, canId, padding) = t;
                var lines = IsoTpWireFormat.Encode(payload, canId, padding);
                return IsoTpParser.ParseIsoTpResponse(string.Join("\r", lines)).SequenceEqual(payload);
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task MultiFrame_OneFramePerLine_RoundTrips(CancellationToken _)
    {
        MultiFramePayloadGen.Select(CanIdGen, PaddingGen).Sample(t =>
            {
                var (payload, canId, padding) = t;
                var lines = IsoTpWireFormat.Encode(payload, canId, padding);
                return IsoTpParser.ParseIsoTpResponse(string.Join("\r", lines)).SequenceEqual(payload);
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task MultiFrame_AllFramesOnOneLine_RoundTrips(CancellationToken _)
    {
        // Some adapters run every frame together with no separator; the parser has to split them.
        MultiFramePayloadGen.Select(CanIdGen, PaddingGen).Sample(t =>
            {
                var (payload, canId, padding) = t;
                var lines = IsoTpWireFormat.Encode(payload, canId, padding);
                return IsoTpParser.ParseIsoTpResponse(string.Concat(lines)).SequenceEqual(payload);
            },
            iter: Iterations);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ArbitraryInput_NeverThrows(CancellationToken _)
    {
        // Adapters emit junk (partial lines, "NO DATA", "BUFFER FULL", noise). Parsing is best-effort
        // but must not throw — callers treat an empty list as "no payload".
        Gen.Char[' ', 'z'].Array[0, 200].Select(c => new string(c)).Sample(s =>
            {
                IsoTpParser.ParseIsoTpResponse(s);
                return true;
            },
            iter: Iterations);

        await Task.CompletedTask;
    }
}
