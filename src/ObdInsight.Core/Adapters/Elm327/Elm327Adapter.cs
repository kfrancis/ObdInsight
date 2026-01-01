using System.Text;
using System.Text.RegularExpressions;

namespace ObdInsight.Core.Adapters.Elm327;

/// <summary>
/// ELM327-compatible OBD adapter implementation.
/// Handles AT commands, protocol negotiation, and OBD-II command framing.
/// </summary>
/// <remarks>
/// The ELM327 is the most common OBD-II interpreter chip. Many dongles use
/// genuine ELM327 chips or clones (some with varying compatibility).
///
/// This adapter handles:
/// - Initialization sequence (ATZ, ATE0, etc.)
/// - Protocol auto-detection or manual selection
/// - Response parsing and error detection
/// - Multi-frame response handling
/// </remarks>
public partial class Elm327Adapter : IObdAdapter
{
    private IObdTransport? _transport;
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _initTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _protocolSearchTimeout = TimeSpan.FromSeconds(45);

    private const int DefaultMaxAttempts = 3;
    private readonly TimeSpan _retryBaseDelay = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Known ELM327 error response patterns (case-insensitive).
    /// Note: SEARCHING is handled specially and not treated as a final error.
    /// </summary>
    private static readonly string[] ErrorPatterns =
    [
        "NO DATA",
        "UNABLE TO CONNECT",
        "CAN ERROR",
        "BUS ERROR",
        "FB ERROR",
        "RX ERROR",
        "DATA ERROR",
        "BUFFER FULL",
        "LV RESET",
        "LP ALERT",
        "ACT ALERT",
        "STOPPED",
        "ERR"
    ];

    /// <inheritdoc />
    public string Name => "ELM327";

    /// <inheritdoc />
    public string[] SupportedDeviceNames => ["OBDII", "Veepeak", "ELM327", "OBDLink", "V-LINK", "OBD"];

    /// <inheritdoc />
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// ELM327 firmware version string
    /// </summary>
    public string? DeviceVersion { get; private set; }

    /// <summary>
    /// Detected OBD protocol description
    /// </summary>
    public string? ProtocolDescription { get; private set; }

    /// <summary>
    /// Event raised for raw communication logging
    /// </summary>
    public event EventHandler<Elm327LogEventArgs>? Log;

