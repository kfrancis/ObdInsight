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
    private readonly List<DiagnosticError> _errors = [];
    private readonly List<string> _notes = [];
    private readonly List<PidProbeResult> _standardPidResults = [];
    private readonly List<PidProbeResult> _extendedPidResults = [];

    private IProgress<DiagnosticProgress>? _progress;
    private int _totalPhases;
    private int _currentPhaseIndex;

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
    /// Extended PIDs for EV/Hybrid detection
    /// </summary>
    private static readonly (string Command, string Description)[] EvProbePids =
    [
        ("015B", "Hybrid battery pack remaining life"),
        ("015E", "Engine fuel rate (0 = EV)"),
        ("2101", "Manufacturer-specific battery data (Nissan)"),
        ("220101", "Manufacturer-specific battery data (GM/Kia)"),
        ("7E421C0", "Tesla-specific probe"),
    ];

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

                await Task.Delay(100, cancellationToken);
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

        // Calibration ID (Mode 09 PID 04)
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
            // Optional - don't report as error
        }

        // ECU Name (Mode 09 PID 0A)
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
            // Optional
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
                $"[{i + 1}/{totalPids}] Probing {command} ({description})...",
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
                    $"[{i + 1}/{totalPids}] {command}: {(response.Success ? TruncateResponse(response.RawResponse) : "NO DATA")} ({sw.ElapsedMilliseconds}ms)",
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
                    $"[{i + 1}/{totalPids}] {command}: ERROR - {ex.Message}",
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
                $"[{i + 1}/{totalPids}] Probing {command} ({description})...",
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
                    $"[{i + 1}/{totalPids}] {command}: {(response.Success ? TruncateResponse(response.RawResponse) : "NO DATA")} ({sw.ElapsedMilliseconds}ms)",
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
                    $"[{i + 1}/{totalPids}] {command}: ERROR - {ex.Message}",
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
            Errors = _errors.ToList(),
            Notes = _notes.ToList()
        };
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
    }

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

    private static double CalculateOverallProgress(DiagnosticPhase phase, double phaseProgress)
    {
        // Weight phases by typical duration
        var phaseWeights = new Dictionary<DiagnosticPhase, (double Start, double Weight)>
        {
            [DiagnosticPhase.BleDiscovery] = (0.0, 0.05),
            [DiagnosticPhase.Connecting] = (0.05, 0.05),
            [DiagnosticPhase.AdapterInit] = (0.10, 0.05),
            [DiagnosticPhase.AdapterInfo] = (0.15, 0.10),
            [DiagnosticPhase.VehicleId] = (0.25, 0.05),
            [DiagnosticPhase.SupportedPids] = (0.30, 0.10),
            [DiagnosticPhase.StandardPidProbe] = (0.40, 0.40),
            [DiagnosticPhase.ExtendedPidProbe] = (0.80, 0.15),
            [DiagnosticPhase.GeneratingReport] = (0.95, 0.05),
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

    private static string TruncateResponse(string? response, int maxLength = 40)
    {
        if (string.IsNullOrEmpty(response))
            return "<empty>";

        var cleaned = response.Replace("\r", "").Replace("\n", " ").Trim();
        if (cleaned.Length <= maxLength)
            return cleaned;

        return cleaned[..(maxLength - 3)] + "...";
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
}
