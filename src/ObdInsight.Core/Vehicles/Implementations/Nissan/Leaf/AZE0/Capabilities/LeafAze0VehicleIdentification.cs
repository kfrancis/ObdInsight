using System.Diagnostics;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    ///     Vehicle identification implementation for Nissan Leaf AZE0.
    ///     Queries the "CHARGER" ECU (0x792/0x793) for VIN via Mode 21 PID 81.
    /// </summary>
    internal sealed class LeafAze0VehicleIdentification : IVehicleIdentification
    {
        private readonly EcuContext _context;
        private readonly IElmSession _session;

        public LeafAze0VehicleIdentification(IElmSession session, EcuContext context)
        {
            _session = session;
            _context = context;
        }

        public async ValueTask<string?> GetVinAsync(CancellationToken ct = default)
        {
            // Nissan-specific: Query Mode 21 PID 81 from "CHARGER" ECU.
            // Degradation contract (audit B7): silent ECU / adapter error → null.
            string[] lines;
            try
            {
                lines = await _session.QueryAsync("2181", _context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }

            Log($"[VehicleID VIN] Received {lines.Length} lines");
            for (var i = 0; i < lines.Length; i++)
                Log($"[VehicleID VIN] Line {i}: {lines[i]}");

            return ParseNissanVin(lines);
        }

        /// <summary>
        ///     Parses VIN from ELM327 response lines containing ISO-TP frames.
        ///     Expected format: Mode 21 PID 81 response with header [61 81] followed by VIN ASCII bytes.
        /// </summary>
        private string? ParseNissanVin(string[] lines)
        {
            if (lines == null || lines.Length == 0)
                return null;

            if (!IsoTpParser.TryReadPayload(lines, out var payload, expectedResponder: _context.RxFilter, commandEcho: "2181"))
                return null;

            // Validate response header (61 81 = positive response to Mode 21 PID 81)
            if (payload.Length < 3 || payload[0] != 0x61 || payload[1] != 0x81)
            {
                Log(
                    $"[VehicleID VIN] Invalid header: {Convert.ToHexString(payload.AsSpan(0, Math.Min(10, payload.Length)))}");
                return null;
            }

            // Never filter bytes into a different identity. Accept exactly 17 VIN
            // characters and optional zero terminators inside the validated payload.
            if (payload.Length < 19) return null;
            Span<char> vinChars = stackalloc char[17];
            for (var i = 0; i < vinChars.Length; i++)
            {
                var b = payload[i + 2];
                if (!(b is >= (byte)'0' and <= (byte)'9' ||
                      b is >= (byte)'A' and <= (byte)'Z' && b is not ((byte)'I') and not ((byte)'O') and not ((byte)'Q')))
                    return null;
                vinChars[i] = (char)b;
            }
            foreach (var trailing in payload.AsSpan(19))
                if (trailing != 0) return null;

            var vin = new string(vinChars);
            Log($"[VehicleID VIN] Parsed: {vin}");
            return vin;
        }

        private static void Log(string message)
        {
            Debug.WriteLine(message);
        }
    }
}
