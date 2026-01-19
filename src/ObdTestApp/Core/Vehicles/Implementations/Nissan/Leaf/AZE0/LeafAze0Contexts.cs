using ObdTestApp.Core.Protocols;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;

public static class LeafAze0Contexts
{
    public static EcuContext Abs => ReqResp("ABS", "740", "760");

    public static EcuContext AbsBroadcast => new()
    {
        Name = "ABS Broadcast (0x130, 0x245, 0x284, 0x285, 0x292, 0x354)",
        TxHeader = "000",            // unused in monitoring mode
        RxFilter = "000",            // unused in monitoring mode
        FlowControlHeader = "000",   // unused in monitoring mode
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
        CanFilterMask = "000",  // No filtering, accept all
        CanFilterPattern = "000",

        ExpectedCanIds = ["130", "245", "284", "285", "292", "354"],

        EnableHeaders = true,
        EnableAutoFormatting = true
    };


    public static EcuContext Airbag => ReqResp("AIRBAG", "752", "772");

    public static IReadOnlyList<EcuContext> All { get; } =
    [
        Vcm, Bcm, BcmBroadcast, Abs, AbsBroadcast, LbcBms, InverterMc, Meter, Hvac, Brake, BrakeBroadcast, Vsp, Eps, Tcu, MultiAv, IpdmEr, Airbag, Ident, Shift, Consult3Plus
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
        TxHeader = "000",            // unused in monitoring mode
        RxFilter = "000",            // unused in monitoring mode
        FlowControlHeader = "000",   // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept BCM broadcast frames
        // 0x60D (20ms) - Main BCM status (doors, locks, lights)
        // 0x625 (20ms) - Headlight/foglight status
        CanFilterMask = "000",  // No filtering, accept all
        CanFilterPattern = "000",

        ExpectedCanIds = ["60D", "625"],

        EnableHeaders = true,
        EnableAutoFormatting = true
    };


    public static EcuContext Brake => ReqResp("BRAKE", "70E", "70F");

    public static EcuContext BrakeBroadcast => new()
    {
        Name = "BRAKE Broadcast (0x1CA)",
        TxHeader = "000",            // unused in monitoring mode
        RxFilter = "000",            // unused in monitoring mode
        FlowControlHeader = "000",   // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept Brake broadcast frame
        // 0x1CA (20ms) - Brake pressure and regen braking
        CanFilterMask = "000",  // No filtering, accept all
        CanFilterPattern = "000",

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

    public static EcuContext Ident => ReqResp("IDENT", "792", "793");

    public static EcuContext InverterMc => ReqResp("INVERTER/MC", "784", "78C");

    public static EcuContext InvMcBroadcast => new()
    {
        Name = "INVmc Broadcast (0x1DA, 0x55A)",
        TxHeader = "000",            // unused in monitoring mode
        RxFilter = "000",            // unused in monitoring mode
        FlowControlHeader = "000",   // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept Inverter/Motor Controller broadcast frames
        // 0x1DA (10ms) - motor status
        // 0x55A (100ms) - temperature
        CanFilterMask = "000",  // No filtering, accept all
        CanFilterPattern = "000",

        ExpectedCanIds = ["1DA", "55A"],

        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    public static EcuContext IpdmEr => ReqResp("IPDM E/R", "74D", "76D");

    public static EcuContext LbcBms => ReqResp("LBC/BMS", "79B", "7BB");

    public static EcuContext Meter => ReqResp("M&A (Meter)", "743", "763");

    public static EcuContext MultiAv => ReqResp("Multi AV", "747", "767");

    public static EcuContext ObcPdBroadcast => new()
    {
        Name = "OBCpd Broadcast (0x390, 0x393)",
        TxHeader = "000",            // unused in monitoring mode
        RxFilter = "000",            // unused in monitoring mode
        FlowControlHeader = "000",   // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept OBCpd broadcast frames (0x390, 0x393)
        // Using a filter to catch these specific frames
        CanFilterMask = "FF8",  // Mask to match 0x390-0x397
        CanFilterPattern = "390",

        ExpectedCanIds = ["390", "393"],

        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    public static EcuContext Shift => ReqResp("SHIFT", "79D", "7BD");

    public static EcuContext Tcu => ReqResp("TCU", "746", "783");

    public static EcuContext Vcm => ReqResp("VCM", "797", "79A");

    public static EcuContext VcmBroadcast => new()
    {
        Name = "VCM Broadcast (0x11A, 0x1D4, 0x1F2, 0x284, 0x5A9)",
        TxHeader = "000",            // unused in monitoring mode
        RxFilter = "000",            // unused in monitoring mode
        FlowControlHeader = "000",   // unused in monitoring mode
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,

        // Monitor all standard CAN frames
        MonitoringCommand = "AT MA",

        // Accept VCM broadcast frames (0x11A, 0x1D4, 0x1F2, 0x284, 0x5A9, 0x50A-0x50C, 0x5B9, 0x603)
        // Using a broader filter to catch all VCM frames
        CanFilterMask = "000",  // No filtering, accept all
        CanFilterPattern = "000",

        ExpectedCanIds = ["11A", "1D4", "1F2", "284", "5A9", "50A", "50B", "50C", "5B9", "603"],

        EnableHeaders = true,
        EnableAutoFormatting = true
    };

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
