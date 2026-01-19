using Spectre.Console;

namespace ObdTestApp.Vehicles;

public class NissanLeaf : VehicleProfile
{
    public override string Make => "Nissan";
    public override string Model => "Leaf";

    static readonly VehicleVariant Gen1 = new(
        new("ZE0-2010-2012"),
        "Gen1 (2010–2012) ZE0",
        2010, 2012,
        "ZE0",
        new Dictionary<string, object?>
        {
            [VariantAttr.PackKwh] = 24,
            [VariantAttr.Motor] = "EM61",
            [VariantAttr.Chemistry] = "LMO Canary",
            [VariantAttr.MaxChargeVolts] = 392
        });


    static readonly VehicleVariant Gen2 = new(
        new("AZE0-0-2013-2014"),
        "Gen2 (2013–2014) AZE0-0",
        2013, 2014,
        "AZE0-0",
        new Dictionary<string, object?>
        {
            [VariantAttr.PackKwh] = 24,
            [VariantAttr.Motor] = "EM57",
            [VariantAttr.Chemistry] = "LMO Wolf",
            [VariantAttr.MaxChargeVolts] = 396
        });

    static readonly VehicleVariant Gen2_5 = new(
        new("AZE0-1-2013-2014"),
        "Gen2.5 (2013–2014) AZE0-1",
        2013, 2014,
        "AZE0-1",
        new Dictionary<string, object?>
        {
            [VariantAttr.PackKwh] = 24,
            [VariantAttr.Motor] = "EM57",
            [VariantAttr.Chemistry] = "LMO Lizard",
            [VariantAttr.MaxChargeVolts] = 396
        });

    static readonly VehicleVariant Gen3 = new(
        new("AZE0-2-2016-2017"),
        "Gen3 (2016–2017) AZE0-2",
        2016, 2017,
        "AZE0-2",
        new Dictionary<string, object?>
        {
            [VariantAttr.PackKwh] = 30,
            [VariantAttr.Motor] = "EM57",
            [VariantAttr.Chemistry] = "NMC",
            [VariantAttr.MaxChargeVolts] = 396
        });

    static VehicleVariant Gen4 => new(
        new("AZE0-2-2016-2017"),
        "Gen3 (2016–2017) AZE0-2",
        2016, 2017,
        "AZE0-2",
        new Dictionary<string, object?>
        {
            [VariantAttr.PackKwh] = 30,
            [VariantAttr.Motor] = "EM57",
            [VariantAttr.Chemistry] = "NMC",
            [VariantAttr.MaxChargeVolts] = 396
        });

    static readonly VehicleVariant Gen5 = new(
        new("ZE1-62-2019+"),
        "Gen5 (2019–) ZE1 e+ 62kWh",
        2019, null,
        "ZE1 e+",
        new Dictionary<string, object?>
        {
            [VariantAttr.PackKwh] = 62,
            [VariantAttr.Motor] = "EM57 160kW",
            [VariantAttr.Chemistry] = "NMC",
            [VariantAttr.MaxChargeVolts] = 404
        });

    public override IReadOnlyList<VehicleVariant> Variants { get; } =
        [Gen1, Gen2, Gen2_5, Gen3, Gen4, Gen5];

    public override IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session) =>
        variantId.Value switch
        {
            "AZE0-2-2016-2017" => new LeafAze0CommandSet(session),
            _ => throw new NotSupportedException($"Unknown/unsupported Leaf variant: {variantId.Value}")
        };
}

public static class LeafAze0Contexts
{
    static EcuContext ReqResp(string name, string tx, string rx) => new()
    {
        Name = name,
        TxHeader = tx,
        RxFilter = rx,
        FlowControlHeader = tx,
        FlowControlData = "300000",
        FlowControlMode = "1",
        EnableHeaders = true,
        EnableAutoFormatting = true,
        CommunicationMode = EcuCommunicationMode.RequestResponse
    };

    public static EcuContext Vcm => ReqResp("VCM", "797", "79A");
    public static EcuContext Bcm => ReqResp("BCM", "745", "765");
    public static EcuContext Abs => ReqResp("ABS", "740", "760");
    public static EcuContext LbcBms => ReqResp("LBC/BMS", "79B", "7BB");
    public static EcuContext InverterMc => ReqResp("INVERTER/MC", "784", "78C");
    public static EcuContext Meter => ReqResp("M&A (Meter)", "743", "763");
    public static EcuContext Hvac => ReqResp("HVAC", "744", "764");

