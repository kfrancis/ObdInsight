// ReSharper disable All

namespace ObdInsight.Core.Protocols;

/// <summary>
///     Represents the configuration required to communicate with a specific ECU.
///     Encapsulates CAN headers, filters, and flow control settings for different vehicle control units.
/// </summary>
public sealed class EcuContext
{
    /// <summary>
    ///     Gets the descriptive name of this ECU context.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Gets the CAN transmit header (AT SH command value).
    /// </summary>
    public required string TxHeader { get; init; }

    /// <summary>
    ///     Gets the CAN receive filter (AT CRA command value).
    /// </summary>
    public string? RxFilter { get; init; }

    /// <summary>
    ///     Gets the ISO-TP flow control header (AT FC SH command value).
    /// </summary>
    public string? FlowControlHeader { get; init; }

    /// <summary>
    ///     Gets the ISO-TP flow control data (AT FC SD command value).
    ///     Default: "300000" (CTS=Continue To Send, BS=0, STmin=0).
    /// </summary>
    public string FlowControlData { get; init; } = "300000";

    /// <summary>
    ///     Gets the ISO-TP flow control mode (AT FC SM command value).
    ///     Default: "1" (auto-respond to flow control).
    /// </summary>
    public string FlowControlMode { get; init; } = "1";

    /// <summary>
    ///     Gets whether CAN headers should be enabled (AT H1/H0).
    /// </summary>
    public bool EnableHeaders { get; init; } = true;

    /// <summary>
    ///     Gets whether CAN auto-formatting should be enabled (AT CAF1/CAF0).
    /// </summary>
    public bool EnableAutoFormatting { get; init; } = true;

    /// <summary>
    ///     Gets the communication mode required for this ECU.
    /// </summary>
    public EcuCommunicationMode CommunicationMode { get; init; } = EcuCommunicationMode.RequestResponse;

    /// <summary>
    ///     Gets the monitoring command to use when entering passive monitoring mode.
    ///     Only applicable when CommunicationMode is PassiveMonitoring.
    /// </summary>
    /// <remarks>
    ///     Examples: "AT MA" (monitor all), "AT MR 7BB" (monitor receiver 0x7BB)
    /// </remarks>
    public string? MonitoringCommand { get; init; }

    /// <summary>
    ///     Gets the list of CAN IDs to expect when monitoring this ECU.
    ///     Used for filtering and validation in passive monitoring mode.
    /// </summary>
    public string[]? ExpectedCanIds { get; init; }

    /// <summary>
    ///     Gets the CAN filter mask to use for passive monitoring.
    ///     When set, configures the ELM327 to only receive frames matching the filter.
    ///     This prevents buffer overflow by reducing the number of frames received.
    /// </summary>
    /// <remarks>
    ///     Format: "AT CF XXX" where XXX is the 3-digit hex CAN ID filter.
    ///     Leave null to monitor all frames (AT AR - accept all CAN frames).
    ///     For multiple specific IDs, use the mask/pattern approach:
    ///     - AT CM FFF (mask all bits)
    ///     - AT CF XXX (filter for specific ID)
    ///     For a range, use:
    ///     - AT CM FF0 (mask upper 8 bits, ignore lower 4 bits)
    ///     - AT CF 1D0 (accepts 0x1D0-0x1DF)
    /// </remarks>
    public string? CanFilterMask { get; init; }

    /// <summary>
    ///     Gets the CAN filter pattern to use for passive monitoring.
    ///     Only frames matching (CAN_ID &amp; CanFilterMask) == CanFilterPattern will be received.
    /// </summary>
    public string? CanFilterPattern { get; init; }

    /// <summary>
    ///     Timeout value for ATST command in units of 4ms.
    ///     Default 32 = 128ms. Working app uses aggressive values like 8 = 32ms for probing.
    /// </summary>
    public int AdapterTimeoutUnits { get; init; } = 32;

