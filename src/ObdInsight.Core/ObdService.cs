using ObdInsight.Core.Adapters.Elm327;

namespace ObdInsight.Core;

/// <summary>
/// High-level OBD service for vehicle data queries.
/// Abstracts PID encoding/decoding from the transport and adapter layers.
/// </summary>
public interface IObdService
{
    bool IsConnected { get; }

    Task<bool> ConnectAsync(IObdTransport transport, CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    // Vehicle info
    Task<string?> GetVinAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetSupportedPidsAsync(CancellationToken cancellationToken = default);

    // Live data (Mode 01)
    Task<int?> GetRpmAsync(CancellationToken cancellationToken = default);

    Task<int?> GetSpeedKphAsync(CancellationToken cancellationToken = default);

    Task<double?> GetCoolantTempCelsiusAsync(CancellationToken cancellationToken = default);

    Task<double?> GetThrottlePositionPercentAsync(CancellationToken cancellationToken = default);

    Task<double?> GetFuelLevelPercentAsync(CancellationToken cancellationToken = default);

    Task<double?> GetEngineLoadPercentAsync(CancellationToken cancellationToken = default);

    // Diagnostics (Mode 03)
    Task<IReadOnlyList<string>> GetDtcCodesAsync(CancellationToken cancellationToken = default);

    Task<bool> ClearDtcCodesAsync(CancellationToken cancellationToken = default);

    // Generic PID query
    Task<ObdPidResponse> QueryPidAsync(ObdPid pid, CancellationToken cancellationToken = default);
}

/// <summary>
/// Standard OBD-II PID definitions
/// </summary>
public record ObdPid(
    byte Mode,
    byte Pid,
    string Name,
    string Unit,
    Func<byte[], double>? Decoder = null
)
{
    public string Command => $"{Mode:X2}{Pid:X2}";

    // Mode 01 - Live data PIDs
    public static ObdPid SupportedPids0120 => new(0x01, 0x00, "Supported PIDs [01-20]", "", null);
    public static ObdPid SupportedPids2140 => new(0x01, 0x20, "Supported PIDs [21-40]", "", null);
    public static ObdPid EngineLoad => new(0x01, 0x04, "Engine Load", "%", data => data.Length > 0 ? data[0] * 100.0 / 255.0 : 0);
    public static ObdPid CoolantTemp => new(0x01, 0x05, "Coolant Temp", "°C", data => data.Length > 0 ? data[0] - 40 : 0);
    public static ObdPid Rpm => new(0x01, 0x0C, "Engine RPM", "rpm", data => data.Length >= 2 ? ((data[0] * 256) + data[1]) / 4.0 : 0);
    public static ObdPid VehicleSpeed => new(0x01, 0x0D, "Vehicle Speed", "km/h", data => data.Length > 0 ? data[0] : 0);
    public static ObdPid ThrottlePosition => new(0x01, 0x11, "Throttle Position", "%", data => data.Length > 0 ? data[0] * 100.0 / 255.0 : 0);
    public static ObdPid FuelLevel => new(0x01, 0x2F, "Fuel Level", "%", data => data.Length > 0 ? data[0] * 100.0 / 255.0 : 0);

    // Mode 09 - Vehicle information
    public static ObdPid Vin => new(0x09, 0x02, "VIN", "", null);
}

public record ObdPidResponse(
    ObdPid Pid,
    bool Success,
    double? Value,
    byte[]? RawBytes,
    string? Error
);

/// <summary>
/// Default implementation of IObdService using ELM327 adapter
/// </summary>
public class ObdService : IObdService
{
    private readonly IObdAdapter _adapter;
    private IObdTransport? _transport;

    public bool IsConnected => _transport?.IsConnected == true && _adapter.IsInitialized;

    public ObdService(IObdAdapter? adapter = null)
    {
        _adapter = adapter ?? new Elm327Adapter();
    }

    public async Task<bool> ConnectAsync(IObdTransport transport, CancellationToken cancellationToken = default)
    {
        _transport = transport;

        if (!transport.IsConnected)
        {
            if (!await transport.ConnectAsync(cancellationToken))
            {
                return false;
            }
        }

        return await _adapter.InitializeAsync(transport, cancellationToken);
    }

    public async Task DisconnectAsync()
    {
        if (_transport != null)
        {
            await _adapter.ResetAsync();
            await _transport.DisconnectAsync();
        }
    }

    public async Task<int?> GetRpmAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryPidAsync(ObdPid.Rpm, cancellationToken);
        return result.Success ? (int?)result.Value : null;
    }

