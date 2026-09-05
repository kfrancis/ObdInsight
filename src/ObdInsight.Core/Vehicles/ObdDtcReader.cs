using System.Globalization;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Vehicles;

/// <summary>
///     OBD-II Mode 03/07 reader for header-bearing, classical 11-bit CAN responses.
///     Missing or malformed replies never count as a successful clean read.
///     Other diagnostic formats are not inferred.
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
    ///     Functional request context. Results cover only observed responders;
    ///     filter acceptance and silent ECUs cannot establish whole-vehicle coverage.
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
        ct.ThrowIfCancellationRequested();
        var stored = await ReadModeAsync("03", 0x43, ct).ConfigureAwait(false);
        var pending = await ReadModeAsync("07", 0x47, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new DtcReadResult { Stored = stored, Pending = pending };
    }

    private async ValueTask<DtcModeResult> ReadModeAsync(string mode, byte responseSid, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var lines = await _session.QueryAsync(mode, _context, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return Parse(lines, mode, responseSid);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Current ELM framing uses cancellation for its internal deadline.
            return DtcModeResult.Failed(DtcReadStatus.Timeout);
        }
        catch (TimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            return DtcModeResult.Failed(DtcReadStatus.Timeout);
        }
        catch (IOException)
        {
            ct.ThrowIfCancellationRequested();
            // Session recovery can collapse NO DATA / adapter rejection into IOException.
            // Do not guess a more precise cause from an exception message.
            return DtcModeResult.Failed(DtcReadStatus.QueryFailed);
        }
    }

    private static DtcModeResult Parse(string[] lines, string mode, byte sid)
    {
        var responders = new Dictionary<int, Response>();
        var invalidData = false;
        var noData = false;
        foreach (var raw in lines)
        {
            var line = raw.Replace(" ", "").Trim();
            if (line.Length == 0 || line == ">" || line == mode || line == "SEARCHING...")
                continue;
            if (line == "NODATA") { noData = true; continue; }
            if (line.Length < 5 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var id) || id is < 0x700 or > 0x7FF)
            {
                invalidData = true;
                continue;
            }
            if (!responders.TryGetValue(id, out var response))
                responders.Add(id, response = new Response());
            var hex = line.AsSpan(3);
            if (hex.Length % 2 != 0 || hex.Length > 16)
            {
                response.Invalid = true;
                continue;
            }
            var bytes = new byte[hex.Length / 2];
            var valid = true;
            for (var i = 0; i < bytes.Length; i++)
                valid &= byte.TryParse(hex.Slice(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out bytes[i]);
            if (!valid) response.Invalid = true;
            else response.Add(bytes);
        }
        return DtcModeResult.FromResponses(responders.Select(r =>
            new DtcResponderResult(r.Key, r.Value.Decode(sid))), invalidData || (noData && responders.Count > 0));
    }

    // Local DTC trust boundary. Other ISO-TP consumers remain a separate tranche;
    // the permissive shared parser cannot establish diagnostic success.
    private sealed class Response
    {
        private readonly List<byte> _data = [];
        private int _expected;
        private int _nextSequence = 1;
        public bool Invalid { get; set; }

        public void Add(byte[] bytes)
        {
            if (Invalid || bytes.Length == 0) { Invalid = true; return; }
            switch (bytes[0] >> 4)
            {
                case 0 when _expected == 0:
                    _expected = bytes[0] & 0xF;
                    if (_expected is < 1 or > 7 || bytes.Length < _expected + 1) { Invalid = true; return; }
                    _data.AddRange(bytes.AsSpan(1, _expected).ToArray());
                    break;
                case 1 when _expected == 0 && bytes.Length == 8:
                    _expected = ((bytes[0] & 0xF) << 8) | bytes[1];
                    if (_expected <= 7) { Invalid = true; return; }
                    _data.AddRange(bytes.AsSpan(2).ToArray());
                    break;
                case 2 when _expected > _data.Count && _expected > 7:
                    var count = Math.Min(7, _expected - _data.Count);
                    if ((bytes[0] & 0xF) != _nextSequence || bytes.Length < count + 1) { Invalid = true; return; }
                    _data.AddRange(bytes.AsSpan(1, count).ToArray());
                    _nextSequence = (_nextSequence + 1) & 0xF;
                    break;
                default:
                    Invalid = true;
                    break;
            }
        }

        public IReadOnlyList<string>? Decode(byte sid)
        {
            if (Invalid || _expected < 2 || _data.Count != _expected || _data[0] != sid)
                return null;
            var count = _data[1];
            var used = 2 + count * 2;
            if (used > _data.Count || _data.Skip(used).Any(b => b != 0))
                return null;
            var codes = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var hi = _data[2 + i * 2];
                var lo = _data[3 + i * 2];
                if (hi == 0 && lo == 0) return null;
                var letter = "PCBU"[hi >> 6];
                codes.Add($"{letter}{(hi >> 4) & 3:X1}{hi & 15:X1}{lo:X2}");
            }
            return codes;
        }
    }
}
