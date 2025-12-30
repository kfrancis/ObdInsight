using System.Diagnostics;
using ObdInsight.Core;
using ObdInsight.Core.Diagnostics;
using Spectre.Console;

namespace ObdInsight.DevTools;

/// <summary>
/// Collects comprehensive diagnostic data from a vehicle/adapter for generating support reports.
/// </summary>
public class DiagnosticDataCollector
{
    private readonly List<DiagnosticError> _errors = [];
    private readonly List<string> _notes = [];
    private readonly List<PidProbeResult> _standardPidResults = [];
    private readonly List<PidProbeResult> _extendedPidResults = [];

    /// <summary>
    /// Standard Mode 01 PIDs to probe (beyond the supported PIDs query)
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
    /// Mode 09 PIDs to probe (vehicle information)
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
        ("ATAL", "Allow long messages"),
        ("ATSP0", "Set protocol to auto"),
        ("ATST32", "Set timeout"),
    ];

    public event EventHandler<string>? StatusUpdate;

    /// <summary>
    /// Collects BLE adapter information including GATT services
    /// </summary>
    public async Task<BleAdapterInfo?> CollectBleInfoAsync(string macAddress)
    {
        try
        {
            RaiseStatus("Discovering BLE services...");

            var mac = ParseMacAddress(macAddress);
            using var device = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromBluetoothAddressAsync(mac);

            if (device == null)
            {
                AddError("BLE Discovery", "Failed to connect to BLE device");
                return null;
            }

            var servicesResult = await device.GetGattServicesAsync(
                Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);

            if (servicesResult.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
            {
                AddError("BLE Discovery", $"Failed to get services: {servicesResult.Status}");
                return null;
            }

            var services = new List<BleServiceInfo>();

            foreach (var service in servicesResult.Services)
            {
                var characteristics = new List<BleCharacteristicInfo>();

                var charsResult = await service.GetCharacteristicsAsync(
                    Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);

                if (charsResult.Status == Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
                {
                    foreach (var characteristic in charsResult.Characteristics)
                    {
                        var props = GetPropertyStrings(characteristic.CharacteristicProperties).ToList();
                        characteristics.Add(new BleCharacteristicInfo
                        {
                            CharacteristicUuid = characteristic.Uuid,
                            Properties = props
                        });
                    }
                }

                services.Add(new BleServiceInfo
                {
                    ServiceUuid = service.Uuid,
                    Characteristics = characteristics
                });

                service.Dispose();
            }

            AddNote($"Found {services.Count} BLE services with {services.Sum(s => s.Characteristics.Count)} characteristics");

            return new BleAdapterInfo
            {
                DeviceName = device.Name ?? "Unknown",
                MacAddress = macAddress,
                Services = services
            };
        }
        catch (Exception ex)
        {
            AddError("BLE Discovery", ex.Message, ex.ToString());
            return null;
        }
    }

    /// <summary>
    /// Collects OBD adapter information (ELM327 version, etc.)
    /// </summary>
    public async Task<ObdAdapterInfo> CollectObdAdapterInfoAsync(IObdAdapter adapter)
    {
        RaiseStatus("Probing OBD adapter...");

        var rawResponses = new Dictionary<string, string>();
        string? resetResponse = null;
        string? versionResponse = null;
        string? deviceDesc = null;
        string? voltage = null;
        string? protocol = null;
        string? protocolNum = null;

        foreach (var (command, description) in AtCommands)
        {
            try
            {
                RaiseStatus($"Sending {command}...");
                var response = await adapter.SendCommandAsync(
                    new ObdCommand(command, TimeSpan.FromSeconds(5)));

                var rawValue = response.RawResponse ?? response.Value ?? "";
                rawResponses[command] = rawValue;

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

                // Small delay between commands
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                rawResponses[command] = $"ERROR: {ex.Message}";
                AddError("AT Command", $"Failed to send {command}: {ex.Message}");
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
    public async Task<VehicleIdentification> CollectVehicleIdAsync(IObdAdapter adapter)
    {
        RaiseStatus("Reading vehicle identification...");

        string? vin = null;
        string? rawVin = null;
        string? calibId = null;
        string? ecuName = null;

        // VIN (Mode 09 PID 02)
        try
        {
            var response = await adapter.SendCommandAsync(
                new ObdCommand("0902", TimeSpan.FromSeconds(10)));
            rawVin = response.RawResponse ?? response.Value;

            if (response.Success && !string.IsNullOrEmpty(response.Value))
            {
                vin = ParseVin(response.Value);
                if (vin != null)
                {
                    AddNote($"VIN detected: {vin}");
                }
            }
        }
        catch (Exception ex)
        {
            AddError("VIN Read", ex.Message);
        }

        // Calibration ID (Mode 09 PID 04)
        try
        {
            var response = await adapter.SendCommandAsync(
                new ObdCommand("0904", TimeSpan.FromSeconds(5)));
            if (response.Success)
            {
                calibId = response.RawResponse ?? response.Value;
            }
        }
        catch { /* Optional */ }

        // ECU Name (Mode 09 PID 0A)
        try
        {
            var response = await adapter.SendCommandAsync(
                new ObdCommand("090A", TimeSpan.FromSeconds(5)));
            if (response.Success)
            {
                ecuName = response.RawResponse ?? response.Value;
            }
        }
        catch { /* Optional */ }

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
    public async Task<SupportedPidsInfo> CollectSupportedPidsAsync(IObdAdapter adapter)
    {
        RaiseStatus("Querying supported PIDs...");

        var mode01Pids = new List<string>();
        var mode09Pids = new List<string>();
        var rawResponses = new Dictionary<string, string>();

        // Mode 01 supported PIDs (query in ranges)
        foreach (var pidQuery in new[] { "0100", "0120", "0140", "0160", "0180", "01A0", "01C0" })
        {
            try
            {
                var response = await adapter.SendCommandAsync(
                    new ObdCommand(pidQuery, TimeSpan.FromSeconds(5)));

                rawResponses[pidQuery] = response.RawResponse ?? response.Value ?? "";

                if (response.Success && !string.IsNullOrEmpty(response.Value))
                {
                    var pids = ParseSupportedPidsBitmap(response.Value, pidQuery);
                    mode01Pids.AddRange(pids);

                    // If this range doesn't include the next range query, stop
                    var nextQuery = $"01{(byte.Parse(pidQuery[2..], System.Globalization.NumberStyles.HexNumber) + 0x20):X2}";
                    if (!pids.Contains(nextQuery))
                        break;
                }
                else
                {
                    break; // No more PIDs supported
                }
            }
            catch (Exception ex)
            {
                AddError("PID Query", $"Failed {pidQuery}: {ex.Message}");
                break;
            }
        }

        // Mode 09 supported PIDs
        try
        {
            var response = await adapter.SendCommandAsync(
                new ObdCommand("0900", TimeSpan.FromSeconds(5)));

            rawResponses["0900"] = response.RawResponse ?? response.Value ?? "";

            if (response.Success && !string.IsNullOrEmpty(response.Value))
            {
                mode09Pids.AddRange(ParseSupportedPidsBitmap(response.Value, "0900"));
            }
        }
        catch { /* Optional */ }

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
    public async Task ProbeStandardPidsAsync(IObdAdapter adapter, SupportedPidsInfo supportedPids)
    {
        RaiseStatus("Probing standard PIDs...");

        var allPids = StandardMode01Pids.Concat(Mode09Pids).ToList();
        var count = 0;

        foreach (var (command, description) in allPids)
        {
            count++;
            RaiseStatus($"Probing {command} ({count}/{allPids.Count})...");

            var sw = Stopwatch.StartNew();
            try
            {
                var response = await adapter.SendCommandAsync(
                    new ObdCommand(command, TimeSpan.FromSeconds(5)));

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
            }

            // Small delay between probes
            await Task.Delay(50);
        }

        var successCount = _standardPidResults.Count(r => r.Success);
        AddNote($"Standard PID probe complete: {successCount}/{_standardPidResults.Count} successful");
    }

    /// <summary>
    /// Probes extended/manufacturer-specific PIDs for EV detection
    /// </summary>
    public async Task ProbeExtendedPidsAsync(IObdAdapter adapter)
    {
        RaiseStatus("Probing extended/EV PIDs...");

        foreach (var (command, description) in EvProbePids)
        {
            RaiseStatus($"Probing {command}...");

            var sw = Stopwatch.StartNew();
            try
            {
                var response = await adapter.SendCommandAsync(
                    new ObdCommand(command, TimeSpan.FromSeconds(5)));

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
            }

            await Task.Delay(100);
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
            StandardPidResults = _standardPidResults,
            ExtendedPidResults = _extendedPidResults,
            Errors = _errors,
            Notes = _notes
        };
    }

    private void AddError(string phase, string message, string? details = null)
    {
        _errors.Add(new DiagnosticError
        {
            Phase = phase,
            Message = message,
            Details = details
        });
    }

    private void AddNote(string note)
    {
        _notes.Add(note);
    }

    private void RaiseStatus(string status)
    {
        StatusUpdate?.Invoke(this, status);
    }

    private static string GetToolVersion()
    {
        var assembly = typeof(DiagnosticDataCollector).Assembly;
        var version = assembly.GetName().Version;
        return version?.ToString() ?? "1.0.0";
    }

    private static ulong ParseMacAddress(string mac)
    {
        var cleanMac = mac.Replace(":", "").Replace("-", "");
        return Convert.ToUInt64(cleanMac, 16);
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

    private static IEnumerable<string> GetPropertyStrings(
        Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties props)
    {
        if (props.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Read))
            yield return "Read";
        if (props.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Write))
            yield return "Write";
        if (props.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.WriteWithoutResponse))
            yield return "WriteNoResp";
        if (props.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Notify))
            yield return "Notify";
        if (props.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Indicate))
            yield return "Indicate";
    }
}
