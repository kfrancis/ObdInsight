using ObdInsight.Core.Adapters.Elm327;
using System.Globalization;

namespace ObdInsight.Core.Vehicles;

/// <summary>
/// Vehicle-aware OBD service implementation.
/// Combines an OBD adapter with a vehicle profile for intelligent data retrieval.
/// </summary>
public class VehicleObdService : IVehicleObdService
{
    private readonly IObdAdapter _adapter;
    private readonly IVehicleDetector _detector;
    private IObdTransport? _transport;
    private IVehicleProfile _profile;

    public VehicleObdService(
        IObdAdapter? adapter = null,
        IVehicleDetector? detector = null,
        IVehicleProfile? initialProfile = null)
    {
        _adapter = adapter ?? new Elm327Adapter();
        _detector = detector ?? new VehicleDetectorService();
        _profile = initialProfile ?? new StandardObdVehicleProfile();
    }

    public bool IsConnected => _transport?.IsConnected == true && _adapter.IsInitialized;
    public IVehicleProfile VehicleProfile => _profile;

    public async Task<bool> ConnectAsync(IObdTransport transport, CancellationToken cancellationToken = default)
    {
        return await ConnectAsync(transport, new VehicleServiceOptions(), cancellationToken);
    }

    public async Task<bool> ConnectAsync(
        IObdTransport transport,
        VehicleServiceOptions options,
        CancellationToken cancellationToken = default)
    {
        _transport = transport;

        // Connect transport if not already connected
        if (!transport.IsConnected)
        {
            if (!await transport.ConnectAsync(cancellationToken))
            {
                return false;
            }
        }

        // Initialize adapter
        if (!await _adapter.InitializeAsync(transport, cancellationToken))
        {
            return false;
        }

        // Set profile from options or detect
        if (options.ManualProfile != null)
        {
            _profile = options.ManualProfile;
        }
        else if (options.AutoDetectVehicle)
        {
            using var detectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            detectionCts.CancelAfter(options.DetectionTimeout);

            try
            {
                var result = await _detector.DetectFromEcuAsync(_adapter, detectionCts.Token);
                _profile = result.Profile;
            }
            catch (OperationCanceledException)
            {
                // Detection timed out, use generic profile
                _profile = new StandardObdVehicleProfile();
            }
        }

        // Run vehicle-specific initialization
        if (options.RunVehicleInit)
        {
            foreach (var command in _profile.GetInitializationCommands())
            {
                await _adapter.SendCommandAsync(command, cancellationToken);
            }
        }

        return true;
    }

    public async Task DisconnectAsync()
    {
        if (_transport != null)
        {
            await _adapter.ResetAsync();
            await _transport.DisconnectAsync();
        }
    }

    #region IVehicleObdService Implementation

    public bool IsDataPointSupported(VehicleDataPoint dataPoint)
    {
        return _profile.GetCommand(dataPoint) != null;
    }

    public async Task<VehicleDataResult> GetDataAsync(VehicleDataPoint dataPoint, CancellationToken cancellationToken = default)
    {
        var command = _profile.GetCommand(dataPoint);
        if (command == null)
        {
            return VehicleDataResult.Fail(dataPoint, "Data point not supported by vehicle profile");
        }

        var response = await _adapter.SendCommandAsync(command, cancellationToken);
        if (!response.Success)
        {
            return VehicleDataResult.Fail(dataPoint, response.Error ?? "Command failed");
        }

        var bytes = ParseHexResponse(response.Value ?? "");
        if (bytes.Length == 0)
        {
            return VehicleDataResult.Fail(dataPoint, "Empty response");
        }

        return _profile.DecodeResponse(dataPoint, bytes);
    }

    public async Task<IReadOnlyList<VehicleDataResult>> GetDataBatchAsync(
        IEnumerable<VehicleDataPoint> dataPoints,
        CancellationToken cancellationToken = default)
    {
        var results = new List<VehicleDataResult>();

        foreach (var dataPoint in dataPoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await GetDataAsync(dataPoint, cancellationToken);
            results.Add(result);
        }

        return results;
    }

    public async Task<double?> GetBatterySocAsync(CancellationToken cancellationToken = default)
    {
        if (!_profile.IsElectric)
            return null;

        var result = await GetDataAsync(VehicleDataPoint.BatteryStateOfCharge, cancellationToken);
        return result.GetValue<double?>();
    }

    public async Task<double?> GetBatterySohAsync(CancellationToken cancellationToken = default)
    {
        if (!_profile.IsElectric)
            return null;

        var result = await GetDataAsync(VehicleDataPoint.BatteryStateOfHealth, cancellationToken);
        return result.GetValue<double?>();
    }

    public async Task<double?> GetBatteryVoltageAsync(CancellationToken cancellationToken = default)
    {
        if (!_profile.IsElectric)
            return null;

        var result = await GetDataAsync(VehicleDataPoint.BatteryVoltage, cancellationToken);
        return result.GetValue<double?>();
    }

