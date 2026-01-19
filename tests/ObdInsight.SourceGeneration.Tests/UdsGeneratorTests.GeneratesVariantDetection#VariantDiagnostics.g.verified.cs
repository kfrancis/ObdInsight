//HintName: VariantDiagnostics.g.cs
#nullable enable
namespace TestNamespace;
partial class VariantDiagnostics
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
        var variant = data.Length switch
        {
            39 => "24kWh",
            49 => "40kWh",
            _ => null
        };
        var response = new StatusResponse();
        if (variant == "24kWh")
        {
        if (data.Length >= 28)
        {
            var healthpercentRaw = (data[26] << 8) | data[27];
            var value = healthpercentRaw * 0.01;
            response.HealthPercent = value;
        }
        }
        if (variant == "40kWh")
        {
        if (data.Length >= 30)
        {
            var healthpercentRaw = (data[28] << 8) | data[29];
            var value = healthpercentRaw * 0.009765625;
            response.HealthPercent = value;
        }
        }
        return response;
    }
}
