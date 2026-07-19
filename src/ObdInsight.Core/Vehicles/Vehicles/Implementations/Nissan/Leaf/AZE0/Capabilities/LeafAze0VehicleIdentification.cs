using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    /// Vehicle identification implementation for Nissan Leaf AZE0.
    /// Queries the "CHARGER" ECU (0x792/0x793) for VIN via Mode 21 PID 81.
    /// </summary>
    internal sealed class LeafAze0VehicleIdentification : IVehicleIdentification
    {
        private readonly IElmSession _session;
        private readonly EcuContext _context;

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
        /// Parses VIN from ELM327 response lines containing ISO-TP frames.
        /// Expected format: Mode 21 PID 81 response with header [61 81] followed by VIN ASCII bytes.
        /// </summary>
        private static string? ParseNissanVin(string[] lines)
        {
            if (lines == null || lines.Length == 0)
                return null;

            // Parse ISO-TP frames (reuse existing method from LeafAze0Bms)
            var frames = LeafAze0Bms.ParseIsoTpFrames(lines);
            if (frames.Count == 0)
            {
                Log("[VehicleID VIN] No valid ISO-TP frames");
                return null;
            }

            // Reassemble payload
            var payload = LeafAze0Bms.ReassembleIsoTpPayload(frames);
            Log($"[VehicleID VIN] Reassembled {payload.Length} bytes: {Convert.ToHexString(payload)}");

            // Validate response header (61 81 = positive response to Mode 21 PID 81)
            if (payload.Length < 3 || payload[0] != 0x61 || payload[1] != 0x81)
            {
                Log($"[VehicleID VIN] Invalid header: {Convert.ToHexString(payload.AsSpan(0, Math.Min(10, payload.Length)))}");
                return null;
            }

            // VIN data starts at byte 2, typically 17 ASCII characters
            var vinBytes = payload.AsSpan(2);
            Span<char> vinChars = stackalloc char[17];
            var vinLen = 0;

            foreach (var b in vinBytes)
            {
                if (b == 0x00)
                    break;

                // Captured data sometimes contains spurious non-ASCII bytes (e.g., 0xE3).
                // VINs are restricted to 0-9 and A-Z (excluding I/O/Q), so filter to that set.
                if (b >= (byte)'0' && b <= (byte)'9')
                {
                    vinChars[vinLen++] = (char)b;
                }
                else if (b >= (byte)'A' && b <= (byte)'Z' && b != (byte)'I' && b != (byte)'O' && b != (byte)'Q')
                {
                    vinChars[vinLen++] = (char)b;
                }

                if (vinLen == vinChars.Length)
                    break;
            }

            if (vinLen != vinChars.Length)
            {
                Log($"[VehicleID VIN] Invalid length: {vinLen} (expected 17)");
                return null;
            }

            var vin = new string(vinChars);
            Log($"[VehicleID VIN] Parsed: {vin}");
            return vin;
        }

        private static void Log(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
