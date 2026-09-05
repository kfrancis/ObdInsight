//HintName: VariantDiagnostics.g.cs
#nullable enable
using System;
namespace TestNamespace;
partial class VariantDiagnostics
{
    public async System.Threading.Tasks.Task<global::ObdInsight.Core.Protocols.Observed<StatusResponse?>> QueryStatusAsync(System.Threading.CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var reply = await _session.QueryResponseAsync("2101", _context, ct).ConfigureAwait(false);
        var lines = reply.Value;
        global::ObdInsight.Core.Protocols.Observed<StatusResponse?> Invalid() => new(null, reply.Observation with { Quality = global::ObdInsight.Core.Protocols.ObservationQuality.Invalid });
        ct.ThrowIfCancellationRequested();
        if (!global::ObdInsight.Core.Protocols.IsoTpParser.TryReadPayload(lines, out var payload, _context.RxFilter, "2101")) return Invalid();
        if (payload.Length < 2 || payload[0] != 0x61 || payload[1] != 0x01) return Invalid();
        var data = payload.AsSpan(2);
        string? variant = data.Length switch
        {
            39 => "24kWh",
            49 => "40kWh",
            _ => null
        };
        if (variant is null) return Invalid();
        var response = new StatusResponse();
        if (variant == "24kWh")
        {
            if (data.Length < 28) return Invalid();
            var healthpercentRaw = (data[26] << 8) | data[27];
            var value = healthpercentRaw * 0.01d;
            if (!double.IsFinite(value) || value < (double)double.MinValue || value > (double)double.MaxValue) return Invalid();
            double converted;
            try { converted = checked((double)value); }
            catch (System.OverflowException) { return Invalid(); }
            response.HealthPercent = converted;
        }
        if (variant == "40kWh")
        {
            if (data.Length < 30) return Invalid();
            var healthpercentRaw = (data[28] << 8) | data[29];
            var value = healthpercentRaw * 0.009765625d;
            if (!double.IsFinite(value) || value < (double)double.MinValue || value > (double)double.MaxValue) return Invalid();
            double converted;
            try { converted = checked((double)value); }
            catch (System.OverflowException) { return Invalid(); }
            response.HealthPercent = converted;
        }
        return new global::ObdInsight.Core.Protocols.Observed<StatusResponse?>(response, reply.Observation);
    }
}
