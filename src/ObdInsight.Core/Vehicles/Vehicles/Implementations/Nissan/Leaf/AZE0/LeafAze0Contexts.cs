using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;

public static class LeafAze0Contexts
{
    public static EcuContext Abs => ReqResp("ABS", "740", "760");

    public static EcuContext AbsBroadcast => new()
    {
        Name = "ABS Broadcast (0x130, 0x245, 0x284, 0x285, 0x292, 0x354)",
        TxHeader = "", // unused in monitoring mode
        RxFilter = "", // unused in monitoring mode
        FlowControlHeader = "", // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept ABS broadcast frames
        // 0x130 (20ms) - ABS status bitmask
        // 0x245 (20ms) - VDC torque control
        // 0x284 (20ms) - Front wheel speeds
        // 0x285 (20ms) - Rear wheel speeds
        // 0x292 (20ms) - Battery voltage and brake pressure
        // 0x354 (20ms) - Vehicle speed pulses and ESP status
        CanFilterMask = "", // No filtering, accept all
        CanFilterPattern = "",
        ExpectedCanIds = ["130", "245", "284", "285", "292", "354"],
        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    public static EcuContext Airbag => ReqResp("AIRBAG", "752", "772");

    public static IReadOnlyList<EcuContext> All { get; } =
    [
        Vcm, Bcm, BcmBroadcast, Abs, AbsBroadcast, LbcBms, InverterMc, Meter, Hvac, Brake, BrakeBroadcast, Vsp, Eps,
        Tcu, MultiAv, IpdmEr, Airbag, Ident, Shift, SteeringBroadcast, Consult3Plus
    ];

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

    public static EcuContext Bcm => ReqResp("BCM", "745", "765");

    public static EcuContext BcmBroadcast => new()
    {
        Name = "BCM Broadcast (0x60D, 0x625)",
        TxHeader = "", // unused in monitoring mode
        RxFilter = "", // unused in monitoring mode
        FlowControlHeader = "", // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept BCM broadcast frames
        // 0x60D (20ms) - Main BCM status (doors, locks, lights)
        // 0x625 (20ms) - Headlight/foglight status
        CanFilterMask = "", // No filtering, accept all
        CanFilterPattern = "",
        ExpectedCanIds = ["60D", "625"],
        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    public static EcuContext Brake => ReqResp("BRAKE", "70E", "70F");

    public static EcuContext BrakeBroadcast => new()
    {
        Name = "BRAKE Broadcast (0x1CA)",
        TxHeader = "", // unused in monitoring mode
        RxFilter = "", // unused in monitoring mode
        FlowControlHeader = "", // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept Brake broadcast frame
        // 0x1CA (20ms) - Brake pressure and regen braking
        CanFilterMask = "", // No filtering, accept all
        CanFilterPattern = "",
        ExpectedCanIds = ["1CA"],
        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    public static IReadOnlyDictionary<string, EcuContext> ByName { get; } =
        All.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

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

    public static EcuContext Eps => ReqResp("EPS", "742", "762");

    public static EcuContext Hvac => ReqResp("HVAC", "744", "764");

    public static EcuContext HvacBroadcast => new()
    {
        Name = "HVAC Broadcast (0x54A-0x54F)",
        TxHeader = "", // unused in monitoring mode
        RxFilter = "", // unused in monitoring mode
        FlowControlHeader = "", // unused in monitoring mode
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

    public static EcuContext Ident => ReqResp("IDENT (Charger)", "797", "79A");

    public static EcuContext InverterMc => ReqResp("INVERTER/MC", "784", "78C");

    public static EcuContext InvMcBroadcast => new()
    {
        Name = "INVmc Broadcast (0x1DA, 0x55A)",
        TxHeader = "", // unused in monitoring mode
        RxFilter = "", // unused in monitoring mode
        FlowControlHeader = "", // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",
        CanFilterMask = "7FF",
        CanFilterPattern = "1DA",

        // Accept Inverter/Motor Controller broadcast frames
        // 0x1DA (10ms) - motor status
        // 0x55A (100ms) - temperature
        // ExpectedCanIds is used to configure AT CRA filters for each ID
        ExpectedCanIds = ["1DA", "55A"],
        EnableHeaders = true,
        EnableAutoFormatting = false // CAF0 required for proper frame parsing
    };

    public static EcuContext IpdmEr => ReqResp("IPDM E/R", "74D", "76D");

    public static EcuContext LbcBms => ReqResp("LBC/BMS", "79B", "7BB");

    public static EcuContext Meter => ReqResp("M&A (Meter)", "743", "763");

    public static EcuContext MultiAv => ReqResp("Multi AV", "747", "767");

    public static EcuContext ObcPdBroadcast => new()
    {
        Name = "OBCpd Broadcast (0x390, 0x393)",
        TxHeader = "", // unused in monitoring mode
        RxFilter = "", // unused in monitoring mode
        FlowControlHeader = "", // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept OBCpd broadcast frames (0x390, 0x393)
        // Using a filter to catch these specific frames
        CanFilterMask = "FF8", // Mask to match 0x390-0x397
        CanFilterPattern = "390",
        ExpectedCanIds = ["390", "393"],
        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    /// <summary>
    ///     Accept-all monitoring context for the shared <c>CanMonitor</c>: one long-lived
    ///     monitoring pass whose frames are demuxed in software to all broadcast capabilities
    ///     (HVAC 0x54A-F, INVmc 0x1DA/0x55A, battery 0x1DB/..., etc.).
    /// </summary>
    public static EcuContext SharedBroadcastMonitor => new()
    {
        Name = "Leaf Shared Broadcast Monitor",
        TxHeader = "", // unused in monitoring mode
        RxFilter = "", // unused in monitoring mode
        FlowControlHeader = "", // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,
        MonitoringCommand = "ATMA",
        CanFilterMask = null, // accept all; software demux in CanMonitor
        CanFilterPattern = null,
        EnableHeaders = true,
        EnableAutoFormatting = false // CAF0 preserves frame bytes for parsing
    };

    /// <summary>
    ///     Hardware-filter rotation for the shared monitor. Accept-all ATMA overruns cheap BLE
    ///     adapters within ~100ms on a busy EV bus (hardware session 2026-07-18), so the monitor
    ///     time-slices the bus with mask 0x700: one window per 0xN00 block that carries frames we
    ///     decode. Full cycle ≈ 3.6s — cache-view data is at most that stale.
    ///     Expected per window (CAR-CAN only — EV-CAN IDs 11A,1CA,1DA,1DB,1DC,55A,59E don't
    ///     appear because stock ELM327 adapters wire OBD pins 6/14 = CAR-CAN; EV-CAN is on pins
    ///     12/13 and needs a modified adapter; hardware-confirmed 2026-07-18):
    ///     0x1xx: 130,174,176,180,1D4 · 0x2xx: 245,284,285,292 (260's signals reach byte 4, so a
    ///     4-byte frame is still short of its MinimumLength — see the note in CarVcmFrames)
    ///     0x3xx: 354,390,393 · 0x4xx: 421 (1-byte gear relay, decodes typed)
    ///     0x5xx: 50A-D,510,54A-F,55B,5A9,5B3,5BC,5C0 · 0x6xx: 603,60D
    /// </summary>
    public static IReadOnlyList<CanFilterWindow> SharedBroadcastRotation { get; } =
    [
        new("700", "100", TimeSpan.FromMilliseconds(600)),
        new("700", "200", TimeSpan.FromMilliseconds(600)),
        new("700", "300", TimeSpan.FromMilliseconds(600)),
        new("700", "400", TimeSpan.FromMilliseconds(600)),
        new("700", "500", TimeSpan.FromMilliseconds(600)),
        new("700", "600", TimeSpan.FromMilliseconds(600))
    ];

    public static EcuContext Shift => ReqResp("SHIFT", "79D", "7BD");

    /// <summary>
    ///     Steering broadcast frames - MAY REQUIRE SESSION ACTIVATION.
    ///     Frames 0x002 (10ms) and 0x300 (20ms) may only appear when steering ECU is awake.
    /// </summary>
    public static EcuContext SteeringBroadcast => new()
    {
        Name = "STEERING Broadcast (0x002, 0x300)",
        TxHeader = "742", // EPS ECU TX address for session activation
        RxFilter = "", // Clear for monitoring
        FlowControlHeader = "742",
        CommunicationMode = EcuCommunicationMode.ActiveMonitoring,

        // Session activation to wake EPS module
        SessionActivationCommand = "1081", // Default session with suppress-positive-response
        RequiresSessionActivation = true,

        // Keep-alive to prevent sleep during monitoring
        KeepAliveCommand = "3E80", // TesterPresent with suppress-positive-response
        KeepAliveIntervalMs = 2000,
        MonitoringCommand = "AT MA",
        ExpectedCanIds = ["002", "300"],
        EnableHeaders = true,
        EnableAutoFormatting = false // CAF0 required for proper frame parsing
    };

    public static EcuContext Tcu => ReqResp("TCU", "746", "783");

    public static EcuContext Vcm => ReqResp("VCM", "797", "79A");

    /// <summary>
    ///     VCM broadcast frames on EV-CAN bus.
    /// </summary>
    public static EcuContext VcmEvCanBroadcast => new()
    {
        Name = "VCM EV-CAN Broadcast (0x11A, 0x1D4, 0x1F2, 0x284, 0x5A9)",
        TxHeader = "", // unused in monitoring mode
        RxFilter = "", // unused in monitoring mode
        FlowControlHeader = "", // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept VCM broadcast frames on EV-CAN (0x11A, 0x1D4, 0x1F2, 0x284, 0x5A9, 0x50A-0x50C, 0x5B9, 0x603)
        // Using a broader filter to catch all VCM frames
        CanFilterMask = "", // No filtering, accept all
        CanFilterPattern = "",
        ExpectedCanIds = ["11A", "1D4", "1F2", "284", "5A9", "50A", "50B", "50C", "5B9", "603"],
        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    /// <summary>
    ///     VCM broadcast frames on CAR-CAN bus.
    /// </summary>
    public static EcuContext VcmCarCanBroadcast => new()
    {
        Name = "VCM CAR-CAN Broadcast (0x174, 0x176, 0x180, 0x260, 0x421, 0x50A, 0x50D, 0x510)",
        TxHeader = "", // unused in monitoring mode
        RxFilter = "", // unused in monitoring mode
        FlowControlHeader = "", // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept VCM broadcast frames on CAR-CAN
        // 0x174 (8 bytes) - Shifter relay data
        // 0x176 (7 bytes) - Motor RPM relay
        // 0x180 (8 bytes) - Motor current and throttle
        // 0x260 (4 bytes) - Motor power consumption
        // 0x421 (1 byte) - Dashboard shifter position
        // 0x50D (8 bytes) - Dashboard indicator lights
        // 0x510 (8 bytes) - Power consumption and climate data
        // Note: 0x50A appears on both EV-CAN and CAR-CAN with same structure
        CanFilterMask = "", // No filtering, accept all
        CanFilterPattern = "",
        ExpectedCanIds = ["174", "176", "180", "260", "421", "50A", "50D", "510"],
        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    /// <summary>
    ///     Alias for VcmEvCanBroadcast to maintain backward compatibility.
    /// </summary>
    public static EcuContext VcmBroadcast => VcmEvCanBroadcast;

    public static EcuContext Vsp => ReqResp("VSP", "73F", "761");

    private static EcuContext ReqResp(string name, string tx, string rx) => new()
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
}
