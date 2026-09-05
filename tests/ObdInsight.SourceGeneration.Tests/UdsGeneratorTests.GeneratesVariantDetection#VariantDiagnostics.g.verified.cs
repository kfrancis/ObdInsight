//HintName: VariantDiagnostics.g.cs
#nullable enable
using System;
namespace TestNamespace;
partial class VariantDiagnostics
{
    public async System.Threading.Tasks.Task<StatusResponse?> QueryStatusAsync(System.Threading.CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lines = await _session.QueryAsync("2101", _context, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!global::ObdInsight.Core.Protocols.IsoTpParser.TryReadPayload(lines, out var payload, _context.RxFilter, "2101")) return null;
        if (payload.Length < 2 || payload[0] != 0x61 || payload[1] != 0x01) return null;
        var data = payload.AsSpan(2);
        string? variant = data.Length switch
        {
            39 => "24kWh",
            49 => "40kWh",
            _ => null
        };
        if (variant is null) return null;
        var response = new StatusResponse();
        if (variant == "24kWh")
        {
            if (data.Length < 28) return null;
            var healthpercentRaw = (data[26] << 8) | data[27];
            var value = healthpercentRaw * 0.01d;
            if (!double.IsFinite(value) || value < (double)double.MinValue || value > (double)double.MaxValue) return null;
            double converted;
            try { converted = checked((double)value); }
            catch (System.OverflowException) { return null; }
            response.HealthPercent = converted;
        }
        if (variant == "40kWh")
        {
            if (data.Length < 30) return null;
            var healthpercentRaw = (data[28] << 8) | data[29];
            var value = healthpercentRaw * 0.009765625d;
            if (!double.IsFinite(value) || value < (double)double.MinValue || value > (double)double.MaxValue) return null;
            double converted;
            try { converted = checked((double)value); }
            catch (System.OverflowException) { return null; }
            response.HealthPercent = converted;
        }
        return response;
    }
}
