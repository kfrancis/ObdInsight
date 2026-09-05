//HintName: CellDiagnostics.g.cs
#nullable enable
using System;
namespace TestNamespace;
partial class CellDiagnostics
{
    public async System.Threading.Tasks.Task<CellVoltagesResponse?> QueryCellVoltagesAsync(System.Threading.CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lines = await _session.QueryAsync("2102", _context, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!global::ObdInsight.Core.Protocols.IsoTpParser.TryReadPayload(lines, out var payload, _context.RxFilter, "2102")) return null;
        if (payload.Length < 2 || payload[0] != 0x61 || payload[1] != 0x02) return null;
        var data = payload.AsSpan(2);
        var response = new CellVoltagesResponse();
        if (data.Length < 192) return null;
        var cellvoltagesmvValues = new int[96];
        for (int index = 0; index < 96; index++)
        {
            int i = 0 + index * 2;
            var value = (data[i] << 8) | data[i + 1];
            if (value < 2500d || value > 4500d)
                return null;
            if ((double)value < (double)int.MinValue || (double)value > (double)int.MaxValue) return null;
            cellvoltagesmvValues[index] = (int)value;
        }
        response.CellVoltagesMv = cellvoltagesmvValues;
        return response;
    }
}