    public async Task<int?> GetSpeedKphAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryPidAsync(ObdPid.VehicleSpeed, cancellationToken);
        return result.Success ? (int?)result.Value : null;
    }

    public async Task<double?> GetCoolantTempCelsiusAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryPidAsync(ObdPid.CoolantTemp, cancellationToken);
        return result.Success ? result.Value : null;
    }

    public async Task<double?> GetThrottlePositionPercentAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryPidAsync(ObdPid.ThrottlePosition, cancellationToken);
        return result.Success ? result.Value : null;
    }

    public async Task<double?> GetFuelLevelPercentAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryPidAsync(ObdPid.FuelLevel, cancellationToken);
        return result.Success ? result.Value : null;
    }

    public async Task<double?> GetEngineLoadPercentAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryPidAsync(ObdPid.EngineLoad, cancellationToken);
        return result.Success ? result.Value : null;
    }

    public async Task<string?> GetVinAsync(CancellationToken cancellationToken = default)
    {
        var response = await _adapter.SendCommandAsync(new ObdCommand("0902", TimeSpan.FromSeconds(10)), cancellationToken);
        if (!response.Success || string.IsNullOrEmpty(response.Value))
        {
            return null;
        }

        // VIN response is multi-line, decode hex to ASCII
        try
        {
            var hexData = response.Value.Replace(" ", "").Replace("\n", "");
            // Skip the response header bytes (49 02 01 for first line)
            // VIN is 17 characters
            var vinBytes = new List<byte>();
            for (var i = 0; i < hexData.Length - 1; i += 2)
            {
                if (byte.TryParse(hexData.Substring(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    if (b >= 0x20 && b <= 0x7E) // Printable ASCII
                    {
                        vinBytes.Add(b);
                    }
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

    public async Task<IReadOnlyList<string>> GetSupportedPidsAsync(CancellationToken cancellationToken = default)
    {
        var supported = new List<string>();

        // Query supported PIDs in groups of 32
        var response = await _adapter.SendCommandAsync(ObdCommand.Create("0100"), cancellationToken);
        if (response.Success && !string.IsNullOrEmpty(response.Value))
        {
            supported.AddRange(ParseSupportedPids(response.Value, 0x00));
        }

        // If PID 0x20 is supported, query next range
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

    public async Task<IReadOnlyList<string>> GetDtcCodesAsync(CancellationToken cancellationToken = default)
    {
        var codes = new List<string>();
        var response = await _adapter.SendCommandAsync(new ObdCommand("03", TimeSpan.FromSeconds(10)), cancellationToken);

        if (!response.Success || string.IsNullOrEmpty(response.Value))
        {
            return codes;
        }

        // Parse DTC codes from response
        // Format: 43 XX XX YY YY ... (pairs of bytes for each DTC)
        try
        {
            var hexData = response.Value.Replace(" ", "").Replace("\n", "");
            // Skip response header (43)
            if (hexData.StartsWith("43"))
            {
                hexData = hexData[2..];
            }

            for (var i = 0; i + 3 < hexData.Length; i += 4)
            {
                var dtcHex = hexData.Substring(i, 4);
                var dtc = DecodeDtc(dtcHex);
                if (!string.IsNullOrEmpty(dtc) && dtc != "P0000")
                {
                    codes.Add(dtc);
                }
            }
        }
        catch
        {
            // Parsing failed
        }

        return codes;
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
        {
            return new ObdPidResponse(pid, false, null, null, response.Error);
        }

        // Parse hex response to bytes
        var dataBytes = ParsePidResponse(response.Value ?? "", pid);
        if (dataBytes == null || dataBytes.Length == 0)
        {
            return new ObdPidResponse(pid, false, null, null, "Failed to parse response");
        }

        // Decode the value using the PID's decoder
        var value = pid.Decoder?.Invoke(dataBytes) ?? 0;
        return new ObdPidResponse(pid, true, value, dataBytes, null);
    }

    private static byte[]? ParsePidResponse(string response, ObdPid pid)
    {
        try
        {
            var hexData = response.Replace(" ", "").Replace("\n", "");

            // Response format: 4X YY DD DD... where X=mode, YY=pid, DD=data
            // Example: 410C 1AF8 for RPM query 010C
            var expectedHeader = $"{(pid.Mode + 0x40):X2}{pid.Pid:X2}";

            var headerIndex = hexData.IndexOf(expectedHeader, StringComparison.OrdinalIgnoreCase);
            if (headerIndex < 0)
            {
                return null;
            }

            // Skip header to get data bytes
            var dataHex = hexData[(headerIndex + expectedHeader.Length)..];

            var bytes = new List<byte>();
            for (var i = 0; i + 1 < dataHex.Length; i += 2)
            {
                if (byte.TryParse(dataHex.Substring(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    bytes.Add(b);
                }
            }

            return bytes.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> ParseSupportedPids(string response, byte baseOffset)
    {
        // Response is a 4-byte bitmap
        var hexData = response.Replace(" ", "").Replace("\n", "");

        // Skip header (e.g., 4100)
        if (hexData.Length >= 12)
        {
            hexData = hexData.Substring(4, 8);
        }
        else
        {
            yield break;
        }

        if (uint.TryParse(hexData, System.Globalization.NumberStyles.HexNumber, null, out var bitmap))
        {
            for (var i = 0; i < 32; i++)
            {
                if ((bitmap & (1u << (31 - i))) != 0)
                {
                    yield return $"01{(baseOffset + i + 1):X2}";
                }
            }
        }
    }

    private static string DecodeDtc(string hexBytes)
    {
        if (hexBytes.Length != 4)
            return string.Empty;

        if (!byte.TryParse(hexBytes[..2], System.Globalization.NumberStyles.HexNumber, null, out var byte1) ||
            !byte.TryParse(hexBytes[2..], System.Globalization.NumberStyles.HexNumber, null, out var byte2))
        {
            return string.Empty;
        }

        // First 2 bits determine prefix: 00=P, 01=C, 10=B, 11=U
        var prefixes = new[] { 'P', 'C', 'B', 'U' };
        var prefix = prefixes[(byte1 >> 6) & 0x03];
        var digit1 = (byte1 >> 4) & 0x03;
        var digit2 = byte1 & 0x0F;
        var digit3 = (byte2 >> 4) & 0x0F;
        var digit4 = byte2 & 0x0F;

        return $"{prefix}{digit1:X}{digit2:X}{digit3:X}{digit4:X}";
    }
}