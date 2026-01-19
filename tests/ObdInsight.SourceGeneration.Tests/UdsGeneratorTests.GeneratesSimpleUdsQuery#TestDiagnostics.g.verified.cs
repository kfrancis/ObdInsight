//HintName: TestDiagnostics.g.cs
#nullable enable
namespace TestNamespace;
partial class TestDiagnostics
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
        if (data.Length >= 2)
        {
            var voltageRaw = (data[0] << 8) | data[1];
            var value = voltageRaw * 0.01;
            response.Voltage = value;
        }
        return response;
    }
}