    public static EcuContext HvacBroadcast => new()
    {
        Name = "HVAC Broadcast (0x54A-0x54F)",
        TxHeader = "000",            // unused in monitoring mode
        RxFilter = "000",            // unused in monitoring mode
        FlowControlHeader = "000",   // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Use monitor all + filter, or a monitor receiver variant if you prefer.
        MonitoringCommand = "AT MA",

        // Accept 0x54A-0x54F by masking the lower nibble:
        // (id & 0xFF0) == 0x540  => matches 0x540..0x54F
        CanFilterMask = "FF0",
        CanFilterPattern = "540",

        ExpectedCanIds = ["54A", "54B", "54C", "54F"],

        EnableHeaders = true,
        EnableAutoFormatting = true
    };
    public static EcuContext Brake => ReqResp("BRAKE", "70E", "70F");
    public static EcuContext Vsp => ReqResp("VSP", "73F", "761");
    public static EcuContext Eps => ReqResp("EPS", "742", "762");
    public static EcuContext Tcu => ReqResp("TCU", "746", "783");
    public static EcuContext MultiAv => ReqResp("Multi AV", "747", "767");
    public static EcuContext IpdmEr => ReqResp("IPDM E/R", "74D", "76D");
    public static EcuContext Airbag => ReqResp("AIRBAG", "752", "772");
    public static EcuContext Charger => ReqResp("CHARGER", "792", "793");
    public static EcuContext Shift => ReqResp("SHIFT", "79D", "7BD");

    public static EcuContext Consult3Plus => new()
    {
        Name = "Consult3+",
        TxHeader = "7D2",
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,
        MonitoringCommand = "AT MA",
        CanFilterMask = "F00",
        CanFilterPattern = "700",
        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    public static EcuContext Avm => new()
    {
        Name = "AVM",
        TxHeader = "7B7",
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,
        MonitoringCommand = "AT MA",
        CanFilterMask = "FFF",
        CanFilterPattern = "7B7",
        EnableHeaders = true,
        EnableAutoFormatting = true
    };
    public static IReadOnlyList<EcuContext> All { get; } =
    [
        Vcm, Bcm, Abs, LbcBms, InverterMc, Meter, Hvac, Brake, Vsp, Eps, Tcu, MultiAv, IpdmEr, Airbag, Charger, Shift, Consult3Plus
    ];

    public static IReadOnlyDictionary<string, EcuContext> ByName { get; } =
        All.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
}
public sealed class LeafAze0CommandSet : VehicleCommandSet
{
    public LeafAze0CommandSet(IElmSession session)
    {
        Add<IHvac>(new LeafAze0Hvac(session, LeafAze0Contexts.HvacBroadcast));
        Add<IBrake>(new LeafAze0Brake(session, LeafAze0Contexts.Brake));
        Add<IVcm>(new LeafAze0Vcm(session, LeafAze0Contexts.Vcm));
        Add<IBatteryManagementSystem>(new LeafAze0Bms(session, LeafAze0Contexts.LbcBms));
        Add<ICharger>(new LeafAze0Charger(session, LeafAze0Contexts.Charger));
    }
}

internal sealed class LeafAze0Bms : IBatteryManagementSystem
{
    private readonly IElmSession _session;
    private readonly EcuContext _context;

    public LeafAze0Bms(IElmSession session, EcuContext context)
    {
        _session = session;
        _context = context;
    }

    public async ValueTask<BatteryStatus> GetStatusAsync(CancellationToken ct = default)
    {
        // Nissan-specific: Query Mode 21 PID 01
        var lines = await _session.QueryAsync("2101", _context, ct);

        Log($"[BMS Group01] Received {lines.Length} lines");
        for (var i = 0; i < lines.Length; i++)
            Log($"[BMS Group01] Line {i}: {lines[i]}");

        // Parse ISO-TP frames from ELM327 response
        // Each line format: "7BB102B6101..." (CAN_ID 3 chars + frame bytes as hex, no spaces with AT S0)
        var frames = ParseIsoTpFrames(lines);

        if (frames.Count == 0)
            throw new InvalidOperationException("No valid ISO-TP frames received from BMS");

        // Reassemble ISO-TP payload
        var payload = ReassembleIsoTpPayload(frames);

        Log($"[BMS Group01] Reassembled {payload.Length} payload bytes: {Convert.ToHexString(payload)}");

        // Validate response header (0x61 0x01 = positive response to 0x21 0x01)
        if (payload.Length < 2 || payload[0] != 0x61 || payload[1] != 0x01)
        {
            throw new InvalidOperationException($"Unexpected response header: {Convert.ToHexString(payload.AsSpan(0, Math.Min(10, payload.Length)))}");
        }

        // Parse the full payload including header using frame-based parsing
        var result = ParseGroup01FromFrames(frames);

        // Map to generic BatteryStatus
        return new BatteryStatus
        {
            SocPercent = result.SocPercent,
            VoltageVolts = result.VoltageVolts,
            CurrentAmps = result.CurrentAmps,
            CapacityAh = result.CapacityAh,
            HealthPercent = result.HxPercent,
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
        var frames = ParseIsoTpFrames(lines);

        if (frames.Count == 0)
        {
            Log("[BMS Group02] No valid frames - returning null");
            return null;
        }

        // Reassemble payload for cell voltages
        var payload = ReassembleIsoTpPayload(frames);

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
            CellVoltagesMv = cellVoltages.ToArray(),
            MinVoltageMv = cellVoltages.Min(),
            MaxVoltageMv = cellVoltages.Max(),
            AvgVoltageMv = (int)cellVoltages.Average()
        };
    }

