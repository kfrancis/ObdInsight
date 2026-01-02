using ObdInsight.Core.Adapters;
using ObdInsight.Core.Transports.Ble;
using System.Diagnostics;

namespace ObdInsight.Core.Diagnostics;

/// <summary>
/// Collects comprehensive diagnostic data from a vehicle/adapter for generating support reports.
/// Platform-agnostic implementation - BLE discovery must be provided by the caller.
/// </summary>
public class DiagnosticDataCollector
{
    /// <summary>
    /// AT commands to probe for adapter info
    /// </summary>
    private static readonly (string Command, string Description)[] AtCommands =
    [
        ("ATZ", "Reset adapter"),
        ("ATI", "Adapter version"),
        ("AT@1", "Device description"),
        ("AT@2", "Device identifier"),
        ("ATRV", "Voltage reading"),
        ("ATDP", "Describe protocol"),
        ("ATDPN", "Describe protocol by number"),
    ];

    /// <summary>
    /// Extended PIDs for EV/Hybrid detection
    /// </summary>
    private static readonly (string Command, string Description)[] EvProbePids =
    [
        ("015B", "Hybrid battery pack remaining life"),
        ("015E", "Engine fuel rate (0 = EV)"),
        ("2101", "Manufacturer-specific battery data (Nissan/Hyundai)"),
        ("2102", "Manufacturer-specific battery data 2"),
        ("2103", "Manufacturer-specific battery data 3"),
        ("2104", "Manufacturer-specific battery data 4"),
        ("2105", "Manufacturer-specific battery data 5"),
        ("220101", "Manufacturer-specific battery data (GM/Kia)"),
        ("220102", "Manufacturer-specific battery data (GM/Kia) 2"),
        ("220105", "Manufacturer-specific battery data (GM/Kia) 3"),
    ];

    /// <summary>
    /// Generic EV CAN probes for unknown vehicles
    /// </summary>
    private static readonly (string Command, string Description)[] GenericEvCanProbes =
    [
        // Common BMS addresses
        ("ATSH7E4", "Set header to common BMS address 1"),
        ("2101", "BMS data request"),
        ("ATSH7E5", "Set header to common BMS address 2"),
        ("2101", "BMS data request"),
        ("ATSH7BB", "Set header to Nissan/Renault BMS"),
        ("2101", "BMS data request"),
        ("ATSH7C0", "Set header to common VCU address"),
        ("2101", "VCU data request"),
        ("ATSH7DF", "Reset to broadcast header"),
    ];

    /// <summary>
    /// Mode 09 PIDs to probe
    /// </summary>
    private static readonly (string Command, string Description)[] Mode09Pids =
    [
        ("0900", "Supported PIDs [01-20]"),
        ("0902", "Vehicle Identification Number (VIN)"),
        ("0904", "Calibration ID"),
        ("0906", "Calibration Verification Numbers"),
        ("090A", "ECU name"),
        ("090B", "In-use performance tracking"),
    ];

    /// <summary>
    /// Nissan Leaf-specific BMS and charger probes based on OVMS project.
    /// Uses CAN IDs 0x79B (BMS TX) -> 0x7BB (BMS RX) and 0x797 (Charger TX) -> 0x79A (Charger RX).
    /// Mode 21 = OBDII Group request (manufacturer specific)
    /// Mode 22 = OBDII Extended PID request
    /// </summary>
    private static readonly (string Command, string Description)[] NissanLeafProbes =
    [
        // === SETUP PHASE ===
        // Set protocol to ISO 15765-4 CAN (11-bit, 500k) - this is what the Leaf uses
        ("ATSP6", "Set protocol to CAN 11-bit 500k"),

        // Enable headers in response so we can see which ECU responds
        ("ATH1", "Enable headers in response"),

        // Disable CAN auto-formatting to get raw data
        ("ATCAF0", "Disable CAN auto-formatting"),

        // Set flow control for multi-frame ISO-TP responses
        // The BMS sends multi-frame responses that need flow control
        ("ATFCSH79B", "Set flow control header to BMS"),
        ("ATFCSD300000", "Set flow control data (CTS, block size 0, delay 0)"),
        ("ATFCSM1", "Enable flow control mode 1"),

        // === BMS COMMUNICATION (TX: 79B -> RX: 7BB) ===
        ("ATSH79B", "Set TX header to BMS (79B)"),
        ("ATCRA7BB", "Set RX filter to BMS response (7BB)"),

        // BMS Group 01 - Battery capacity, SOC, HX, current, voltage
        // Response: 39 bytes (ZE0/AZE0) or 51 bytes (ZE1)
        ("2101", "BMS Group 01: Battery SOC, capacity, current, voltage"),

        // BMS Group 02 - All 96 cell voltages (196 bytes)
        ("2102", "BMS Group 02: Cell voltages (96 cells)"),

        // BMS Group 04 - Temperature sensors (14-29 bytes)
        ("2104", "BMS Group 04: Pack temperatures"),

        // BMS Group 06 - Cell balancing shunts (24 bytes) - only useful when charging
        ("2106", "BMS Group 06: Cell balancing status"),

        // BMS Group 61 - SOH for ZE1 Leafs (329 bytes) - ZE1 only
        ("2161", "BMS Group 61: SOH (ZE1 models only)"),

        // === CHARGER/VCM COMMUNICATION (TX: 797 -> RX: 79A) ===
        ("ATFCSH797", "Set flow control header to Charger"),
        ("ATSH797", "Set TX header to Charger (797)"),
        ("ATCRA79A", "Set RX filter to Charger response (79A)"),

        // Charger Group 81 - VIN (19 bytes)
        ("2181", "Charger Group 81: VIN"),

        // Extended PID 1203 - Quick charge (CHAdeMO) count
        ("221203", "Extended PID 1203: Quick charge count"),

        // Extended PID 1205 - L0/L1/L2 AC charge count
        ("221205", "Extended PID 1205: AC charge count"),

        // === CLEANUP ===
        ("ATCRA", "Clear RX filter"),
        ("ATH0", "Disable headers"),
        ("ATCAF1", "Re-enable CAN auto-formatting"),
        ("ATFCSM0", "Disable flow control mode"),
        ("ATSH7DF", "Reset to broadcast header"),
        ("ATD", "Reset to defaults"),
    ];

