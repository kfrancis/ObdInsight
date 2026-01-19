using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Protocols;
using ObdTestApp.Core.Vehicles;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    internal sealed class LeafAze0Bms : IBatteryManagementSystem
    {
        private readonly IElmSession _session;
        private readonly EcuContext _context;

        public LeafAze0Bms(IElmSession session, EcuContext context)
        {
            _session = session;
            _context = context;
        }

        /// <summary>
        /// Parses ISO-TP frames - made internal static so LeafAze0Charger can use it.
        /// </summary>
        internal static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFrames(string[] lines) =>
            ParseIsoTpFramesImpl(lines);

        /// <summary>
        /// Reassembles ISO-TP payload - made internal static so LeafAze0Charger can use it.
        /// </summary>
        internal static byte[] ReassembleIsoTpPayload(List<(int FrameType, int SeqOrLen, byte[] Data)> frames) =>
            ReassembleIsoTpPayloadImpl(frames);

        public async ValueTask<BatteryStatus> GetStatusAsync(CancellationToken ct = default)
        {
            // Nissan-specific: Query Mode 21 PID 01
            var lines = await _session.QueryAsync("2101", _context, ct);

            Log($"[BMS Group01] Received {lines.Length} lines");
            for (var i = 0; i < lines.Length; i++)
                Log($"[BMS Group01] Line {i}: {lines[i]}");

            // Parse ISO-TP frames from ELM327 response
            // Each line format: "7BB102B6101..." (CAN_ID 3 chars + frame bytes as hex, no spaces with AT S0)
            var frames = ParseIsoTpFramesImpl(lines);

            if (frames.Count == 0)
                throw new InvalidOperationException("No valid ISO-TP frames received from BMS");

            // Reassemble ISO-TP payload
            var payload = ReassembleIsoTpPayloadImpl(frames);

            Log($"[BMS Group01] Reassembled {payload.Length} payload bytes: {Convert.ToHexString(payload)}");

            // Validate response header (0x61 0x01 = positive response to 0x21 0x01)
            if (payload.Length < 2 || payload[0] != 0x61 || payload[1] != 0x01)
            {
                throw new InvalidOperationException($"Unexpected response header: {Convert.ToHexString(payload.AsSpan(0, Math.Min(10, payload.Length)))}");
            }

            // Parse the full payload including header using frame-based parsing
            var (socPercent, voltageVolts, currentAmps, capacityAh, hxPercent) = ParseGroup01FromFrames(frames);

            // Map to generic BatteryStatus
            return new BatteryStatus
            {
                SocPercent = socPercent,
                VoltageVolts = voltageVolts,
                CurrentAmps = currentAmps,
                CapacityAh = capacityAh,
                HealthPercent = hxPercent,
                TemperatureC = null
            };
        }

        public async ValueTask<CellVoltageData?> GetCellVoltagesAsync(CancellationToken ct = default)
        {
            // Nissan-specific: Query Mode 21 PID 02
            var lines = await _session.QueryAsync("2102", _context, ct);

            Log($"[BMS Group02] Received {lines.Length} lines");
            for (var i = 0; i < lines.Length; i++)
                Log($"[BMS Group02] Line {i}: {lines[i]}");

            // Parse ISO-TP frames
            var frames = ParseIsoTpFramesImpl(lines);

            if (frames.Count == 0)
            {
                Log("[BMS Group02] No valid frames - returning null");
                return null;
            }

            // Reassemble payload for cell voltages
            var payload = ReassembleIsoTpPayloadImpl(frames);

            Log($"[BMS Group02] Reassembled {payload.Length} payload bytes");

            // Validate response header (0x61 0x02)
            if (payload.Length < 4 || payload[0] != 0x61 || payload[1] != 0x02)
            {
                Log($"[BMS Group02] Invalid header: {Convert.ToHexString(payload.AsSpan(0, Math.Min(10, payload.Length)))}");
                return null;
            }

            // Parse cell voltages - skip header bytes
            var cellData = payload.AsSpan(2);
            var cellVoltages = new List<int>();

            // Nissan Leaf has 96 cell pairs, each 2 bytes big-endian
            for (var i = 0; i + 1 < cellData.Length && cellVoltages.Count < 96; i += 2)
            {
                var voltage = (cellData[i] << 8) | cellData[i + 1];
                // Valid lithium cell voltages: 2500-4500mV
                if (voltage is >= 2500 and <= 4500)
                {
                    cellVoltages.Add(voltage);
                }
                else if (voltage > 0 && voltage < 10000)
                {
                    // May be scaled differently, log and include
                    Log($"[BMS Group02] Cell {cellVoltages.Count}: unusual voltage {voltage}mV");
                    cellVoltages.Add(voltage);
                }
            }

            if (cellVoltages.Count == 0)
            {
                Log("[BMS Group02] No valid cell voltages parsed");
                return null;
            }

            Log($"[BMS Group02] Parsed {cellVoltages.Count} cell voltages, min={cellVoltages.Min()}mV, max={cellVoltages.Max()}mV");

            return new CellVoltageData
            {
                CellVoltagesMv = [.. cellVoltages],
                MinVoltageMv = cellVoltages.Min(),
                MaxVoltageMv = cellVoltages.Max(),
                AvgVoltageMv = (int)cellVoltages.Average()
            };
        }

        /// <summary>
        /// Parses ISO-TP frames from ELM327 response lines.
        /// Handles format like "7BB102B6101000000EB" (CAN_ID + frame bytes, no spaces).
        /// </summary>
        private static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFramesImpl(string[] lines)
        {
            var frames = new List<(int FrameType, int SeqOrLen, byte[] Data)>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length < 5) continue; // Need at least CAN_ID (3) + PCI (2)

                // Check for valid CAN ID prefix (7xx for OBD range)
                var canIdHex = trimmed[..3];
                if (!int.TryParse(canIdHex, System.Globalization.NumberStyles.HexNumber, null, out var canId))
                    continue;
                if (canId < 0x700 || canId > 0x7FF)
                    continue;

                // Parse frame data bytes (everything after CAN ID)
                var frameHex = trimmed[3..];
                if (frameHex.Length < 2) continue;

                // Handle 'H' characters in captured data - they represent ASCII 'H' (0x48)
                frameHex = frameHex.Replace("H", "48");

                // Parse all bytes
                var frameBytes = new List<byte>();
                for (var i = 0; i + 1 < frameHex.Length; i += 2)
                {
                    if (byte.TryParse(frameHex.AsSpan(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                        frameBytes.Add(b);
                    else
                        break;
                }

                if (frameBytes.Count == 0) continue;

                // Parse ISO-TP PCI byte
                var pci = frameBytes[0];
                var frameType = (pci >> 4) & 0x0F;
                var frameInfo = pci & 0x0F;

                switch (frameType)
                {
                    case 0: // Single Frame - length in low nibble
                        frames.Add((0, frameInfo, frameBytes.Skip(1).ToArray()));
                        break;

                    case 1: // First Frame - 12-bit length
                        if (frameBytes.Count >= 2)
                        {
                            var totalLen = (frameInfo << 8) | frameBytes[1];
                            frames.Add((1, totalLen, frameBytes.Skip(2).ToArray()));
                        }
                        break;

                    case 2: // Consecutive Frame - sequence number in low nibble
                        frames.Add((2, frameInfo, frameBytes.Skip(1).ToArray()));
                        break;

                    case 3: // Flow Control - ignore
                        break;
                }
            }

            return frames;
        }

        /// <summary>
        /// Reassembles ISO-TP payload from parsed frames.
        /// ISO-TP consecutive frames use sequence numbers 0-F that wrap around.
        /// For long messages (>112 bytes), we need to maintain arrival order, not sort by sequence.
        /// </summary>
        private static byte[] ReassembleIsoTpPayloadImpl(List<(int FrameType, int SeqOrLen, byte[] Data)> frames)
        {
            var payload = new List<byte>();
            var expectedLength = 0;

            // Find First Frame or Single Frame
            var (frameType, seqOrLen, data) = frames.FirstOrDefault(f => f.FrameType == 0 || f.FrameType == 1);
            if (data == null)
                return [];

            if (frameType == 0)
            {
                // Single Frame - all data in one frame
                expectedLength = seqOrLen;
                var dataLen = Math.Min(expectedLength, data.Length);
                payload.AddRange(data.Take(dataLen));
            }
            else
            {
                // First Frame - multi-frame response
                expectedLength = seqOrLen;
                payload.AddRange(data); // First 6 bytes

                // Add Consecutive Frames in ARRIVAL ORDER (not sorted by sequence number!)
                // ISO-TP sequence numbers are 0-F and wrap around, so sorting doesn't work
                // for messages longer than 112 bytes (16 consecutive frames × 7 bytes).
                // The ELM327 returns frames in order, so we just take them as received.
                var consecutiveFrames = frames
                    .Where(f => f.FrameType == 2)
                    .ToList(); // Keep arrival order, don't sort!

                foreach (var (cfFrameType, cfSeqOrLen, cfData) in consecutiveFrames)
                {
                    payload.AddRange(cfData);
                    if (payload.Count >= expectedLength)
                        break;
                }
            }

            // Trim to expected length
            if (expectedLength > 0 && payload.Count > expectedLength)
                return [.. payload.Take(expectedLength)];

            return [.. payload];
        }

        /// <summary>
        /// Parses Group 01 data from the reassembled ISO-TP payload.
        ///
        /// Uses offsets from OVMS (vehicle_nissanleaf.cpp) which are based on the
        /// reassembled data AFTER stripping the 61 01 service response header:
        ///
        /// For 24/30kWh Leaf (39/41 byte responses):
        /// - Current1: Bytes 0-3 (signed 32-bit big-endian, /1024 for amps)
        /// - Voltage: From Frame 23 data[1-2] per Leaf2018-CAN spec, /100 for volts
        /// - Hx: Bytes 26-27 (big-endian), /100 for percentage
        /// - AHR: Bytes 33-35 (big-endian), /10000 for Ah
        /// - SOC: Not available in Group 01 for these models (use passive CAN)
        ///
        /// For ZE1/40kWh+ (51 byte responses):
        /// - Hx: Bytes 28-29, /102.4 for percentage
        /// - SOC: Bytes 31-33, /10000 for percentage
        /// - AHR: Bytes 35-37, /10000 for Ah
        /// </summary>
        private static (double? SocPercent, double? VoltageVolts, double? CurrentAmps,
                        double? CapacityAh, double? HxPercent) ParseGroup01FromFrames(
            List<(int FrameType, int SeqOrLen, byte[] Data)> frames)
        {
            double? currentAmps = null;
            double? voltageVolts = null;
            double? socPercent = null;
            double? capacityAh = null;
            double? hxPercent = null;

            // Reassemble payload to get contiguous data for OVMS-style offset access
            var payload = ReassembleIsoTpPayloadImpl(frames);

            // Validate response header (61 01)
            if (payload.Length < 4 || payload[0] != 0x61 || payload[1] != 0x01)
            {
                Log($"[BMS Parse] Invalid header, payload length={payload.Length}");
                return (null, null, null, null, null);
            }

            // Data portion starts after 61 01 header
            var data = payload.AsSpan(2);
            var dataLen = data.Length;

            Log($"[BMS Parse] Data length={dataLen} bytes (39=24kWh, 41=30kWh, 49=ZE1)");

            // Current1: Bytes 0-3 (signed 32-bit big-endian, /1024 for amps)
            if (dataLen >= 4)
            {
                var currentUnsigned = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
                var currentSigned = unchecked((int)currentUnsigned);
                currentAmps = currentSigned / 1024.0;
                Log($"[BMS Parse] Current1: data[0-3]=[{data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2}] = 0x{currentUnsigned:X8} / 1024 = {currentAmps:F3}A");
            }

            // Voltage: From Frame 23 (CF3) data[1-2] per Leaf2018-CAN spec
            var cfMap = frames
                .Where(f => f.FrameType == 2)
                .ToDictionary(f => f.SeqOrLen, f => f.Data);

            if (cfMap.TryGetValue(3, out var cf3) && cf3.Length >= 2)
            {
                var voltageRaw = (cf3[0] << 8) | cf3[1];
                voltageVolts = voltageRaw / 100.0;
                Log($"[BMS Parse] Voltage: cf3[0-1]=[{cf3[0]:X2} {cf3[1]:X2}] = 0x{voltageRaw:X4} / 100 = {voltageVolts:F2}V");
            }

            // Hx and AHR: Use OVMS offsets based on response length
            var isZE1 = dataLen >= 49; // ZE1 has 51 bytes total (49 data + 2 header)

            if (isZE1)
            {
                // ZE1/40kWh format
                if (dataLen >= 30)
                {
                    var hxRaw = (data[28] << 8) | data[29];
                    hxPercent = hxRaw / 102.4;
                    Log($"[BMS Parse] Hx (ZE1): data[28-29]=[{data[28]:X2} {data[29]:X2}] = 0x{hxRaw:X4} / 102.4 = {hxPercent:F2}%");
                }

                if (dataLen >= 34)
                {
                    var socRaw = (data[31] << 16) | (data[32] << 8) | data[33];
                    socPercent = socRaw / 10000.0;
                    Log($"[BMS Parse] SOC (ZE1): data[31-33]=[{data[31]:X2} {data[32]:X2} {data[33]:X2}] = 0x{socRaw:X6} / 10000 = {socPercent:F2}%");
                }

                if (dataLen >= 38)
                {
                    var ahrRaw = (data[35] << 16) | (data[36] << 8) | data[37];
                    capacityAh = ahrRaw / 10000.0;
                    Log($"[BMS Parse] AHR (ZE1): data[35-37]=[{data[35]:X2} {data[36]:X2} {data[37]:X2}] = 0x{ahrRaw:X6} / 10000 = {capacityAh:F2}Ah");
                }
            }
            else
            {
                // 24/30kWh format
                if (dataLen >= 28)
                {
                    var hxRaw = (data[26] << 8) | data[27];
                    hxPercent = hxRaw / 100.0;
                    Log($"[BMS Parse] Hx (24/30kWh): data[26-27]=[{data[26]:X2} {data[27]:X2}] = 0x{hxRaw:X4} / 100 = {hxPercent:F2}%");
                }

                // AHR requires at least 36 bytes of data and should be in plausible range
                // (30kWh Leaf has ~66Ah nominal, 24kWh ~55Ah, so valid range ~20-80Ah)
                if (dataLen >= 36)
                {
                    var ahrRaw = (data[33] << 16) | (data[34] << 8) | data[35];
                    var ahrValue = ahrRaw / 10000.0;
                    // Only accept AHR if it's in a plausible range (avoid corrupt/incomplete data)
                    if (ahrValue >= 10.0 && ahrValue <= 100.0)
                    {
                        capacityAh = ahrValue;
                        Log($"[BMS Parse] AHR (24/30kWh): data[33-35]=[{data[33]:X2} {data[34]:X2} {data[35]:X2}] = 0x{ahrRaw:X6} / 10000 = {capacityAh:F2}Ah");
                    }
                    else
                    {
                        Log($"[BMS Parse] AHR (24/30kWh): data[33-35]=[{data[33]:X2} {data[34]:X2} {data[35]:X2}] = {ahrValue:F2}Ah (out of range, ignoring)");
                    }
                }
                else
                {
                    Log($"[BMS Parse] AHR: Insufficient data ({dataLen} bytes, need 36)");
                }

                // SOC for 24/30kWh Leaf is typically read from passive CAN (0x1DB, 0x55B)
                // not from Group 01 polling. We leave SOC as null for these models.
                Log($"[BMS Parse] SOC: Not available in Group 01 for 24/30kWh Leaf (use passive CAN)");
            }

            return (socPercent, voltageVolts, currentAmps, capacityAh, hxPercent);
        }

        private static void Log(string message)
        {
            Serilog.Log.Debug(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
