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
        var result = IsoTpParser.ParseResponses(lines, mode);
        return DtcModeResult.FromResponses(result.Responses.Select(response =>
            new DtcResponderResult(response.CanId, response.Error == IsoTpError.None && response.CanId is >= 0x700 and <= 0x7FF
                ? Decode(response.Payload.Span, sid) : null)), result.HasUnattributedErrors);
    }

    private static IReadOnlyList<string>? Decode(ReadOnlySpan<byte> data, byte sid)
    {
        if (data.Length < 2 || data[0] != sid) return null;
        var count = data[1];
        var used = 2 + count * 2;
        if (used > data.Length) return null;
        foreach (var padding in data[used..])
            if (padding != 0) return null;
        var codes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var hi = data[2 + i * 2];
            var lo = data[3 + i * 2];
            if (hi == 0 && lo == 0) return null;
            var letter = "PCBU"[hi >> 6];
            codes.Add($"{letter}{(hi >> 4) & 3:X1}{hi & 15:X1}{lo:X2}");
        }
        return codes;
    }
}
