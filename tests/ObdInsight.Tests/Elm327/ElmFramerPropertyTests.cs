using System.Text;
using CsCheck;
using ObdInsight.Core.Communication.Elm327;

namespace OdbTestApp.Tests.Elm327;

/// <summary>
///     Property-based tests (CsCheck) for <see cref="ElmFramer" /> framing: the bytes a caller gets
///     back must not depend on where the transport happened to split its reads. Example-based tests
///     can only cover the split points someone thought of; these cover splits mid-frame, mid-prompt
///     and mid-delimiter, which is exactly where the carry-over buffer earns its keep.
///     On failure CsCheck prints the shrunk counterexample plus a seed — rerun that case with
///     <c>seed:</c> on the failing Sample call, then pin it as an example test.
/// </summary>
[Timeout(120_000)]
public class ElmFramerPropertyTests
{
    private const int Iterations = 200;

    /// <summary>
    ///     ELM response alphabet: hex, spaces and line breaks. Excludes '\0' (dropped by the framer) and '>' (the
    ///     prompt).
    /// </summary>
    private static readonly char[] ResponseAlphabet =
        "0123456789ABCDEF abcdefNODATSRHUK:?\r\n".ToCharArray();

    /// <summary>As above, minus CR — used where CR is the delimiter under test.</summary>
    private static readonly char[] ResponseAlphabetNoCr =
        "0123456789ABCDEF abcdefNODATSRHUK:?\n".ToCharArray();

    /// <summary>Chunk sizes spanning sub-line, typical BLE notification (20) and over-buffer (&gt;256) reads.</summary>
    private static readonly Gen<int[]> ChunkSizesGen = Gen.Int[1, 400].Array[1, 12];

    [Test]
    public async Task PromptFramedResponses_SurviveArbitraryChunkBoundaries(CancellationToken ct)
    {
        var gen = PayloadsGen(ResponseAlphabet).Select(ChunkSizesGen);

        await gen.SampleAsync(async t =>
            {
                var (payloads, chunks) = t;

                // Wire format: each response is terminated by the ELM prompt.
                var stream = Encoding.ASCII.GetBytes(string.Concat(payloads.Select(p => p + ">")));
                var framer = new ElmFramer(new ChunkingTransport(stream, chunks));

                foreach (var expected in payloads)
                {
                    var actual = await framer.SendAndReadFrameAsync("ATI", TimeSpan.FromSeconds(5), ct);
                    if (actual != expected)
                    {
                        return false;
                    }
                }

                return true;
            },
            iter: Iterations);
    }

    [Test]
    public async Task ReadUntil_SurvivesArbitraryChunkBoundaries(CancellationToken ct)
    {
        var gen = PayloadsGen(ResponseAlphabetNoCr).Select(ChunkSizesGen);

        await gen.SampleAsync(async t =>
            {
                var (payloads, chunks) = t;

                // Monitoring mode: CR-delimited lines, no prompt.
                var stream = Encoding.ASCII.GetBytes(string.Concat(payloads.Select(p => p + "\r")));
                var framer = new ElmFramer(new ChunkingTransport(stream, chunks));

                foreach (var expected in payloads)
                {
                    var actual = await framer.ReadUntilAsync("\r", TimeSpan.FromSeconds(5), ct);
                    if (actual != expected)
                    {
                        return false;
                    }
                }

                return true;
            },
            iter: Iterations);
    }

    [Test]
    public async Task MixedPromptAndDelimiterReads_SurviveArbitraryChunkBoundaries(CancellationToken ct)
    {
        // A command response followed by monitoring lines: the carry-over buffer has to hand
        // bytes read past the prompt to the next reader, in order.
        var gen = PayloadsGen(ResponseAlphabetNoCr).Select(PayloadsGen(ResponseAlphabetNoCr), ChunkSizesGen);

        await gen.SampleAsync(async t =>
            {
                var (command, lines, chunks) = t;

                var stream = Encoding.ASCII.GetBytes(
                    command[0] + ">" + string.Concat(lines.Select(l => l + "\r")));
                var framer = new ElmFramer(new ChunkingTransport(stream, chunks));

                var response = await framer.SendAndReadFrameAsync("0100", TimeSpan.FromSeconds(5), ct);
                if (response != command[0])
                {
                    return false;
                }

                foreach (var expected in lines)
                {
                    var actual = await framer.ReadUntilAsync("\r", TimeSpan.FromSeconds(5), ct);
                    if (actual != expected)
                    {
                        return false;
                    }
                }

                return true;
            },
            iter: Iterations);
    }

    /// <summary>1–6 responses of 0–120 chars drawn from the given alphabet.</summary>
    private static Gen<string[]> PayloadsGen(char[] alphabet) =>
        Gen.Int[0, alphabet.Length - 1]
            .Array[0, 120]
            .Select(indices => new string(indices.Select(i => alphabet[i]).ToArray()))
            .Array[1, 6];
}
