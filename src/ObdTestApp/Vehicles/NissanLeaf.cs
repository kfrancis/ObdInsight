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
        var response = string.Join("\r", lines);

        // Parse Nissan-specific response format
        var nissanData = ParseNissanGroup01(response);

        // Map to generic BatteryStatus
        return new BatteryStatus
        {
            SocPercent = nissanData.SocPercent,
            VoltageVolts = nissanData.VoltageVolts,
            CurrentAmps = nissanData.CurrentAmps,
            CapacityAh = nissanData.CapacityAh,
            HealthPercent = nissanData.HxPercent,
            // Nissan Group 01 doesn't include temp, would need Group 03/04
            TemperatureC = null
        };
    }

    public async ValueTask<CellVoltageData?> GetCellVoltagesAsync(CancellationToken ct = default)
    {
        // Nissan-specific: Query Mode 21 PID 02
        var lines = await _session.QueryAsync("2102", _context, ct);
        var response = string.Join("\n", lines);

        var nissanData = ParseNissanGroup02(response);

        if (nissanData == null)
            return null;

        // Map to generic CellVoltageData
        return new CellVoltageData
        {
            CellVoltagesMv = nissanData.Value.CellVoltages,
            MinVoltageMv = nissanData.Value.MinVoltage,
            MaxVoltageMv = nissanData.Value.MaxVoltage,
            AvgVoltageMv = nissanData.Value.AvgVoltage
        };
    }

    // Nissan-specific parsing (internal implementation details)
    private static (double? SocPercent, double? VoltageVolts, double? CurrentAmps,
                    double? CapacityAh, double? HxPercent) ParseNissanGroup01(string response)
    {
        // Your existing parsing logic from Program.cs
        // Returns Nissan-specific data structure

        return (null, null, null, null, null); // placeholder
    }

    private static (int[] CellVoltages, int MinVoltage, int MaxVoltage, int AvgVoltage)?
        ParseNissanGroup02(string response)
    {
        // Your existing parsing logic from Program.cs

        return null; // placeholder
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