    /// <summary>
    /// Parses ISO-TP frames from ELM327 response lines.
    /// Handles format like "7BB102B6101000000EB" (CAN_ID + frame bytes, no spaces).
    /// </summary>
    private static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFrames(string[] lines)
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
    private static byte[] ReassembleIsoTpPayload(List<(int FrameType, int SeqOrLen, byte[] Data)> frames)
    {
        var payload = new List<byte>();
        var expectedLength = 0;

        // Find First Frame or Single Frame
        var firstFrame = frames.FirstOrDefault(f => f.FrameType == 0 || f.FrameType == 1);
        if (firstFrame.Data == null)
            return [];

        if (firstFrame.FrameType == 0)
        {
            // Single Frame - all data in one frame
            expectedLength = firstFrame.SeqOrLen;
            var dataLen = Math.Min(expectedLength, firstFrame.Data.Length);
            payload.AddRange(firstFrame.Data.Take(dataLen));
        }
        else
        {
            // First Frame - multi-frame response
            expectedLength = firstFrame.SeqOrLen;
            payload.AddRange(firstFrame.Data); // First 6 bytes

            // Add Consecutive Frames in ARRIVAL ORDER (not sorted by sequence number!)
            // ISO-TP sequence numbers are 0-F and wrap around, so sorting doesn't work
            // for messages longer than 112 bytes (16 consecutive frames × 7 bytes).
            // The ELM327 returns frames in order, so we just take them as received.
            var consecutiveFrames = frames
                .Where(f => f.FrameType == 2)
                .ToList(); // Keep arrival order, don't sort!

            foreach (var cf in consecutiveFrames)
            {
                payload.AddRange(cf.Data);
                if (payload.Count >= expectedLength)
                    break;
            }
        }

        // Trim to expected length
        if (expectedLength > 0 && payload.Count > expectedLength)
            return payload.Take(expectedLength).ToArray();

        return payload.ToArray();
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
        var payload = ReassembleIsoTpPayload(frames);

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
        bool isZE1 = dataLen >= 49; // ZE1 has 51 bytes total (49 data + 2 header)

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

internal sealed class LeafAze0Charger : ICharger
{
    private readonly IElmSession _session;
    private readonly EcuContext _context;

    public LeafAze0Charger(IElmSession session, EcuContext context)
    {
        _session = session;
        _context = context;
    }

    public async ValueTask<string?> GetVinAsync(CancellationToken ct = default)
    {
        // Nissan-specific: Query Mode 21 PID 81
        var lines = await _session.QueryAsync("2181", _context, ct);
        var response = string.Join("\n", lines);
        return ParseNissanVin(response);
    }

    public async ValueTask<ChargingStatus?> GetChargingStatusAsync(CancellationToken ct = default)
    {
        // Nissan Leaf might have charging status in different PID
        // Or might need to monitor broadcast frames
        throw new NotImplementedException("Charging status for Nissan Leaf");
    }

    private static string? ParseNissanVin(string response)
    {
        // Your existing VIN parsing logic
        return "";
    }
}

internal class LeafAze0Vcm : IVcm
{
    /// <summary>
    /// Leaf AZE0 VCM DID used for gear position.
    /// </summary>
    /// <remarks>
    /// Request (UDS RDBI):
    ///   22 11 56
    ///
    /// Often displayed in ISO-TP single-frame 8-byte format:
    ///   03 22 11 56 00 00 00 00
    ///
    /// Response:
    ///   62 11 56 [gear] ...
    /// Where gear is typically:
    ///   1=Park, 2=Reverse, 3=Neutral, 4=Drive, 7=Eco
    /// </remarks>
    const ushort Did_GearPosition = 0x1156;


    private IElmSession _session;
    private EcuContext _vcm;

    public LeafAze0Vcm(IElmSession session, EcuContext vcm)
    {
        _session = session;
        _vcm = vcm;
    }

    public async ValueTask<GearPosition> GetGearPositionAsync(CancellationToken ct = default)
    {
        // Logical UDS payload (no ISO-TP framing here):
        // 22 11 56
        var payload = Uds.BuildReadDidPayload(Did_GearPosition);

        // Depending on your session API, you may send bytes or a hex string.
        // If your QueryAsync takes a string, convert payload to "221156".
        var requestHex = Convert.ToHexString(payload); // "221156"

        // --- Call into your existing session ---
        // Case A: QueryAsync returns a string (very common with ELM stacks).
        var raw = await _session.QueryAsync(requestHex, _vcm, ct);

        // Parse bytes from the response text, then extract the ISO-TP payload if present.
        var bytes = Hex.ParseBytes(raw[0]);
        var udsPayload = Hex.TryExtractIsoTpPayload(bytes);

        // Validate + extract DID data bytes.
        var data = Uds.ParseReadDidResponse(udsPayload, Did_GearPosition);

        // For this DID, the first data byte is the gear.
        var gearByte = data[0];
        return gearByte switch
        {
            1 => GearPosition.Park,
            2 => GearPosition.Reverse,
            3 => GearPosition.Neutral,
            4 => GearPosition.Drive,
            7 => GearPosition.Eco,
            _ => GearPosition.Unknown
        };
    }
}

internal class LeafAze0Brake : IBrake
{
    private IElmSession _session;
    private EcuContext _brake;

    public LeafAze0Brake(IElmSession session, EcuContext brake)
    {
        _session = session;
        _brake = brake;
    }

    public ValueTask<BrakeStatus> GetStatusAsync(CancellationToken ct = default)
    {
        throw new NotImplementedException();
    }
}

internal sealed class LeafAze0Hvac : IHvac
{
    readonly IElmSession _session;
    readonly EcuContext _context;

    // The HVAC broadcast IDs we care about per your screenshots
    const int Id54A = 0x54A;
    const int Id54B = 0x54B;
    const int Id54C = 0x54C;
    const int Id54F = 0x54F;

    public LeafAze0Hvac(IElmSession session, EcuContext context)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _context = context ?? throw new ArgumentNullException(nameof(context));
        if (_context.CommunicationMode != EcuCommunicationMode.PassiveMonitoring)
            throw new ArgumentException("HVAC status requires PassiveMonitoring context (0x54A-0x54F).", nameof(context));
    }

    /// <summary>
    /// Reads current HVAC status by monitoring Leaf AZE0 HVAC broadcast frames:
    /// - 0x54A: setpoint/ambient-ish fields (setpoint raw in byte4)
    /// - 0x54B: fan speed nibble (bits 36..39), vent modes (bytes2/3)
    /// - 0x54C: outside ambient temp, evap temp, A/C status bits, rear defrost, fan voltage
    /// - 0x54F: interior intake temp, A/C power, heater power, auto amp status bits
    ///
    /// These frames transmit about every 100ms.
    /// </summary>
    public async ValueTask<HvacStatus> GetStatusAsync(CancellationToken ct = default)
    {
        // A small window is usually enough since these frames are ~100ms periodic.
        var window = TimeSpan.FromMilliseconds(400);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(window);

        // Ensure your session is in monitoring mode for the HVAC filter.
        // If your MonitorFramesAsync already enters monitoring mode internally, remove this.
        await _session.EnterMonitoringModeAsync(_context, ct);

        ReadOnlyMemory<byte>? f54a = null;
        ReadOnlyMemory<byte>? f54b = null;
        ReadOnlyMemory<byte>? f54c = null;
        ReadOnlyMemory<byte>? f54f = null;

        try
        {
            await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
            {
                if (frame.Data.Length < 8) continue; // ignore malformed

                switch (frame.CanId)
                {
                    case Id54A: f54a = frame.Data; break;
                    case Id54B: f54b = frame.Data; break;
                    case Id54C: f54c = frame.Data; break;
                    case Id54F: f54f = frame.Data; break;
                }

                // Exit early once we have everything.
                if (f54a.HasValue && f54b.HasValue && f54c.HasValue && f54f.HasValue)
                    break;
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // timeout window ended; return what we captured (partial is OK)
        }
        finally
        {
            // If you have an explicit stop/exit, use it; otherwise omit.
            await _session.ExitMonitoringModeAsync(ct);
        }

        // Build status. Missing frames => nulls/false defaults.
        return new HvacStatus
        {
            SetpointRaw = f54a.HasValue ? Decode54A_SetpointRaw(f54a.Value.Span) : null,

            FanSpeed = f54b.HasValue ? Decode54B_FanSpeed(f54b.Value.Span) : null,

            ClimateControlOn = f54c.HasValue && Decode54C_ClimateControlOn(f54c.Value.Span),
            AcOn = f54c.HasValue && Decode54C_AcOn(f54c.Value.Span),
            RearDefrostOn = f54c.HasValue && Decode54C_RearDefrostOn(f54c.Value.Span),

            OutsideAmbientTempC = f54c.HasValue ? Decode54C_OutsideAmbientTempC(f54c.Value.Span) : null,
            EvaporatorTempC = f54c.HasValue ? Decode54C_EvaporatorTempC(f54c.Value.Span) : null,
            FanVoltageV = f54c.HasValue ? Decode54C_FanVoltageV(f54c.Value.Span) : null,

            InteriorIntakeTempC = f54f.HasValue ? Decode54F_InteriorIntakeTempC(f54f.Value.Span) : null,
            AcPowerWatts = f54f.HasValue ? Decode54F_AcPowerWatts(f54f.Value.Span) : null,
            HeaterPowerWatts = f54f.HasValue ? Decode54F_HeaterPowerWatts(f54f.Value.Span) : null
        };
    }

    // -------------------------
    // 0x54A (setpoint raw)
    // Signals per screenshot:
    // - ClimateControlSetpoint: bitpos 32 len 8 => byte4
    // -------------------------
    static byte Decode54A_SetpointRaw(ReadOnlySpan<byte> d) => d[4];

    // -------------------------
    // 0x54B (fan speed nibble)
    // Signals per screenshot:
    // - FanSpeed: bitpos 36 len 4 => upper nibble of byte4 (bits 4..7)
    // -------------------------
    static int Decode54B_FanSpeed(ReadOnlySpan<byte> d) => (d[4] >> 4) & 0x0F;

    // -------------------------
    // 0x54C (status + temps)
    // Per screenshot:
    // - ACEvaporatorTemperature: bitpos 0 len 8, unit shows 0.25C/bit
    // - CC_BackScreenDefrost: bitpos 9 len 1
    // - CC_ClimateControlStatus: bitpos 10 len 1
    // - CC_ACStatus: bitpos 11 len 1
    // - FanVoltage: bitpos 40 len 8, unit shows 0.05 V/bit
    // - OutsideAmbientTemperature: bitpos 48 len 8 factor 0.5 offset -40
    // -------------------------
    static bool Decode54C_RearDefrostOn(ReadOnlySpan<byte> d) => CanBits.ReadBool(d, 9);
    static bool Decode54C_ClimateControlOn(ReadOnlySpan<byte> d) => CanBits.ReadBool(d, 10);
    static bool Decode54C_AcOn(ReadOnlySpan<byte> d) => CanBits.ReadBool(d, 11);

    static double Decode54C_EvaporatorTempC(ReadOnlySpan<byte> d)
    {
        var raw = CanBits.ReadUnsigned(d, 0, 8);
        return raw * 0.25; // per screenshot unit note
    }

    static double Decode54C_FanVoltageV(ReadOnlySpan<byte> d)
    {
        var raw = CanBits.ReadUnsigned(d, 40, 8);
        return raw * 0.05; // per screenshot unit note
    }

    static double Decode54C_OutsideAmbientTempC(ReadOnlySpan<byte> d)
    {
        var raw = CanBits.ReadUnsigned(d, 48, 8);
        return (raw * 0.5) - 40.0;
    }

    // -------------------------
    // 0x54F (power + interior intake temp)
    // Per screenshot:
    // - InteriorIntakeTemp: bitpos 0 len 8 factor 0.5 offset -14
    // - ACPowerConsumption: bitpos 8 len 8, comment "50W/bit"
    // - HeaterPowerConsumption: bitpos 40 len 6, comment "300W/bit"
    // - ACAutoAmpStatus: bitpos 46 len 2 (not used below but easy to add)
    // -------------------------
    static double Decode54F_InteriorIntakeTempC(ReadOnlySpan<byte> d)
    {
        var raw = CanBits.ReadUnsigned(d, 0, 8);
        return (raw * 0.5) - 14.0;
    }

    static int Decode54F_AcPowerWatts(ReadOnlySpan<byte> d)
    {
        var raw = CanBits.ReadUnsigned(d, 8, 8);
        return (int)raw * 50;
    }

    static int Decode54F_HeaterPowerWatts(ReadOnlySpan<byte> d)
    {
        var raw = CanBits.ReadUnsigned(d, 40, 6);
        return (int)raw * 300;
    }
}

