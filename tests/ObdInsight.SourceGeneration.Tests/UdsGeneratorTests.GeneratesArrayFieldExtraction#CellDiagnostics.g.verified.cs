//HintName: CellDiagnostics.g.cs
#nullable enable
namespace TestNamespace;
partial class CellDiagnostics
{
    public async System.Threading.Tasks.Task<CellVoltagesResponse?> QueryCellVoltagesAsync(System.Threading.CancellationToken ct = default)
    {
        var lines = await _session.QueryAsync("2102", _context, ct);
        var frames = ParseIsoTpFrames(lines);
        if (frames.Count == 0) return null;
        var payload = ReassembleIsoTpPayload(frames);
        if (payload.Length < 2 || payload[0] != 0x61 || payload[1] != 0x02)
            return null;
        var data = payload.AsSpan(2);
        string? variant = null;
        var response = new CellVoltagesResponse();
        var cellvoltagesmvList = new System.Collections.Generic.List<int>();
        for (int i = 0; i + 1 < data.Length && cellvoltagesmvList.Count < 96; i += 2)
        {
            var value = (data[i] << 8) | data[i + 1];
            if (value >= 2500 && value <= 4500)
                cellvoltagesmvList.Add(value);
        }
        response.CellVoltagesMv = cellvoltagesmvList.ToArray();
        return response;
    }
}
