using System.Globalization;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Vehicles;

/// <summary>
///     Vehicle-agnostic OBD-II DTC reader (roadmap B5): Mode 03 (stored) + Mode 07
///     (pending) over the functional broadcast address, tolerating multi-ECU responses.
///     Each responding ECU's ISO-TP payload is reassembled independently by CAN header;
///     codes are decoded to standard "P0xxx"-style strings and deduplicated.
///     Degradation contract: adapter errors, NO DATA, silent ECUs, and malformed frames
///     all yield empty code lists — never an exception (cancellation excepted).
///     UDS 0x19 per-ECU reads are a separate roadmap item.
/// </summary>
public sealed class ObdDtcReader : IDiagnosticTroubleCodes
{
    private readonly EcuContext _context;
    private readonly IElmSession _session;

    public ObdDtcReader(IElmSession session, EcuContext context)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    ///     Functional OBD-II context: request on 0x7DF, accept the full 0x7E8-0x7EF
    ///     response range via the ELM327 "X" don't-care filter nibble. Adapters whose
    ///     firmware rejects "AT CRA 7EX" keep their previous filter — the reader then
    ///     degrades to whatever that filter admits (worst case: empty results).
    /// </summary>
    public static EcuContext FunctionalContext { get; } = new()
    {
        Name = "OBD-II Functional (DTC)",
        TxHeader = "7DF",
        RxFilter = "7EX",
        FlowControlHeader = "7E0",
        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    public async ValueTask<DtcReadResult> GetDtcsAsync(CancellationToken ct = default)
    {
        var stored = await ReadModeAsync("03", 0x43, ct);
        var pending = await ReadModeAsync("07", 0x47, ct);
        return new DtcReadResult { StoredCodes = stored, PendingCodes = pending };
    }

    private async ValueTask<IReadOnlyList<string>> ReadModeAsync(
        string mode, byte responseSid, CancellationToken ct)
    {
        string[] lines;
        try
        {
            lines = await _session.QueryAsync(mode, _context, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // NO DATA / adapter error / silent bus — nothing readable, not a failure.
            return [];
        }

        var codes = new List<string>();
        foreach (var payload in ReassemblePerEcu(lines))
        {
            DecodeDtcPayload(payload, responseSid, codes);
        }

        return codes.Distinct().ToList();
    }

    /// <summary>
    ///     Groups response lines by their 3-digit CAN header and reassembles each ECU's
    ///     ISO-TP payload (SF, or FF + CFs in arrival order). One frame per line
    ///     (the ELM327 line format this stack produces everywhere else); non-frame lines
    ///     are skipped.
    /// </summary>
    private static List<byte[]> ReassemblePerEcu(string[] lines)
    {
        var perEcu = new Dictionary<string, (List<byte> Data, int ExpectedLength)>();

        foreach (var raw in lines)
        {
            var line = raw.Replace(" ", "").Trim();
            if (line.Length < 5 || !line[..3].All(Uri.IsHexDigit))
            {
                continue;
            }

            var header = line[..3];
            var hex = line[3..];
            if (hex.Length % 2 == 1)
            {
                hex = hex[..^1]; // stray trailing nibble — drop it
            }

            var bytes = new byte[hex.Length / 2];
            var valid = true;
            for (var i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, null, out bytes[i]))
                {
                    valid = false;
                    break;
                }
            }

            if (!valid || bytes.Length == 0)
            {
                continue;
            }

            var pciType = bytes[0] >> 4;
            switch (pciType)
            {
                case 0: // Single frame
                    {
                        var length = bytes[0] & 0xF;
                        if (length > 0 && bytes.Length >= 1 + length)
                        {
                            perEcu[header] = (bytes.Skip(1).Take(length).ToList(), length);
                        }

                        break;
                    }
                case 1: // First frame
                    {
                        if (bytes.Length >= 2)
                        {
                            var length = ((bytes[0] & 0xF) << 8) | bytes[1];
                            perEcu[header] = (bytes.Skip(2).ToList(), length);
                        }

                        break;
                    }
                case 2: // Consecutive frame — append in arrival order
                    {
                        if (perEcu.TryGetValue(header, out var entry))
                        {
                            entry.Data.AddRange(bytes.Skip(1));
                        }

                        break;
                    }
            }
        }

        var payloads = new List<byte[]>();
        foreach (var (data, expectedLength) in perEcu.Values)
        {
            payloads.Add(data.Take(expectedLength).ToArray());
        }

        return payloads;
    }

    /// <summary>
    ///     Decodes one ECU's Mode 03/07 payload: [SID+0x40] [count] [2-byte DTC]* on CAN.
    ///     Zero pairs (padding) are skipped; an implausible count byte falls back to
    ///     consuming all pairs present.
    /// </summary>
    private static void DecodeDtcPayload(byte[] payload, byte responseSid, List<string> codes)
    {
        if (payload.Length < 2 || payload[0] != responseSid)
        {
            return;
        }

        var count = payload[1];
        var pairsAvailable = (payload.Length - 2) / 2;
        var pairs = count <= pairsAvailable ? count : pairsAvailable;

        for (var i = 0; i < pairs; i++)
        {
            var hi = payload[2 + i * 2];
            var lo = payload[3 + i * 2];
            if (hi == 0 && lo == 0)
            {
                continue;
            }

            codes.Add(DecodeDtc(hi, lo));
        }
    }

    /// <summary>SAE J2012 two-byte DTC → "P0143"-style string (nibbles are hex digits).</summary>
    private static string DecodeDtc(byte hi, byte lo)
    {
        var letter = ((hi >> 6) & 0x3) switch
        {
            0 => 'P',
            1 => 'C',
            2 => 'B',
            _ => 'U'
        };

        return $"{letter}{(hi >> 4) & 0x3:X1}{hi & 0xF:X1}{lo >> 4:X1}{lo & 0xF:X1}";
    }
}
