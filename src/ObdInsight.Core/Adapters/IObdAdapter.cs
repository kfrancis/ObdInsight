namespace ObdInsight.Core.Adapters;

/// <summary>
/// OBD adapter interface for protocol handling (ELM327, STN, etc.).
/// Adapters handle the translation between raw transport data and OBD commands.
/// </summary>
/// <remarks>
/// The adapter layer is responsible for:
/// - Protocol initialization (AT commands for ELM327)
/// - Command framing and response parsing
/// - Error detection and handling
/// - Timeout management
///
/// Adapters are independent of vehicle-specific logic - they only care about
/// sending commands and receiving responses reliably.
/// </remarks>
public interface IObdAdapter
{
    /// <summary>
    /// Whether the adapter has been initialized and is ready for commands
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Display name of the adapter (e.g., "ELM327", "STN1110")
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Device names this adapter can handle (for auto-detection)
    /// </summary>
    string[] SupportedDeviceNames { get; }

    /// <summary>
    /// Initialize the adapter over the given transport.
    /// </summary>
    /// <param name="transport">The transport to use for communication</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if initialization succeeded</returns>
    Task<bool> InitializeAsync(IObdTransport transport, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reset the adapter to its default state.
    /// </summary>
    Task ResetAsync();

    /// <summary>
    /// Send an OBD command and wait for the response.
    /// </summary>
    /// <param name="command">The command to send</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The response from the adapter/ECU</returns>
    Task<ObdResponse> SendCommandAsync(ObdCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents an OBD command to send to the adapter.
/// </summary>
/// <param name="Command">The command string (e.g., "010C" for RPM, "ATZ" for reset)</param>
/// <param name="CustomTimeout">Optional timeout override for this command</param>
public record ObdCommand(string Command, TimeSpan? CustomTimeout = null)
{
    /// <summary>
    /// Default timeout for commands (5 seconds)
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Create a command with default timeout
    /// </summary>
    public static ObdCommand Create(string command) => new(command);

    /// <summary>
    /// Create a command with custom timeout
    /// </summary>
    public static ObdCommand Create(string command, TimeSpan timeout) => new(command, timeout);

    /// <summary>
    /// The effective timeout for this command
    /// </summary>
    public TimeSpan Timeout => CustomTimeout ?? DefaultTimeout;
}

/// <summary>
/// Response from an OBD command.
/// </summary>
/// <param name="Success">Whether the command succeeded</param>
/// <param name="Value">The parsed response value (if successful)</param>
/// <param name="RawResponse">The raw response string from the adapter</param>
/// <param name="Error">Error message (if failed)</param>
public record ObdResponse(bool Success, string? Value, string? RawResponse, string? Error)
{
    /// <summary>
    /// Create a successful response
    /// </summary>
    public static ObdResponse Ok(string value, string rawResponse) => new(true, value, rawResponse, null);

    /// <summary>
    /// Create a failed response
    /// </summary>
    public static ObdResponse Fail(string error, string? rawResponse = null) => new(false, null, rawResponse, error);
}