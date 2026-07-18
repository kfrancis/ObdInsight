//HintName: FrameSourceDiagnostics.g.cs
#nullable enable
namespace TestNamespace;
partial class FrameSourceDiagnostics
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
        var cf3 = frames.FirstOrDefault(f => f.FrameType == 2 && f.SeqOrLen == 3).Data;
        if (cf3?.Length >= 2)
        {
            var voltagevoltsRaw = (cf3[0] << 8) | cf3[1];
            var value = voltagevoltsRaw * 0.01;
            response.VoltageVolts = value;
        }
        return response;
    }
}