    public async Task<double?> GetRangeRemainingAsync(CancellationToken cancellationToken = default)
    {
        if (!_profile.IsElectric)
            return null;

        var result = await GetDataAsync(VehicleDataPoint.RangeRemaining, cancellationToken);
        return result.GetValue<double?>();
    }

    public async Task<string?> GetChargingStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!_profile.IsElectric)
            return null;

        var result = await GetDataAsync(VehicleDataPoint.ChargingStatus, cancellationToken);
        return result.GetValue<string>();
    }

    public async Task<BatteryInfo?> GetBatteryInfoAsync(CancellationToken cancellationToken = default)
    {
        if (!_profile.IsElectric)
            return null;

        var dataPoints = new[]
        {
            VehicleDataPoint.BatteryStateOfCharge,
            VehicleDataPoint.BatteryStateOfHealth,
            VehicleDataPoint.BatteryVoltage,
            VehicleDataPoint.BatteryCurrent,
            VehicleDataPoint.BatteryTemp,
            VehicleDataPoint.BatteryCapacity,
            VehicleDataPoint.RangeRemaining,
            VehicleDataPoint.ChargingStatus
        };

        var results = await GetDataBatchAsync(dataPoints, cancellationToken);
        var resultMap = results.ToDictionary(r => r.DataPoint);

        // Only return if we got the essential data
        if (!resultMap.TryGetValue(VehicleDataPoint.BatteryStateOfCharge, out var socResult) || !socResult.Success)
            return null;

        return new BatteryInfo(
            StateOfCharge: socResult.GetValue<double?>() ?? 0,
            StateOfHealth: resultMap.GetValueOrDefault(VehicleDataPoint.BatteryStateOfHealth)?.GetValue<double?>() ?? 0,
            Voltage: resultMap.GetValueOrDefault(VehicleDataPoint.BatteryVoltage)?.GetValue<double?>() ?? 0,
            Current: resultMap.GetValueOrDefault(VehicleDataPoint.BatteryCurrent)?.GetValue<double?>() ?? 0,
            Temperature: resultMap.GetValueOrDefault(VehicleDataPoint.BatteryTemp)?.GetValue<double?>() ?? 0,
            Capacity: resultMap.GetValueOrDefault(VehicleDataPoint.BatteryCapacity)?.GetValue<double?>() ?? 0,
            RangeRemaining: resultMap.GetValueOrDefault(VehicleDataPoint.RangeRemaining)?.GetValue<double?>() ?? 0,
            ChargingStatus: resultMap.GetValueOrDefault(VehicleDataPoint.ChargingStatus)?.GetValue<string>() ?? "Unknown"
        );
    }

    #endregion IVehicleObdService Implementation

    #region IObdService Implementation (delegates to standard PIDs)

    public async Task<string?> GetVinAsync(CancellationToken cancellationToken = default)
    {
        var response = await _adapter.SendCommandAsync(new ObdCommand("0902", TimeSpan.FromSeconds(10)), cancellationToken);
        if (!response.Success || string.IsNullOrEmpty(response.Value))
            return null;

        return ParseVin(response.Value);
    }

    public async Task<IReadOnlyList<string>> GetSupportedPidsAsync(CancellationToken cancellationToken = default)
    {
        var supported = new List<string>();

        var response = await _adapter.SendCommandAsync(ObdCommand.Create("0100"), cancellationToken);
        if (response.Success && !string.IsNullOrEmpty(response.Value))
        {
            supported.AddRange(ParseSupportedPids(response.Value, 0x00));
        }

        if (supported.Contains("0120"))
        {
            response = await _adapter.SendCommandAsync(ObdCommand.Create("0120"), cancellationToken);
            if (response.Success && !string.IsNullOrEmpty(response.Value))
            {
                supported.AddRange(ParseSupportedPids(response.Value, 0x20));
            }
        }

        return supported;
    }

    public async Task<int?> GetRpmAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetDataAsync(VehicleDataPoint.Rpm, cancellationToken);
        return result.Success ? (int?)result.GetValue<double>() : null;
    }

    public async Task<int?> GetSpeedKphAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetDataAsync(VehicleDataPoint.Speed, cancellationToken);
        return result.GetValue<int>();
    }

    public async Task<double?> GetCoolantTempCelsiusAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetDataAsync(VehicleDataPoint.CoolantTemp, cancellationToken);
        return result.GetValue<double?>();
    }

    public async Task<double?> GetThrottlePositionPercentAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetDataAsync(VehicleDataPoint.ThrottlePosition, cancellationToken);
        return result.GetValue<double?>();
    }

    public async Task<double?> GetFuelLevelPercentAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetDataAsync(VehicleDataPoint.FuelLevel, cancellationToken);
        return result.GetValue<double?>();
    }

    public async Task<double?> GetEngineLoadPercentAsync(CancellationToken cancellationToken = default)
    {
        var result = await GetDataAsync(VehicleDataPoint.EngineLoad, cancellationToken);
        return result.GetValue<double?>();
    }

    public async Task<IReadOnlyList<string>> GetDtcCodesAsync(CancellationToken cancellationToken = default)
    {
        var response = await _adapter.SendCommandAsync(new ObdCommand("03", TimeSpan.FromSeconds(10)), cancellationToken);
        if (!response.Success || string.IsNullOrEmpty(response.Value))
            return [];

        return ParseDtcCodes(response.Value);
    }

    public async Task<bool> ClearDtcCodesAsync(CancellationToken cancellationToken = default)
    {
        var response = await _adapter.SendCommandAsync(new ObdCommand("04", TimeSpan.FromSeconds(5)), cancellationToken);
        return response.Success;
    }

    public async Task<ObdPidResponse> QueryPidAsync(ObdPid pid, CancellationToken cancellationToken = default)
    {
        var response = await _adapter.SendCommandAsync(ObdCommand.Create(pid.Command), cancellationToken);

        if (!response.Success)
            return new ObdPidResponse(pid, false, null, null, response.Error);

        var dataBytes = ParsePidResponse(response.Value ?? "", pid);
        if (dataBytes == null || dataBytes.Length == 0)
            return new ObdPidResponse(pid, false, null, null, "Failed to parse response");

        var value = pid.Decoder?.Invoke(dataBytes) ?? 0;
        return new ObdPidResponse(pid, true, value, dataBytes, null);
    }

    #endregion IObdService Implementation (delegates to standard PIDs)

    #region Parsing Helpers

    private static byte[] ParseHexResponse(string response)
    {
        var hexData = response.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        var bytes = new List<byte>();

        for (var i = 0; i + 1 < hexData.Length; i += 2)
        {
            if (byte.TryParse(hexData.Substring(i, 2), NumberStyles.HexNumber, null, out var b))
            {
                bytes.Add(b);
            }
        }

        return bytes.ToArray();
    }

    private static string? ParseVin(string response)
    {
        try
        {
            var hexData = response.Replace(" ", "").Replace("\n", "");
            var vinBytes = new List<byte>();

            for (var i = 0; i < hexData.Length - 1; i += 2)
            {
                if (byte.TryParse(hexData.Substring(i, 2), NumberStyles.HexNumber, null, out var b))
                {
                    if (b >= 0x20 && b <= 0x7E)
                        vinBytes.Add(b);
                }
            }

            var vin = System.Text.Encoding.ASCII.GetString(vinBytes.ToArray());
            return vin.Length >= 17 ? vin[..17] : vin;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ParseSupportedPids(string response, byte baseOffset)
    {
        var hexData = response.Replace(" ", "").Replace("\n", "");

        if (hexData.Length >= 12)
            hexData = hexData.Substring(4, 8);
        else
            yield break;

        if (uint.TryParse(hexData, NumberStyles.HexNumber, null, out var bitmap))
        {
            for (var i = 0; i < 32; i++)
            {
                if ((bitmap & (1u << (31 - i))) != 0)
                    yield return $"01{(baseOffset + i + 1):X2}";
            }
        }
    }

    private static List<string> ParseDtcCodes(string response)
    {
        var codes = new List<string>();
        var hexData = response.Replace(" ", "").Replace("\n", "");

        if (hexData.StartsWith("43"))
            hexData = hexData[2..];

        for (var i = 0; i + 3 < hexData.Length; i += 4)
        {
            var dtcHex = hexData.Substring(i, 4);
            var dtc = DecodeDtc(dtcHex);
            if (!string.IsNullOrEmpty(dtc) && dtc != "P0000")
                codes.Add(dtc);
        }

        return codes;
    }

    private static string DecodeDtc(string hexBytes)
    {
        if (hexBytes.Length != 4)
            return string.Empty;

        if (!byte.TryParse(hexBytes[..2], NumberStyles.HexNumber, null, out var byte1) ||
            !byte.TryParse(hexBytes[2..], NumberStyles.HexNumber, null, out var byte2))
            return string.Empty;

        var prefixes = new[] { 'P', 'C', 'B', 'U' };
        var prefix = prefixes[(byte1 >> 6) & 0x03];
        var digit1 = (byte1 >> 4) & 0x03;
        var digit2 = byte1 & 0x0F;
        var digit3 = (byte2 >> 4) & 0x0F;
        var digit4 = byte2 & 0x0F;

        return $"{prefix}{digit1:X}{digit2:X}{digit3:X}{digit4:X}";
    }

    private static byte[]? ParsePidResponse(string response, ObdPid pid)
    {
        try
        {
            var hexData = response.Replace(" ", "").Replace("\n", "");
            var expectedHeader = $"{(pid.Mode + 0x40):X2}{pid.Pid:X2}";

            var headerIndex = hexData.IndexOf(expectedHeader, StringComparison.OrdinalIgnoreCase);
            if (headerIndex < 0)
                return null;

            var dataHex = hexData[(headerIndex + expectedHeader.Length)..];
            var bytes = new List<byte>();

            for (var i = 0; i + 1 < dataHex.Length; i += 2)
            {
                if (byte.TryParse(dataHex.Substring(i, 2), NumberStyles.HexNumber, null, out var b))
                    bytes.Add(b);
            }

            return bytes.ToArray();
        }
        catch
        {
            return null;
        }
    }

    #endregion Parsing Helpers
}