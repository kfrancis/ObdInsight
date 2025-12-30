using System.Text.RegularExpressions;

namespace ObdInsight.Core;

/// <summary>
/// ELM327-compatible OBD adapter implementation.
/// Handles AT commands, protocol negotiation, and OBD-II command framing.
/// </summary>
public partial class Elm327Adapter : IObdAdapter
{
    private IObdTransport? _transport;
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _initTimeout = TimeSpan.FromSeconds(10);

    public string Name => "ELM327";
    public string[] SupportedDeviceNames => ["OBDII", "Veepeak", "ELM327", "OBDLink", "V-LINK"];
    public bool IsInitialized { get; private set; }

    public string? DeviceVersion { get; private set; }
    public string? ProtocolDescription { get; private set; }

    /// <summary>
    /// Event raised for raw communication logging
    /// </summary>
    public event EventHandler<Elm327LogEventArgs>? Log;

    public async Task<bool> InitializeAsync(IObdTransport transport, CancellationToken cancellationToken = default)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        IsInitialized = false;

        try
        {
            // Reset the adapter
            var resetResponse = await SendRawCommandAsync("ATZ", _initTimeout, cancellationToken);
            if (resetResponse.Contains("ELM327") || resetResponse.Contains("ELM329") || resetResponse.Contains("OBDII"))
            {
                DeviceVersion = ExtractVersion(resetResponse);
                RaiseLog(Elm327LogLevel.Info, $"Device identified: {DeviceVersion}");
            }
            else
            {
                RaiseLog(Elm327LogLevel.Warning, $"Unexpected reset response: {resetResponse}");
            }

            // Echo off (cleaner responses)
            await SendRawCommandAsync("ATE0", _defaultTimeout, cancellationToken);

            // Linefeed off
            await SendRawCommandAsync("ATL0", _defaultTimeout, cancellationToken);

            // Spaces off (compact responses)
            await SendRawCommandAsync("ATS0", _defaultTimeout, cancellationToken);

            // Headers off (just data)
            await SendRawCommandAsync("ATH0", _defaultTimeout, cancellationToken);

            // Set timeout to ~200ms per retry (adaptive timing will adjust)
            await SendRawCommandAsync("ATST32", _defaultTimeout, cancellationToken);

            // Enable adaptive timing (level 1)
            await SendRawCommandAsync("ATAT1", _defaultTimeout, cancellationToken);

            // Set protocol to automatic
            var protocolResponse = await SendRawCommandAsync("ATSP0", _defaultTimeout, cancellationToken);
            if (!protocolResponse.Contains("OK"))
            {
                RaiseLog(Elm327LogLevel.Warning, $"Protocol set response: {protocolResponse}");
            }

            // Try to connect to the vehicle ECU (longer timeout for protocol search)
            var ecuTimeout = TimeSpan.FromSeconds(30);
            var ecuResponse = await SendRawCommandAsync("0100", ecuTimeout, cancellationToken);

            if (ecuResponse.Contains("UNABLE TO CONNECT"))
            {
                RaiseLog(Elm327LogLevel.Warning, "No vehicle ECU detected (adapter not connected to vehicle or ignition off)");
                // Still mark as initialized - adapter works, just no vehicle connected
            }
            else if (ecuResponse.Contains("NO DATA") || ecuResponse.Contains("ERROR"))
            {
                RaiseLog(Elm327LogLevel.Warning, $"ECU communication issue: {ecuResponse.Replace("\n", " ")}");
            }
            else
            {
                // Get the detected protocol
                var dpResponse = await SendRawCommandAsync("ATDP", _defaultTimeout, cancellationToken);
                ProtocolDescription = dpResponse.Trim();
                RaiseLog(Elm327LogLevel.Info, $"Protocol: {ProtocolDescription}");
            }

            IsInitialized = true;
            return true;
        }
        catch (Exception ex)
        {
            RaiseLog(Elm327LogLevel.Error, $"Initialization failed: {ex.Message}");
            return false;
        }
    }

    public async Task<ObdResponse> SendCommandAsync(ObdCommand command, CancellationToken cancellationToken = default)
    {
        if (_transport == null || !_transport.IsConnected)
        {
            return ObdResponse.Fail("Transport not connected");
        }

        var timeout = command.CustomTimeout ?? _defaultTimeout;

        try
        {
            var rawResponse = await SendRawCommandAsync(command.Command, timeout, cancellationToken);

            // Check for common error responses
            if (string.IsNullOrWhiteSpace(rawResponse))
            {
                return ObdResponse.Fail("No response", rawResponse);
            }

            if (rawResponse.Contains("NO DATA"))
            {
                return ObdResponse.Fail("No data from ECU", rawResponse);
            }

            if (rawResponse.Contains("UNABLE TO CONNECT"))
            {
                return ObdResponse.Fail("Unable to connect to ECU", rawResponse);
            }

            if (rawResponse.Contains("ERROR"))
            {
                return ObdResponse.Fail("Command error", rawResponse);
            }

            if (rawResponse.Contains("?"))
            {
                return ObdResponse.Fail("Unknown command", rawResponse);
            }

            // Parse the response based on command type
            var parsedValue = ParseResponse(command.Command, rawResponse);
            return ObdResponse.Ok(parsedValue, rawResponse);
        }
        catch (TimeoutException)
        {
            return ObdResponse.Fail("Command timeout", null);
        }
        catch (Exception ex)
        {
            return ObdResponse.Fail($"Command failed: {ex.Message}", null);
        }
    }

    public async Task ResetAsync()
    {
        if (_transport?.IsConnected == true)
        {
            await SendRawCommandAsync("ATZ", _initTimeout, CancellationToken.None);
        }
        IsInitialized = false;
    }

    /// <summary>
    /// Send a raw command and get the response
    /// </summary>
    private async Task<string> SendRawCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport not set");

        // ELM327 commands end with carriage return
        var fullCommand = command.EndsWith('\r') ? command : command + "\r";

        RaiseLog(Elm327LogLevel.Debug, $"TX: {command}");

        await _transport.WriteAsync(fullCommand, cancellationToken);

        // Read until we get the prompt character '>'
        var response = await _transport.ReadUntilAsync(">", timeout, cancellationToken);

        // Clean up the response
        response = CleanResponse(response, command);

        RaiseLog(Elm327LogLevel.Debug, $"RX: {response}");

        return response;
    }

    /// <summary>
    /// Clean up ELM327 response by removing echo, prompt, and extra whitespace
    /// </summary>
    private static string CleanResponse(string response, string command)
    {
        if (string.IsNullOrEmpty(response))
            return string.Empty;

        // Remove the command echo if present
        var cleaned = response;
        if (cleaned.StartsWith(command, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[command.Length..];
        }

        // Remove prompt and clean whitespace
        cleaned = cleaned
            .Replace(">", "")
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Trim();

        return cleaned;
    }

    /// <summary>
    /// Parse OBD response based on command type
    /// </summary>
    private static string ParseResponse(string command, string rawResponse)
    {
        // For AT commands, return as-is
        if (command.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
        {
            return rawResponse;
        }

        // For OBD PIDs, strip the response header bytes
        // Response format: 4X YY [ZZ...] where X=mode+4, YY=PID
        // Example: command "010C" (RPM) -> response "410C 1AF8"
        var hexPattern = HexResponseRegex();
        var match = hexPattern.Match(rawResponse.Replace(" ", "").Replace("\n", ""));

        if (match.Success)
        {
            return match.Value;
        }

        return rawResponse;
    }

    private static string ExtractVersion(string response)
    {
        // Look for version pattern like "ELM327 v1.5" or "ELM327 v2.1"
        var versionPattern = VersionRegex();
        var match = versionPattern.Match(response);
        return match.Success ? match.Value : response.Trim();
    }

    private void RaiseLog(Elm327LogLevel level, string message)
    {
        Log?.Invoke(this, new Elm327LogEventArgs(level, message));
    }

    [GeneratedRegex(@"[0-9A-Fa-f]+")]
    private static partial Regex HexResponseRegex();

    [GeneratedRegex(@"ELM\d+\s*v[\d.]+", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
}

public enum Elm327LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public class Elm327LogEventArgs : EventArgs
{
    public Elm327LogLevel Level { get; }
    public string Message { get; }
    public DateTime Timestamp { get; }

    public Elm327LogEventArgs(Elm327LogLevel level, string message)
    {
        Level = level;
        Message = message;
        Timestamp = DateTime.UtcNow;
    }
}