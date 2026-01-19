//HintName: CurrentDiagnostics.g.cs
#nullable enable
namespace TestNamespace;
partial class CurrentDiagnostics
{
    public async System.Threading.Tasks.Task<StatusResponse?> QueryStatusAsync(System.Threading.CancellationToken ct = default)
    {
        var lines = await _session.QueryAsync("2101", _context, ct);
        var frames = ParseIsoTpFrames(lines);
        if (frames.Count == 0) return null;
        var payload = ReassembleIsoTpPayload(frames);
        if (payload.Length < 2 || payload[0] != 0x61 || payload[1] != 0x01)
            return null;
        var data = payload.AsSpan(2);
        string? variant = null;
        var response = new StatusResponse();
        if (data.Length >= 4)
        {
            var currentampsRawUnsigned = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
            var currentampsRaw = unchecked((int)currentampsRawUnsigned);
            var value = currentampsRaw * 0.0009765625;
            response.CurrentAmps = value;
        }
        return response;
    }
}
