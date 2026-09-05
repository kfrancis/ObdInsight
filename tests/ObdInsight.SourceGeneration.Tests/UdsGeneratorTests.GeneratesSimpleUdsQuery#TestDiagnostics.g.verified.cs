//HintName: TestDiagnostics.g.cs
#nullable enable
using System;
namespace TestNamespace;
partial class TestDiagnostics
{
    public async System.Threading.Tasks.Task<StatusResponse?> QueryStatusAsync(System.Threading.CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lines = await _session.QueryAsync("2101", _context, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!global::ObdInsight.Core.Protocols.IsoTpParser.TryReadPayload(lines, out var payload, _context.RxFilter, "2101")) return null;
        if (payload.Length < 2 || payload[0] != 0x61 || payload[1] != 0x01) return null;
        var data = payload.AsSpan(2);
        if (data.Length < 10) return null;
        if (data.Length > 50) return null;
        var response = new StatusResponse();
        {
            if (data.Length < 2) return null;
            var voltageRaw = (data[0] << 8) | data[1];
            var value = voltageRaw * 0.01d;
            if (!double.IsFinite(value) || value < (double)double.MinValue || value > (double)double.MaxValue) return null;
            double converted;
            try { converted = checked((double)value); }
            catch (System.OverflowException) { return null; }
            response.Voltage = converted;
        }
        return response;
    }
}
