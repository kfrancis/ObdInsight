using ObdInsight.SourceGeneration.Attributes;
using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Protocols;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames
{
    /// <summary>
    /// UDS diagnostics service for Nissan Leaf BMS (Battery Management System).
    /// Uses Mode 21 (ReadDataByIdentifier) for battery diagnostics.
    /// </summary>
    [UdsService(0x21, EcuType = "BMS", Description = "Nissan Leaf Battery Diagnostics")]
    internal sealed partial class LeafBmsDiagnostics
    {
        private readonly EcuContext _context;
        private readonly IElmSession _session;

        public LeafBmsDiagnostics(IElmSession session, EcuContext context)
        {
            _session = session;
            _context = context;
        }

        // ISO-TP parsing methods - shared with LeafAze0Charger
        public static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFrames(string[] lines) =>
            ParseIsoTpFramesImpl(lines);

        public static byte[] ReassembleIsoTpPayload(List<(int FrameType, int SeqOrLen, byte[] Data)> frames) =>
            ReassembleIsoTpPayloadImpl(frames);

        // ISO-TP parsing implementation (needed by generated code and LeafAze0Charger)
        private static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFramesImpl(string[] lines)
        {
            var frames = new List<(int FrameType, int SeqOrLen, byte[] Data)>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length < 5) continue;

                var canIdHex = trimmed[..3];
                if (!int.TryParse(canIdHex, System.Globalization.NumberStyles.HexNumber, null, out var canId))
                    continue;
                if (canId < 0x700 || canId > 0x7FF)
                    continue;

                var frameHex = trimmed[3..];
                if (frameHex.Length < 2) continue;

                frameHex = frameHex.Replace("H", "48");

                var frameBytes = new List<byte>();
                for (var i = 0; i + 1 < frameHex.Length; i += 2)
                {
                    if (byte.TryParse(frameHex.AsSpan(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                        frameBytes.Add(b);
                    else
                        break;
                }

                if (frameBytes.Count == 0) continue;

                var pci = frameBytes[0];
                var frameType = (pci >> 4) & 0x0F;
                var frameInfo = pci & 0x0F;

                switch (frameType)
                {
                    case 0:
                        frames.Add((0, frameInfo, frameBytes.Skip(1).ToArray()));
                        break;

                    case 1:
                        if (frameBytes.Count >= 2)
                        {
                            var totalLen = (frameInfo << 8) | frameBytes[1];
                            frames.Add((1, totalLen, frameBytes.Skip(2).ToArray()));
                        }
                        break;

                    case 2:
                        frames.Add((2, frameInfo, frameBytes.Skip(1).ToArray()));
                        break;
                }
            }

            return frames;
        }

        private static byte[] ReassembleIsoTpPayloadImpl(List<(int FrameType, int SeqOrLen, byte[] Data)> frames)
        {
            var payload = new List<byte>();
            var expectedLength = 0;

            var (frameType, seqOrLen, data) = frames.FirstOrDefault(f => f.FrameType == 0 || f.FrameType == 1);
            if (data == null)
                return [];

            if (frameType == 0)
            {
                expectedLength = seqOrLen;
                var dataLen = Math.Min(expectedLength, data.Length);
                payload.AddRange(data.Take(dataLen));
            }
            else
            {
                expectedLength = seqOrLen;
                payload.AddRange(data);

                var consecutiveFrames = frames
                    .Where(f => f.FrameType == 2)
                    .ToList();

                foreach (var (_, _, cfData) in consecutiveFrames)
                {
                    payload.AddRange(cfData);
                    if (payload.Count >= expectedLength)
                        break;
                }
            }

            if (expectedLength > 0 && payload.Count > expectedLength)
                return [.. payload.Take(expectedLength)];

            return [.. payload];
        }

        /// <summary>
        /// PID 0x01 - Battery status including current, voltage, SOC, capacity, and health.
        /// Response length varies by battery model:
        /// - 24kWh/30kWh: 39-41 bytes
        /// - 40kWh+ (ZE1): 49+ bytes
        /// </summary>
        [UdsPid(0x01, Name = "Group01")]
        [UdsResponse(MinLength = 39, MaxLength = 51)]
        [UdsResponseVariant(Length = 39, Model = "24kWh")]
        [UdsResponseVariant(Length = 41, Model = "30kWh")]
        [UdsResponseVariant(Length = 49, Model = "40kWh_ZE1")]
        public partial class Group01Response
        {
            // Capacity (AHR): Different offsets by model, with validation
            [UdsField(Offset = 33, Length = 3, Type = UdsFieldType.UInt24BE, Scale = 0.0001,
                ValidRange = "10..100", AppliesTo = "24kWh,30kWh")]
            [UdsField(Offset = 35, Length = 3, Type = UdsFieldType.UInt24BE, Scale = 0.0001,
                ValidRange = "10..100", AppliesTo = "40kWh_ZE1")]
            public double CapacityAh { get; set; }

            // Current: Bytes 0-3 (signed 32-bit big-endian, /1024 for amps)
            [UdsField(Offset = 0, Length = 4, Type = UdsFieldType.Int32BE, Scale = 1.0 / 1024.0)]
            public double CurrentAmps { get; set; }

            // Health (Hx): Different offsets and scales by model
            [UdsField(Offset = 26, Length = 2, Type = UdsFieldType.UInt16BE, Scale = 0.01, AppliesTo = "24kWh,30kWh")]
            [UdsField(Offset = 28, Length = 2, Type = UdsFieldType.UInt16BE, Scale = 1.0 / 102.4, AppliesTo = "40kWh_ZE1")]
            public double HealthPercent { get; set; }

            // SOC: Only available in ZE1/40kWh+ models
            [UdsField(Offset = 31, Length = 3, Type = UdsFieldType.UInt24BE, Scale = 0.0001, AppliesTo = "40kWh_ZE1")]
            public double? SocPercent { get; set; }

            // Voltage: From CF3 frame bytes 0-1 (per Leaf2018-CAN spec, /100 for volts)
            [UdsField(FrameType = FrameSource.ConsecutiveFrame, FrameSequence = 3, Offset = 0, Length = 2,
                Type = UdsFieldType.UInt16BE, Scale = 0.01)]
            public double VoltageVolts { get; set; }
        }

        /// <summary>
        /// PID 0x02 - Individual cell pair voltages.
        /// Nissan Leaf has 96 cell pairs, each reported as 2 bytes in millivolts.
        /// </summary>
        [UdsPid(0x02, Name = "Group02")]
        [UdsResponse(MinLength = 192)] // 96 cells × 2 bytes
        public partial class Group02Response
        {
            [UdsComputed]
            public int AvgVoltageMv => CellVoltagesMv.Length > 0 ? (int)CellVoltagesMv.Average() : 0;

            [UdsArrayField(Offset = 0, ElementCount = 96, ElementLength = 2,
                            Type = UdsFieldType.UInt16BE, ValidRange = "2500..4500")]
            public int[] CellVoltagesMv { get; set; } = [];

            [UdsComputed]
            public int MaxVoltageMv => CellVoltagesMv.Length > 0 ? CellVoltagesMv.Max() : 0;

            [UdsComputed]
            public int MinVoltageMv => CellVoltagesMv.Length > 0 ? CellVoltagesMv.Min() : 0;
        }
    }
}