    /// <summary>
    /// OBD protocols to try, in order of preference
    /// </summary>
    private static readonly (string SetCommand, string Name, string Description)[] OdbProtocols =
    [
        ("ATSP0", "AUTO", "Automatic protocol detection"),
        ("ATSP6", "ISO 15765-4 CAN (11-bit, 500kbaud)", "CAN 11-bit 500k"),
        ("ATSP7", "ISO 15765-4 CAN (29-bit, 500kbaud)", "CAN 29-bit 500k"),
        ("ATSP8", "ISO 15765-4 CAN (11-bit, 250kbaud)", "CAN 11-bit 250k"),
        ("ATSP9", "ISO 15765-4 CAN (29-bit, 250kbaud)", "CAN 29-bit 250k"),
        ("ATSPB", "User1 CAN (11-bit, user baud)", "User CAN 11-bit"),
        ("ATSPC", "User2 CAN (29-bit, user baud)", "User CAN 29-bit"),
        ("ATSP5", "ISO 14230-4 KWP (fast init)", "KWP Fast"),
        ("ATSP4", "ISO 14230-4 KWP (5-baud init)", "KWP 5-baud"),
        ("ATSP3", "ISO 9141-2", "ISO 9141"),
    ];

    /// <summary>
    /// Standard Mode 01 PIDs to probe
    /// </summary>
    private static readonly (string Command, string Description)[] StandardMode01Pids =
    [
        ("0100", "Supported PIDs [01-20]"),
        ("0101", "Monitor status since DTCs cleared"),
        ("0103", "Fuel system status"),
        ("0104", "Calculated engine load"),
        ("0105", "Engine coolant temperature"),
        ("0106", "Short term fuel trim—Bank 1"),
        ("0107", "Long term fuel trim—Bank 1"),
        ("010A", "Fuel pressure"),
        ("010B", "Intake manifold absolute pressure"),
        ("010C", "Engine speed (RPM)"),
        ("010D", "Vehicle speed"),
        ("010E", "Timing advance"),
        ("010F", "Intake air temperature"),
        ("0110", "Mass air flow sensor"),
        ("0111", "Throttle position"),
        ("0113", "Oxygen sensors present (2 banks)"),
        ("011C", "OBD standards this vehicle conforms to"),
        ("011F", "Run time since engine start"),
        ("0120", "Supported PIDs [21-40]"),
        ("0121", "Distance traveled with MIL on"),
        ("012F", "Fuel tank level input"),
        ("0131", "Distance traveled since codes cleared"),
        ("0133", "Absolute Barometric Pressure"),
        ("0140", "Supported PIDs [41-60]"),
        ("0142", "Control module voltage"),
        ("0145", "Relative throttle position"),
        ("0146", "Ambient air temperature"),
        ("0149", "Accelerator pedal position D"),
        ("014A", "Accelerator pedal position E"),
        ("014C", "Commanded throttle actuator"),
        ("0151", "Fuel Type"),
        ("015B", "Hybrid battery pack remaining life"),
        ("015C", "Engine oil temperature"),
        ("015E", "Engine fuel rate"),
        ("0160", "Supported PIDs [61-80]"),
        ("0161", "Driver's demand engine - percent torque"),
        ("0162", "Actual engine - percent torque"),
        ("0163", "Engine reference torque"),
        ("0166", "Mass air flow sensor B"),
        ("0167", "Engine coolant temperature sensor 2"),
    ];

    private readonly List<CanProbeResult> _canProbeResults = [];
    private readonly List<DiagnosticError> _errors = [];
    private readonly List<PidProbeResult> _extendedPidResults = [];
    private readonly List<string> _notes = [];
    private readonly List<ProtocolProbeResult> _protocolProbeResults = [];
    private readonly List<PidProbeResult> _standardPidResults = [];
    private int _currentPhaseIndex;
    private IProgress<DiagnosticProgress>? _progress;
    private int _totalPhases;

    /// <summary>
    /// Adds an error to the collection
    /// </summary>
    public void AddError(string phase, string message, string? details = null)
    {
        _errors.Add(new DiagnosticError
        {
            Phase = phase,
            Message = message,
            Details = details
        });
    }

