//HintName: CurrentDiagnostics.g.cs
#nullable enable
using System;
namespace TestNamespace;
partial class CurrentDiagnostics
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
        var response = new StatusResponse();
        {
            if (data.Length < 4) return Invalid();
            var currentampsRawUnsigned = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
            var currentampsRaw = unchecked((int)currentampsRawUnsigned);
            var value = currentampsRaw * 0.0009765625d;
            if (!double.IsFinite(value) || value < (double)double.MinValue || value > (double)double.MaxValue) return Invalid();
            double converted;
            try { converted = checked((double)value); }
            catch (System.OverflowException) { return Invalid(); }
            response.CurrentAmps = converted;
        }
        return new global::ObdInsight.Core.Protocols.Observed<StatusResponse?>(response, reply.Observation);
    }
}