    /// <summary>
    ///     Session activation command (e.g., "10C0" for Nissan OEM session, "1081" for default+suppress).
    ///     Sent before diagnostic queries or monitoring if module requires session activation.
    /// </summary>
    /// <remarks>
    ///     Common values:
    ///     - "1001" = Default session
    ///     - "1081" = Default session with suppress-positive-response bit
    ///     - "10C0" = Nissan OEM-specific session
    ///     - "1003" = Extended diagnostic session
    /// </remarks>
    public string? SessionActivationCommand { get; init; }

    /// <summary>
    ///     Keep-alive command to prevent module sleep during extended monitoring.
    ///     Typically a TesterPresent command ("3E00" or "3E80").
    /// </summary>
    public string? KeepAliveCommand { get; init; }

    /// <summary>
    ///     Keep-alive interval in milliseconds. Default 2000ms (2 seconds).
    ///     Most ECUs require keep-alive within 5 seconds to prevent sleep.
    /// </summary>
    public int KeepAliveIntervalMs { get; init; } = 2000;

    /// <summary>
    ///     Whether this ECU requires session activation before data is available.
    ///     If true, session will be activated before monitoring or first query.
    /// </summary>
    public bool RequiresSessionActivation { get; init; }


    /// <summary>
    ///     Nissan Leaf BMS (Battery Management System) - Mode 21 queries.
    /// </summary>
    /// <remarks>
    ///     Used for querying battery state (SOC, voltage, current, temperature, health).
    ///     TX: 0x79B, RX: 0x7BB
    /// </remarks>
    public static EcuContext NissanLeafBms => new()
    {
        Name = "Nissan Leaf BMS",
        TxHeader = "79B",
        RxFilter = "7BB",
        FlowControlHeader = "79B",
        FlowControlData = "300000",
        FlowControlMode = "1",
        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    /// <summary>
    ///     Nissan Leaf Charger - Mode 21 queries.
    /// </summary>
    /// <remarks>
    ///     Used for querying charger information (VIN, charging status, etc.).
    ///     TX: 0x797, RX: 0x79A
    /// </remarks>
    public static EcuContext NissanLeafCharger => new()
    {
        Name = "Nissan Leaf Charger",
        TxHeader = "797",
        RxFilter = "79A",
        FlowControlHeader = "797",
        FlowControlData = "300000",
        FlowControlMode = "1",
        EnableHeaders = true,
        EnableAutoFormatting = true
    };

    /// <summary>
    ///     Standard OBD-II broadcast (Mode 01/09 queries to any ECU).
    /// </summary>
    /// <remarks>
    ///     Used for standard OBD-II queries. Most Nissan Leaf ECUs don't respond to standard queries.
    ///     TX: 0x7DF (broadcast), RX: 0x7E8 (primary ECU response)
    /// </remarks>
    public static EcuContext StandardObdBroadcast => new()
    {
        Name = "OBD-II Broadcast",
        TxHeader = "7DF",
        RxFilter = "7E8",
        FlowControlHeader = "7DF",
        FlowControlData = "300000",
        FlowControlMode = "1",
        EnableHeaders = false,
        EnableAutoFormatting = false
    };

    /// <summary>
    ///     Nissan Leaf VCM (Vehicle Control Module) wakeup context.
    /// </summary>
    /// <remarks>
    ///     Used for waking up sleeping ECUs. TX: 0x679
    /// </remarks>
    public static EcuContext NissanLeafVcmWakeup => new()
    {
        Name = "Nissan Leaf VCM Wakeup",
        TxHeader = "679",
        RxFilter = "679", // No response expected
        FlowControlHeader = "679",
        EnableHeaders = true,
        EnableAutoFormatting = false
    };

    /// <summary>
    ///     Nissan Leaf Battery Heater wakeup context.
    /// </summary>
    /// <remarks>
    ///     Used for waking up sleeping ECUs. TX: 0x5C0
    /// </remarks>
    public static EcuContext NissanLeafBatteryHeaterWakeup => new()
    {
        Name = "Nissan Leaf Battery Heater Wakeup",
        TxHeader = "5C0",
        RxFilter = "5C0", // No response expected
        FlowControlHeader = "5C0",
        EnableHeaders = true,
        EnableAutoFormatting = false
    };

    /// <summary>
    ///     Nissan Leaf HVBAT - Passive monitoring of EV-CAN broadcast frames.
    /// </summary>
    /// <remarks>
    ///     Monitors passive broadcast frames for real-time battery data without sending queries.
    ///     Data is automatically broadcast by the car when in READY mode or charging.
    ///     Key broadcast frames:
    ///     - 0x1DB (LB_STATUS): Current, voltage, usable SOC
    ///     - 0x1DC (LB_LIMITS): Discharge/charge power limits
    ///     - 0x55B (LB_SOC): High-resolution SOC (0.1% precision)
    ///     - 0x5BC (LB_GIDS): GIDs (remaining capacity), SOH, charge time
    ///     - 0x5C0 (LB_TEMPS): Battery temperatures, heater status
    ///     - 0x59E (QC_CAPACITY): Full/remaining capacity for Quick Charge (Wh)
    ///     - 0x1DA (INVERTER): Motor voltage, torque, RPM
    ///     This is distinct from BMS Mode 21 queries - no requests are sent.
    ///     CAN Filtering Strategy:
    ///     - Currently set to null (AT AR - accept all frames) for diagnostics
    ///     - This allows seeing all CAN traffic to understand what's on the bus
    ///     - If buffer overflow occurs, enable hardware filtering with specific mask/pattern
    ///     - Software filtering in MonitorFramesAsync will only yield expected IDs
    /// </remarks>
    public static EcuContext NissanLeafHvbatMonitor => new()
    {
        Name = "Nissan Leaf HVBAT Monitor",
        TxHeader = "000", // No TX in monitoring mode
        RxFilter = "000", // Monitor all
        FlowControlHeader = "000",
        EnableHeaders = true,
        EnableAutoFormatting = false, // CAF0 for monitoring - preserves spaces between bytes
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,
        MonitoringCommand = "ATMA", // Monitor all CAN traffic
        ExpectedCanIds = ["1DB", "1DC", "55B", "5BC", "5C0", "59E", "1DA", "5A9"],
        CanFilterMask = null, // Disable hardware filter to see all traffic
        CanFilterPattern = null // Software filtering based on ExpectedCanIds
    };

    /// <summary>
    ///     Nissan Leaf Inverter - Passive monitoring of motor/inverter data.
    /// </summary>
    /// <remarks>
    ///     Monitors passive broadcast frames for real-time motor data.
    ///     Key broadcast frames:
    ///     - 0x1DA (INVERTER): Motor voltage, torque, RPM
    ///     - 0x55A: Additional motor data
    ///     CAN Filtering Strategy:
    ///     - Uses mask 0x700 with pattern 0x100 to accept 0x100-0x1FF and 0x500-0x5FF
    ///     - This covers both inverter frames without risk of buffer overflow
    /// </remarks>
    public static EcuContext NissanLeafInverterMonitor => new()
    {
        Name = "Nissan Leaf Inverter Monitor",
        TxHeader = "000",
        RxFilter = "000",
        FlowControlHeader = "000",
        EnableHeaders = true,
        EnableAutoFormatting = false, // CAF0 for monitoring - preserves spaces between bytes
        CommunicationMode = EcuCommunicationMode.PassiveMonitoring,
        MonitoringCommand = "ATMA",
        ExpectedCanIds = ["1DA", "55A"],
        CanFilterMask = "700",
        CanFilterPattern = "100"
    };

    public void Validate()
    {
        if (CommunicationMode == EcuCommunicationMode.RequestResponse)
        {
            if (string.IsNullOrWhiteSpace(RxFilter)) throw new InvalidOperationException($"{Name}: RxFilter required.");
            if (string.IsNullOrWhiteSpace(FlowControlHeader))
                throw new InvalidOperationException($"{Name}: FlowControlHeader required.");
        }
    }
}
