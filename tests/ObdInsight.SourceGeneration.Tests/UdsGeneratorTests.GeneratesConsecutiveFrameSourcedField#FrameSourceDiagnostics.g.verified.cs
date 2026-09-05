//HintName: FrameSourceDiagnostics.g.cs
#nullable enable
using System;
namespace TestNamespace;
partial class FrameSourceDiagnostics
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
            if (payload.Length <= 7 || payload.Length < 22) return null;
            var frameData = payload.AsSpan(20, System.Math.Min(7, payload.Length - 20));
            if (frameData.Length < 2) return null;
            var voltagevoltsRaw = (frameData[0] << 8) | frameData[1];
            var value = voltagevoltsRaw * 0.01d;
            if (!double.IsFinite(value) || value < (double)double.MinValue || value > (double)double.MaxValue) return null;
            double converted;
            try { converted = checked((double)value); }
            catch (System.OverflowException) { return null; }
            response.VoltageVolts = converted;
        }
        return response;
    }
}