    /// <summary>
    /// Sets the transport without doing a full initialization sequence.
    /// Use this when you've already done manual setup and just need the adapter
    /// to be able to send commands through the transport.
    /// </summary>
    /// <param name="transport">The transport to use</param>
    /// <param name="markAsInitialized">Whether to mark the adapter as initialized</param>
    public void SetTransport(IObdTransport transport, bool markAsInitialized = true)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        IsInitialized = markAsInitialized;
    }

    /// <inheritdoc />
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

            // Try to connect to the vehicle ECU
            // Use extended timeout for protocol search - this can take 30-45 seconds
            // The adapter sends "SEARCHING..." while trying different protocols
            RaiseLog(Elm327LogLevel.Info, "Searching for vehicle ECU (this may take up to 45 seconds)...");
            var ecuResponse = await SendProtocolSearchCommandAsync("0100", _protocolSearchTimeout, cancellationToken);

            if (ecuResponse.Contains("UNABLE TO CONNECT"))
            {
                RaiseLog(Elm327LogLevel.Warning, "No vehicle ECU detected (adapter not connected to vehicle or ignition off)");
                // Still mark as initialized - adapter works, just no vehicle connected
            }
            else if (ecuResponse.Contains("NO DATA") || ecuResponse.Contains("ERROR"))
            {
                RaiseLog(Elm327LogLevel.Warning, $"ECU communication issue: {ecuResponse.Replace("\n", " ")}");
            }
            else if (!string.IsNullOrWhiteSpace(ecuResponse))
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

    /// <inheritdoc />
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

            // Check for "?" (unknown command)
            if (rawResponse.Contains('?'))
            {
                return ObdResponse.Fail("Unknown command", rawResponse);
            }

            // Check for UDS negative response (7F XX YY)
            if (IsNegativeResponse(rawResponse))
            {
                return ObdResponse.Fail("Negative response from ECU", rawResponse);
            }

            // Check all known error patterns
            var errorPattern = GetMatchingErrorPattern(rawResponse);
            if (errorPattern != null)
            {
                return ObdResponse.Fail(GetErrorMessage(errorPattern), rawResponse);
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

    /// <inheritdoc />
    public async Task ResetAsync()
    {
        if (_transport?.IsConnected == true)
        {
            await SendRawCommandAsync("ATZ", _initTimeout, CancellationToken.None);
        }
        IsInitialized = false;
    }

    /// <summary>
    /// Check if response matches any known error pattern.
    /// </summary>
    private static string? GetMatchingErrorPattern(string response)
    {
        var upperResponse = response.ToUpperInvariant();

        foreach (var pattern in ErrorPatterns)
        {
            if (upperResponse.Contains(pattern))
            {
                // Special case: "BUS INIT: OK" is not an error
                if (pattern == "BUS ERROR" && upperResponse.Contains("BUS INIT") && upperResponse.Contains("OK"))
                {
                    continue;
                }

                return pattern;
            }
        }

        return null;
    }

    /// <summary>
    /// Check if response is a UDS negative response (7F XX YY format).
    /// </summary>
    private static bool IsNegativeResponse(string response)
    {
        // Match pattern like "7F 01 12" or "7F0112"
        var cleaned = response.Replace(" ", "").ToUpperInvariant();
        return NegativeResponseRegex().IsMatch(cleaned);
    }

    /// <summary>
    /// Get user-friendly error message for error pattern.
    /// </summary>
    private static string GetErrorMessage(string pattern)
    {
        return pattern switch
        {
            "NO DATA" => "No data from ECU",
            "UNABLE TO CONNECT" => "Unable to connect to ECU",
            "CAN ERROR" => "CAN bus error",
            "BUS ERROR" => "Bus communication error",
            "FB ERROR" => "Flow control error",
            "RX ERROR" => "Receive error",
            "DATA ERROR" => "Data error",
            "BUFFER FULL" => "Buffer overflow",
            "LV RESET" => "Low voltage reset",
            "LP ALERT" => "Low power alert",
            "ACT ALERT" => "Activity alert - no bus activity",
            "STOPPED" => "Command stopped",
            "ERR" => "Adapter error",
            _ => $"Error: {pattern}"
        };
    }

    /// <summary>
    /// Send a command that may trigger protocol search (like 0100).
    /// This handles the "SEARCHING..." intermediate response specially.
    /// </summary>
    private async Task<string> SendProtocolSearchCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport not set");

        return await ExecuteWithRetriesAsync(
            operationName: $"ProtocolSearch:{command}",
            maxAttempts: DefaultMaxAttempts,
            cancellationToken,
            async () =>
            {
                var fullCommand = command.EndsWith('\r') ? command : command + "\r";

                RaiseLog(Elm327LogLevel.Debug, $"TX: {command}");

                await _transport.WriteAsync(fullCommand, cancellationToken);

                // Read response, handling "SEARCHING..." specially
                var response = new StringBuilder();
                var startTime = DateTime.UtcNow;
                var searchingReported = false;
                var lastActivityTime = DateTime.UtcNow;

                // Use longer individual read timeouts during protocol search
                var readTimeout = TimeSpan.FromSeconds(10);

                while (DateTime.UtcNow - startTime < timeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!_transport.IsConnected)
                    {
                        throw new IOException("Transport disconnected");
                    }

                    try
                    {
                        var chunk = await _transport.ReadUntilAsync(">", readTimeout, cancellationToken);
                        lastActivityTime = DateTime.UtcNow;
                        response.Append(chunk);

                        var currentResponse = response.ToString();

                        if (currentResponse.Contains("SEARCHING", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!searchingReported)
                            {
                                RaiseLog(Elm327LogLevel.Info, "Protocol search in progress...");
                                searchingReported = true;
                            }

                            if (currentResponse.TrimEnd().EndsWith('>'))
                            {
                                break;
                            }

                            continue;
                        }

                        break;
                    }
                    catch (TimeoutException)
                    {
                        if (searchingReported)
                        {
                            var elapsed = DateTime.UtcNow - startTime;
                            RaiseLog(Elm327LogLevel.Debug, $"Still searching for protocol... ({elapsed.TotalSeconds:F0}s elapsed)");

                            var silenceTime = DateTime.UtcNow - lastActivityTime;
                            if (silenceTime > TimeSpan.FromSeconds(20))
                            {
                                throw new TimeoutException("No response for 20 seconds during protocol search");
                            }

                            continue;
                        }

                        if (response.Length > 0)
                        {
                            break;
                        }

                        throw;
                    }
                }

                var finalResponse = CleanResponse(response.ToString(), command);
                RaiseLog(Elm327LogLevel.Debug, $"RX: {finalResponse}");

                return finalResponse;
            }).ConfigureAwait(false);
    }

    private async Task<string> SendRawCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_transport == null)
            throw new InvalidOperationException("Transport not set");

        return await ExecuteWithRetriesAsync(
            operationName: $"Command:{command}",
            maxAttempts: DefaultMaxAttempts,
            cancellationToken,
            async () =>
            {
                if (!_transport.IsConnected)
                {
                    throw new IOException("Transport disconnected");
                }

                var fullCommand = command.EndsWith('\r') ? command : command + "\r";

                RaiseLog(Elm327LogLevel.Debug, $"TX: {command}");

                await _transport.WriteAsync(fullCommand, cancellationToken);

                var response = await _transport.ReadUntilAsync(">", timeout, cancellationToken);
                response = CleanResponse(response, command);

                RaiseLog(Elm327LogLevel.Debug, $"RX: {response}");

                // Some adapters occasionally return just a prompt; treat that as transient.
                if (string.IsNullOrWhiteSpace(response))
                {
                    throw new TimeoutException("Empty response");
                }

                return response;
            }).ConfigureAwait(false);
    }

    private async Task<T> ExecuteWithRetriesAsync<T>(
        string operationName,
        int maxAttempts,
        CancellationToken cancellationToken,
        Func<Task<T>> operation)
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < maxAttempts)
            {
                last = ex;
                var delay = TimeSpan.FromMilliseconds(_retryBaseDelay.TotalMilliseconds * attempt * attempt);
                RaiseLog(Elm327LogLevel.Warning, $"{operationName} transient failure (attempt {attempt}/{maxAttempts}): {ex.Message}. Retrying in {delay.TotalMilliseconds:F0}ms");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        throw last ?? new InvalidOperationException($"{operationName} failed");
    }

    private static bool IsTransient(Exception ex) =>
        ex is TimeoutException ||
        ex is IOException ||
        ex is InvalidOperationException;

    /// <summary>
    /// Clean up ELM327 response by removing echo, prompt, and extra whitespace
    /// </summary>
    private static string CleanResponse(string response, string command)
    {
        if (string.IsNullOrEmpty(response))
            return string.Empty;

        // Remove null bytes (some adapters have buffer issues)
        response = response.Replace("\0", "");

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

    [GeneratedRegex(@"^7F[0-9A-F]{4}")]
    private static partial Regex NegativeResponseRegex();
}

/// <summary>
/// Log levels for ELM327 adapter events
/// </summary>
public enum Elm327LogLevel
{
    /// <summary>Detailed debugging information</summary>
    Debug,

    /// <summary>General information</summary>
    Info,

    /// <summary>Potential issues</summary>
    Warning,

    /// <summary>Errors</summary>
    Error
}

/// <summary>
/// Event args for ELM327 log events
/// </summary>
public class Elm327LogEventArgs : EventArgs
{
    /// <summary>Log level</summary>
    public Elm327LogLevel Level { get; }

    /// <summary>Log message</summary>
    public string Message { get; }

    /// <summary>When the event occurred</summary>
    public DateTime Timestamp { get; }

    /// <summary>
    /// Creates a new log event
    /// </summary>
    public Elm327LogEventArgs(Elm327LogLevel level, string message)
    {
        Level = level;
        Message = message;
        Timestamp = DateTime.UtcNow;
    }
}