//HintName: CurrentDiagnostics.g.cs
#nullable enable
using System;
namespace TestNamespace;
partial class CurrentDiagnostics
{
    public async System.Threading.Tasks.Task<StatusResponse?> QueryStatusAsync(System.Threading.CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var lines = await _session.QueryAsync("2101", _context, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!global::ObdInsight.Core.Protocols.IsoTpParser.TryReadPayload(lines, out var payload, _context.RxFilter, "2101")) return null;
        if (payload.Length < 2 || payload[0] != 0x61 || payload[1] != 0x01) return null;
        var data = payload.AsSpan(2);
        var response = new StatusResponse();
        {
            if (data.Length < 4) return null;
            var currentampsRawUnsigned = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
            var currentampsRaw = unchecked((int)currentampsRawUnsigned);
            var value = currentampsRaw * 0.0009765625d;
            if (!double.IsFinite(value) || value < (double)double.MinValue || value > (double)double.MaxValue) return null;
            double converted;
            try { converted = checked((double)value); }
            catch (System.OverflowException) { return null; }
            response.CurrentAmps = converted;
        }
        return response;
    }
}