    /// <summary>
    /// Adds a note to the collection
    /// </summary>
    public void AddNote(string note)
    {
        _notes.Add(note);
    }

    /// <summary>
    /// Builds the final diagnostic report
    /// </summary>
    public DiagnosticReport BuildReport(
        UserVehicleInfo userInfo,
        BleAdapterInfo? bleInfo,
        ObdAdapterInfo? obdInfo,
        VehicleIdentification? vehicleId,
        SupportedPidsInfo? supportedPids)
    {
        return new DiagnosticReport
        {
            GeneratedAt = DateTime.UtcNow,
            ToolVersion = GetToolVersion(),
            UserVehicleInfo = userInfo,
            BleAdapterInfo = bleInfo,
            ObdAdapterInfo = obdInfo,
            VehicleId = vehicleId,
            SupportedPids = supportedPids,
            StandardPidResults = _standardPidResults.ToList(),
            ExtendedPidResults = _extendedPidResults.ToList(),
            ProtocolProbeResults = _protocolProbeResults.ToList(),
            CanProbeResults = _canProbeResults.ToList(),
            Errors = _errors.ToList(),
            Notes = _notes.ToList()
        };
    }

    /// <summary>
    /// Collects OBD adapter information (ELM327 version, etc.)
    /// </summary>
    public async Task<ObdAdapterInfo> CollectObdAdapterInfoAsync(
        IObdAdapter adapter,
        IProgress<DiagnosticProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _progress = progress;
        _currentPhaseIndex = 0;
        _totalPhases = 1;

        ReportProgress(DiagnosticPhase.AdapterInfo, "Probing OBD adapter...", 0, 0, AtCommands.Length);

        var rawResponses = new Dictionary<string, string>();
        string? resetResponse = null;
        string? versionResponse = null;
        string? deviceDesc = null;
        string? voltage = null;
        string? protocol = null;
        string? protocolNum = null;

        for (var i = 0; i < AtCommands.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (command, description) = AtCommands[i];

            ReportProgress(
                DiagnosticPhase.AdapterInfo,
                $"Sending {command} ({description})...",
                (double)i / AtCommands.Length,
                i,
                AtCommands.Length,
                command);

            try
            {
                var response = await adapter.SendCommandAsync(
                    new ObdCommand(command, TimeSpan.FromSeconds(5)),
                    cancellationToken);

                var rawValue = response.RawResponse ?? response.Value ?? "";
                rawResponses[command] = rawValue;

                // Check for transport disconnection error
                if (response.Error == "Transport not connected")
                {
                    AddError("Adapter Info", $"Transport disconnected during {command}");
                    ReportProgress(
                        DiagnosticPhase.AdapterInfo,
                        $"{command}: DISCONNECTED",
                        (double)(i + 1) / AtCommands.Length,
                        i + 1,
                        AtCommands.Length,
                        command,
                        "Transport not connected",
                        false);
                    // Break out - reconnection will be handled by caller
                    break;
                }

                ReportProgress(
                    DiagnosticPhase.AdapterInfo,
                    $"{command}: {TruncateResponse(rawValue)}",
                    (double)(i + 1) / AtCommands.Length,
                    i + 1,
                    AtCommands.Length,
                    command,
                    rawValue,
                    response.Success);

                switch (command)
                {
                    case "ATZ":
                        resetResponse = rawValue;
                        break;

                    case "ATI":
                        versionResponse = rawValue;
                        break;

                    case "AT@1":
                        deviceDesc = rawValue;
                        break;

                    case "ATRV":
                        voltage = rawValue;
                        break;

                    case "ATDP":
                        protocol = rawValue;
                        break;

                    case "ATDPN":
                        protocolNum = rawValue;
                        break;
                }

                // Longer delay between commands to keep BLE connection stable
                await Task.Delay(300, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                rawResponses[command] = $"ERROR: {ex.Message}";
                AddError("AT Command", $"Failed to send {command}: {ex.Message}");

                ReportProgress(
                    DiagnosticPhase.AdapterInfo,
                    $"{command}: ERROR - {ex.Message}",
                    (double)(i + 1) / AtCommands.Length,
                    i + 1,
                    AtCommands.Length,
                    command,
                    ex.Message,
                    false);

                // Break on error - let caller handle reconnection
                break;
            }
        }

        AddNote($"OBD adapter identified as: {versionResponse?.Trim() ?? "Unknown"}");

        return new ObdAdapterInfo
        {
            ResetResponse = resetResponse,
            VersionResponse = versionResponse,
            DeviceDescription = deviceDesc,
            VoltageResponse = voltage,
            ProtocolDescription = protocol,
            ProtocolNumber = protocolNum,
            RawAtResponses = rawResponses
        };
    }

    /// <summary>
    /// Collects supported PIDs information
    /// </summary>
    public async Task<SupportedPidsInfo> CollectSupportedPidsAsync(
        IObdAdapter adapter,
        IProgress<DiagnosticProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _progress = progress;
        ReportProgress(DiagnosticPhase.SupportedPids, "Querying supported PIDs...", 0, 0, 8);

        var mode01Pids = new List<string>();
        var mode09Pids = new List<string>();
        var rawResponses = new Dictionary<string, string>();

        var pidQueries = new[] { "0100", "0120", "0140", "0160", "0180", "01A0", "01C0" };
        var queryIndex = 0;

        // Mode 01 supported PIDs
        foreach (var pidQuery in pidQueries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            ReportProgress(
                DiagnosticPhase.SupportedPids,
                $"Querying Mode 01 PIDs ({pidQuery})...",
                (double)queryIndex / 8,
                queryIndex,
                8,
                pidQuery);

            try
            {
                var response = await adapter.SendCommandAsync(
                    new ObdCommand(pidQuery, TimeSpan.FromSeconds(5)),
                    cancellationToken);

                rawResponses[pidQuery] = response.RawResponse ?? response.Value ?? "";

                if (response.Success && !string.IsNullOrEmpty(response.Value))
                {
                    var pids = ParseSupportedPidsBitmap(response.Value, pidQuery).ToList();
                    mode01Pids.AddRange(pids);

                    ReportProgress(
                        DiagnosticPhase.SupportedPids,
                        $"{pidQuery}: Found {pids.Count} PIDs",
                        (double)(queryIndex + 1) / 8,
                        queryIndex + 1,
                        8,
                        pidQuery,
                        $"{pids.Count} PIDs",
                        true);

                    // If this range doesn't include the next range query, stop
                    var nextQuery = $"01{(byte.Parse(pidQuery[2..], System.Globalization.NumberStyles.HexNumber) + 0x20):X2}";
                    if (!pids.Contains(nextQuery))
                        break;
                }
                else
                {
                    ReportProgress(
                        DiagnosticPhase.SupportedPids,
                        $"{pidQuery}: No response",
                        (double)(queryIndex + 1) / 8,
                        queryIndex + 1,
                        8,
                        pidQuery,
                        response.RawResponse,
                        false);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AddError("PID Query", $"Failed {pidQuery}: {ex.Message}");
                ReportProgress(
                    DiagnosticPhase.SupportedPids,
                    $"{pidQuery}: ERROR - {ex.Message}",
                    (double)(queryIndex + 1) / 8,
                    queryIndex + 1,
                    8,
                    pidQuery,
                    ex.Message,
                    false);
                break;
            }

            queryIndex++;
        }

        // Mode 09 supported PIDs
        ReportProgress(DiagnosticPhase.SupportedPids, "Querying Mode 09 PIDs (0900)...", 0.875, 7, 8, "0900");
        try
        {
            var response = await adapter.SendCommandAsync(
                new ObdCommand("0900", TimeSpan.FromSeconds(5)),
                cancellationToken);

            rawResponses["0900"] = response.RawResponse ?? response.Value ?? "";

            if (response.Success && !string.IsNullOrEmpty(response.Value))
            {
                var pids = ParseSupportedPidsBitmap(response.Value, "0900").ToList();
                mode09Pids.AddRange(pids);
                ReportProgress(
                    DiagnosticPhase.SupportedPids,
                    $"0900: Found {pids.Count} Mode 09 PIDs",
                    1.0,
                    8,
                    8,
                    "0900",
                    $"{pids.Count} PIDs",
                    true);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Optional
        }

        AddNote($"Found {mode01Pids.Count} Mode 01 PIDs and {mode09Pids.Count} Mode 09 PIDs supported");

        return new SupportedPidsInfo
        {
            Mode01Pids = mode01Pids,
            Mode09Pids = mode09Pids,
            RawResponses = rawResponses
        };
    }

    /// <summary>
    /// Collects vehicle identification (VIN, calibration, ECU name)
    /// </summary>
    public async Task<VehicleIdentification> CollectVehicleIdAsync(
        IObdAdapter adapter,
        IProgress<DiagnosticProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _progress = progress;
        ReportProgress(DiagnosticPhase.VehicleId, "Reading vehicle identification...", 0, 0, 3);

        string? vin = null;
        string? rawVin = null;
        string? calibId = null;
        string? ecuName = null;

        // VIN (Mode 09 PID 02)
        ReportProgress(DiagnosticPhase.VehicleId, "Reading VIN (0902)...", 0, 0, 3, "0902");
        try
        {
            var response = await adapter.SendCommandAsync(
                new ObdCommand("0902", TimeSpan.FromSeconds(10)),
                cancellationToken);
            rawVin = response.RawResponse ?? response.Value;

            if (response.Success && !string.IsNullOrEmpty(response.Value))
            {
                vin = ParseVin(response.Value);
                if (vin != null)
                {
                    AddNote($"VIN detected: {vin}");
                }
            }

            ReportProgress(DiagnosticPhase.VehicleId, $"VIN: {vin ?? "Not available"}", 0.33, 1, 3, "0902", rawVin, vin != null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddError("VIN Read", ex.Message);
            ReportProgress(DiagnosticPhase.VehicleId, $"VIN: ERROR - {ex.Message}", 0.33, 1, 3, "0902", ex.Message, false);
        }

        // Calibration ID (Mode 09 PID 04) - don't let failure stop collection
        ReportProgress(DiagnosticPhase.VehicleId, "Reading Calibration ID (0904)...", 0.33, 1, 3, "0904");
        try
        {
            var response = await adapter.SendCommandAsync(
                new ObdCommand("0904", TimeSpan.FromSeconds(5)),
                cancellationToken);
            if (response.Success)
            {
                calibId = response.RawResponse ?? response.Value;
            }
            ReportProgress(DiagnosticPhase.VehicleId, $"Calibration ID: {TruncateResponse(calibId)}", 0.66, 2, 3, "0904", calibId, response.Success);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Optional - don't report as error, continue
            ReportProgress(DiagnosticPhase.VehicleId, "Calibration ID: Not available", 0.66, 2, 3, "0904", null, false);
        }

        // ECU Name (Mode 09 PID 0A) - don't let failure stop collection
        ReportProgress(DiagnosticPhase.VehicleId, "Reading ECU Name (090A)...", 0.66, 2, 3, "090A");
        try
        {
            var response = await adapter.SendCommandAsync(
                new ObdCommand("090A", TimeSpan.FromSeconds(5)),
                cancellationToken);
            if (response.Success)
            {
                ecuName = response.RawResponse ?? response.Value;
            }
            ReportProgress(DiagnosticPhase.VehicleId, $"ECU Name: {TruncateResponse(ecuName)}", 1.0, 3, 3, "090A", ecuName, response.Success);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Optional - continue
            ReportProgress(DiagnosticPhase.VehicleId, "ECU Name: Not available", 1.0, 3, 3, "090A", null, false);
        }

        return new VehicleIdentification
        {
            Vin = vin,
            RawVinResponse = rawVin,
            CalibrationId = calibId,
            EcuName = ecuName
        };
    }

    /// <summary>
    /// Probes EV-specific CAN addresses for vehicles like Nissan Leaf
    /// </summary>
    public async Task<List<CanProbeResult>> ProbeEvCanAddressesAsync(
        IObdAdapter adapter,
        string vehicleMake,
        IProgress<DiagnosticProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _progress = progress;
        _canProbeResults.Clear();

        var isNissan = vehicleMake.Equals("Nissan", StringComparison.OrdinalIgnoreCase);

        // Select probes based on vehicle make
        var probes = vehicleMake.ToUpperInvariant() switch
        {
            "NISSAN" => NissanLeafProbes,
            _ => GenericEvCanProbes
        };

        var totalProbes = probes.Length;
        ReportProgress(DiagnosticPhase.EvCanProbe, $"Probing {totalProbes} EV CAN addresses for {vehicleMake}...", 0, 0, totalProbes);

        string? currentHeader = null;
        var headersEnabled = false;

        for (var i = 0; i < probes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (command, description) = probes[i];

            ReportProgress(
                DiagnosticPhase.EvCanProbe,
                $"Sending {command} ({description})...",
                (double)i / totalProbes,
                i,
                totalProbes,
                command);

            var result = new CanProbeResult
            {
                Command = command,
                Description = description,
                Header = currentHeader
            };

            try
            {
                var timeout = command.StartsWith("21") || command.StartsWith("22")
                    ? TimeSpan.FromSeconds(10)
                    : TimeSpan.FromSeconds(5);

                var sw = Stopwatch.StartNew();
                var response = await adapter.SendCommandAsync(new ObdCommand(command, timeout), cancellationToken);
                sw.Stop();

                var raw = response.RawResponse;
                result.RawResponse = raw;
                result.ResponseTime = sw.Elapsed;

                if (command.StartsWith("ATSH", StringComparison.OrdinalIgnoreCase))
                {
                    currentHeader = command.Substring(4);
                    result.Header = currentHeader;

                    result.Success = response.Success &&
                        (raw?.Contains("OK") == true || string.IsNullOrWhiteSpace(response.Error));
                }
                else if (command.Equals("ATH1", StringComparison.OrdinalIgnoreCase))
                {
                    headersEnabled = response.Success &&
                        (raw?.Contains("OK") == true || string.IsNullOrWhiteSpace(response.Error));

                    result.Success = headersEnabled;
                }
                else if (command.Equals("ATH0", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = response.Success &&
                        (raw?.Contains("OK") == true || string.IsNullOrWhiteSpace(response.Error));

                    if (ok)
                        headersEnabled = false;

                    result.Success = ok;
                }
                else if (command.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
                {
                    result.Success = response.Success || raw?.Contains("OK") == true || string.IsNullOrWhiteSpace(response.Error);
                }
                else
                {
                    var hasData = response.Success &&
                                  !string.IsNullOrEmpty(raw) &&
                                  !raw.Contains("NO DATA") &&
                                  !raw.Contains("UNABLE") &&
                                  !raw.Contains("ERROR") &&
                                  !raw.Contains("?") &&
                                  raw.Length > 2;

                    if (hasData && isNissan && headersEnabled)
                    {
                        var expectedRx = currentHeader?.ToUpperInvariant() switch
                        {
                            "79B" => "7BB",
                            "797" => "79A",
                            _ => null
                        };

                        if (expectedRx != null && !raw.Contains(expectedRx, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Success = false;
                            result.Error = $"Response did not contain expected RX header {expectedRx}";
                        }
                        else
                        {
                            result.Success = true;
                        }
                    }
                    else
                    {
                        result.Success = hasData;
                    }
                }

                if (result.Success && !command.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
                {
                    AddNote($"EV CAN probe {command} with header {currentHeader} responded ({raw?.Length ?? 0} chars): {TruncateResponse(raw, 80)}");
                }

                var display = result.Success
                    ? TruncateResponse(raw)
                    : result.Error ?? "No response";

                ReportProgress(
                    DiagnosticPhase.EvCanProbe,
                    $"{command}: {display} ({sw.ElapsedMilliseconds}ms)",
                    (double)(i + 1) / totalProbes,
                    i + 1,
                    totalProbes,
                    command,
                    raw,
                    result.Success);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                result.Success = false;

                ReportProgress(
                    DiagnosticPhase.EvCanProbe,
                    $"{command}: ERROR - {ex.Message}",
                    (double)(i + 1) / totalProbes,
                    i + 1,
                    totalProbes,
                    command,
                    ex.Message,
                    false);
            }

            _canProbeResults.Add(result);

            var delayMs = command.StartsWith("21") || command.StartsWith("22") ? 500 :
                          command.StartsWith("ATSH") || command.StartsWith("ATFC") ? 300 :
                          200;
            await Task.Delay(delayMs, cancellationToken);
        }

        try
        {
            await adapter.SendCommandAsync(new ObdCommand("ATSH7DF", TimeSpan.FromSeconds(2)), cancellationToken);
            await Task.Delay(100, cancellationToken);
            await adapter.SendCommandAsync(new ObdCommand("ATH0", TimeSpan.FromSeconds(2)), cancellationToken);
            await Task.Delay(100, cancellationToken);
            await adapter.SendCommandAsync(new ObdCommand("ATSP0", TimeSpan.FromSeconds(2)), cancellationToken);
        }
        catch { /* ignore */ }

        var successfulProbes = _canProbeResults.Count(r => r.Success && !r.Command.StartsWith("AT"));
        AddNote($"EV CAN probe complete: {successfulProbes} data responses");

        return _canProbeResults;
    }

    /// <summary>
    /// Probes extended/manufacturer-specific PIDs for EV detection
    /// </summary>
    public async Task ProbeExtendedPidsAsync(
        IObdAdapter adapter,
        IProgress<DiagnosticProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _progress = progress;
        _extendedPidResults.Clear();

        var totalPids = EvProbePids.Length;
        ReportProgress(DiagnosticPhase.ExtendedPidProbe, $"Probing {totalPids} extended/EV PIDs...", 0, 0, totalPids);

        for (var i = 0; i < EvProbePids.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (command, description) = EvProbePids[i];

            ReportProgress(
                DiagnosticPhase.ExtendedPidProbe,
                $"Probing {command} ({description})...",
                (double)i / totalPids,
                i,
                totalPids,
                command);

            var sw = Stopwatch.StartNew();
            try
            {
                var response = await adapter.SendCommandAsync(
                    new ObdCommand(command, TimeSpan.FromSeconds(5)),
                    cancellationToken);

                sw.Stop();

                _extendedPidResults.Add(new PidProbeResult
                {
                    Command = command,
                    Description = description,
                    Success = response.Success,
                    RawResponse = response.RawResponse ?? response.Value,
                    ParsedValue = response.Value,
                    Error = response.Error,
                    ResponseTime = sw.Elapsed
                });

                ReportProgress(
                    DiagnosticPhase.ExtendedPidProbe,
                    $"{command}: {(response.Success ? TruncateResponse(response.RawResponse) : "NO DATA")} ({sw.ElapsedMilliseconds}ms)",
                    (double)(i + 1) / totalPids,
                    i + 1,
                    totalPids,
                    command,
                    response.RawResponse,
                    response.Success);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _extendedPidResults.Add(new PidProbeResult
                {
                    Command = command,
                    Description = description,
                    Success = false,
                    Error = ex.Message,
                    ResponseTime = sw.Elapsed
                });

                ReportProgress(
                    DiagnosticPhase.ExtendedPidProbe,
                    $"{command}: ERROR - {ex.Message}",
                    (double)(i + 1) / totalPids,
                    i + 1,
                    totalPids,
                    command,
                    ex.Message,
                    false);
            }

            await Task.Delay(100, cancellationToken);
        }

        var successCount = _extendedPidResults.Count(r => r.Success);
        AddNote($"Extended PID probe complete: {successCount}/{_extendedPidResults.Count} successful");
    }

    /// <summary>
    /// Probes multiple OBD protocols to find which ones get responses
    /// </summary>
    public async Task<List<ProtocolProbeResult>> ProbeProtocolsAsync(
        IObdAdapter adapter,
        IProgress<DiagnosticProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _progress = progress;
        _protocolProbeResults.Clear();

        var totalProtocols = OdbProtocols.Length;
        ReportProgress(DiagnosticPhase.ProtocolProbe, $"Probing {totalProtocols} OBD protocols...", 0, 0, totalProtocols);

        for (var i = 0; i < OdbProtocols.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (setCommand, name, description) = OdbProtocols[i];

            ReportProgress(
                DiagnosticPhase.ProtocolProbe,
                $"Trying {description}...",
                (double)i / totalProtocols,
                i,
                totalProtocols,
                setCommand);

            var result = new ProtocolProbeResult
            {
                ProtocolCommand = setCommand,
                ProtocolName = name,
                Description = description
            };

            try
            {
                // Set the protocol
                var setResponse = await adapter.SendCommandAsync(
                    new ObdCommand(setCommand, TimeSpan.FromSeconds(3)),
                    cancellationToken);

                // Check if transport disconnected
                if (setResponse.Error == "Transport not connected")
                {
                    result.SetSuccess = false;
                    result.Error = "Transport disconnected";
                    _protocolProbeResults.Add(result);
                    AddError("Protocol Probe", $"Transport disconnected during {setCommand}");
                    break; // Exit loop to let caller reconnect
                }

                if (!setResponse.Success || setResponse.RawResponse?.Contains("ERROR") == true)
                {
                    result.SetSuccess = false;
                    result.SetResponse = setResponse.RawResponse;
                    _protocolProbeResults.Add(result);
                    await Task.Delay(200, cancellationToken);
                    continue;
                }

                result.SetSuccess = true;
                result.SetResponse = setResponse.RawResponse;

                // Wait a bit for protocol change to take effect
                await Task.Delay(300, cancellationToken);

                // Try a simple OBD query to test if protocol works
                // Use shorter timeout to avoid long waits
                var sw = Stopwatch.StartNew();
                var testResponse = await adapter.SendCommandAsync(
                    new ObdCommand("0100", TimeSpan.FromSeconds(5)),
                    cancellationToken);
                sw.Stop();

                // Check if transport disconnected during test
                if (testResponse.Error == "Transport not connected")
                {
                    result.TestResponse = "DISCONNECTED";
                    result.Error = "Transport disconnected during test";
                    _protocolProbeResults.Add(result);
                    AddError("Protocol Probe", $"Transport disconnected during 0100 test on {description}");
                    break; // Exit loop to let caller reconnect
                }

                result.TestResponse = testResponse.RawResponse;
                result.ResponseTime = sw.Elapsed;

                // Check if we got actual data vs NO DATA/errors
                var gotData = testResponse.Success &&
                              !string.IsNullOrEmpty(testResponse.RawResponse) &&
                              !testResponse.RawResponse.Contains("NO DATA") &&
                              !testResponse.RawResponse.Contains("UNABLE") &&
                              !testResponse.RawResponse.Contains("ERROR") &&
                              !testResponse.RawResponse.Contains("STOPPED") &&
                              testResponse.RawResponse.Contains("41");

                result.GotResponse = gotData;

                if (gotData)
                {
                    AddNote($"Protocol {description} responded with data");
                }

                ReportProgress(
                    DiagnosticPhase.ProtocolProbe,
                    $"{description}: {(gotData ? "RESPONDED" : "No data")} ({sw.ElapsedMilliseconds}ms)",
                    (double)(i + 1) / totalProtocols,
                    i + 1,
                    totalProtocols,
                    setCommand,
                    testResponse.RawResponse,
                    gotData);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                ReportProgress(
                    DiagnosticPhase.ProtocolProbe,
                    $"{description}: ERROR - {ex.Message}",
                    (double)(i + 1) / totalProtocols,
                    i + 1,
                    totalProtocols,
                    setCommand,
                    ex.Message,
                    false);

                // On exception, break to let caller handle reconnection
                _protocolProbeResults.Add(result);
                break;
            }

            _protocolProbeResults.Add(result);

            // Longer delay between protocol changes to keep BLE stable
            await Task.Delay(500, cancellationToken);
        }

        // Reset to auto protocol (don't wait for response if disconnected)
        try
        {
            await adapter.SendCommandAsync(new ObdCommand("ATSP0", TimeSpan.FromSeconds(2)), cancellationToken);
        }
        catch { /* ignore */ }

        var successfulProtocols = _protocolProbeResults.Count(r => r.GotResponse);
        AddNote($"Protocol probe complete: {successfulProtocols}/{_protocolProbeResults.Count} protocols responded");

        return _protocolProbeResults;
    }

    /// <summary>
    /// Probes all standard PIDs and records responses
    /// </summary>
    public async Task ProbeStandardPidsAsync(
        IObdAdapter adapter,
        SupportedPidsInfo? supportedPids,
        IProgress<DiagnosticProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _progress = progress;
        _standardPidResults.Clear();

        var allPids = StandardMode01Pids.Concat(Mode09Pids).ToList();
        var totalPids = allPids.Count;

        ReportProgress(DiagnosticPhase.StandardPidProbe, $"Probing {totalPids} standard PIDs...", 0, 0, totalPids);

        for (var i = 0; i < allPids.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (command, description) = allPids[i];

            ReportProgress(
                DiagnosticPhase.StandardPidProbe,
                $"Probing {command} ({description})...",
                (double)i / totalPids,
                i,
                totalPids,
                command);

            var sw = Stopwatch.StartNew();
            try
            {
                var response = await adapter.SendCommandAsync(
                    new ObdCommand(command, TimeSpan.FromSeconds(5)),
                    cancellationToken);

                sw.Stop();

                var result = new PidProbeResult
                {
                    Command = command,
                    Description = description,
                    Success = response.Success,
                    RawResponse = response.RawResponse ?? response.Value,
                    ParsedValue = response.Value,
                    Error = response.Error,
                    ResponseTime = sw.Elapsed
                };

                _standardPidResults.Add(result);

                ReportProgress(
                    DiagnosticPhase.StandardPidProbe,
                    $"{command}: {(response.Success ? TruncateResponse(response.RawResponse) : "NO DATA")} ({sw.ElapsedMilliseconds}ms)",
                    (double)(i + 1) / totalPids,
                    i + 1,
                    totalPids,
                    command,
                    response.RawResponse,
                    response.Success);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _standardPidResults.Add(new PidProbeResult
                {
                    Command = command,
                    Description = description,
                    Success = false,
                    Error = ex.Message,
                    ResponseTime = sw.Elapsed
                });

                ReportProgress(
                    DiagnosticPhase.StandardPidProbe,
                    $"{command}: ERROR - {ex.Message}",
                    (double)(i + 1) / totalPids,
                    i + 1,
                    totalPids,
                    command,
                    ex.Message,
                    false);
            }

            await Task.Delay(50, cancellationToken);
        }

        var successCount = _standardPidResults.Count(r => r.Success);
        AddNote($"Standard PID probe complete: {successCount}/{_standardPidResults.Count} successful");
    }

    /// <summary>
    /// Clears all collected data for a new collection run
    /// </summary>
    public void Reset()
    {
        _errors.Clear();
        _notes.Clear();
        _standardPidResults.Clear();
        _extendedPidResults.Clear();
        _protocolProbeResults.Clear();
        _canProbeResults.Clear();
    }

    private static double CalculateOverallProgress(DiagnosticPhase phase, double phaseProgress)
    {
        // Weight phases by typical duration
        var phaseWeights = new Dictionary<DiagnosticPhase, (double Start, double Weight)>
        {
            [DiagnosticPhase.BleDiscovery] = (0.0, 0.05),
            [DiagnosticPhase.Connecting] = (0.05, 0.05),
            [DiagnosticPhase.AdapterInit] = (0.10, 0.03),
            [DiagnosticPhase.AdapterInfo] = (0.13, 0.07),
            [DiagnosticPhase.ProtocolProbe] = (0.20, 0.10),
            [DiagnosticPhase.VehicleId] = (0.30, 0.05),
            [DiagnosticPhase.SupportedPids] = (0.35, 0.05),
            [DiagnosticPhase.StandardPidProbe] = (0.40, 0.25),
            [DiagnosticPhase.ExtendedPidProbe] = (0.65, 0.10),
            [DiagnosticPhase.EvCanProbe] = (0.75, 0.15),
            [DiagnosticPhase.GeneratingReport] = (0.90, 0.10),
            [DiagnosticPhase.Complete] = (1.0, 0.0),
        };

        if (phaseWeights.TryGetValue(phase, out var weight))
        {
            return weight.Start + (phaseProgress * weight.Weight);
        }

        return 0;
    }

    private static string GetToolVersion()
    {
        var assembly = typeof(DiagnosticDataCollector).Assembly;
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "1.0.0";
    }

    private static IEnumerable<string> ParseSupportedPidsBitmap(string response, string query)
    {
        var hexData = response.Replace(" ", "").Replace("\n", "").Replace("\r", "");

        // Extract mode and base PID from query
        var mode = query[..2];
        var basePid = byte.Parse(query[2..], System.Globalization.NumberStyles.HexNumber);

        // Skip header (e.g., 4100 for query 0100)
        if (hexData.Length >= 12)
            hexData = hexData.Substring(4, 8);
        else
            yield break;

        if (uint.TryParse(hexData, System.Globalization.NumberStyles.HexNumber, null, out var bitmap))
        {
            for (var i = 0; i < 32; i++)
            {
                if ((bitmap & (1u << (31 - i))) != 0)
                    yield return $"{mode}{(basePid + i + 1):X2}";
            }
        }
    }

    private static string? ParseVin(string response)
    {
        try
        {
            var hexData = response.Replace(" ", "").Replace("\n", "").Replace("\r", "");
            var vinBytes = new List<byte>();

            for (var i = 0; i < hexData.Length - 1; i += 2)
            {
                if (byte.TryParse(hexData.Substring(i, 2),
                    System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    if (b >= 0x20 && b <= 0x7E)
                        vinBytes.Add(b);
                }
            }

            var vin = System.Text.Encoding.ASCII.GetString(vinBytes.ToArray());
            return vin.Length >= 17 ? vin[..17] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string TruncateResponse(string? response, int maxLength = 40)
    {
        if (string.IsNullOrEmpty(response))
            return "<empty>";

        var cleaned = response.Replace("\r", "").Replace("\n", " ").Trim();
        if (cleaned.Length <= maxLength)
            return cleaned;

        return cleaned[..(maxLength - 3)] + "...";
    }

    private void ReportProgress(
                            DiagnosticPhase phase,
        string message,
        double phaseProgress,
        int itemsCompleted,
        int itemsTotal,
        string? currentItem = null,
        string? lastResponse = null,
        bool? lastSuccess = null)
    {
        _progress?.Report(new DiagnosticProgress
        {
            Phase = phase,
            Message = message,
            PhaseProgress = phaseProgress,
            OverallProgress = CalculateOverallProgress(phase, phaseProgress),
            CurrentItem = currentItem,
            ItemsCompleted = itemsCompleted,
            ItemsTotal = itemsTotal,
            LastResponse = lastResponse,
            LastOperationSuccess = lastSuccess
        });
    }
}