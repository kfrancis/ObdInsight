using Serilog;
using ObdTestApp.Core.Protocols;
using System;

namespace ObdTestApp.Core.Communication.Elm327
{
    public interface IElmSession
    {
        TimeSpan CommandTimeout { get; set; }
        EcuCommunicationMode CurrentMode { get; }
        bool EnableDebugLogging { get; set; }
        int MaxConsecutiveFailures { get; set; }
        TimeSpan ProtocolDetectionTimeout { get; set; }

        ValueTask<bool> ActivateSessionAsync(EcuContext context, CancellationToken ct);
        ValueTask EnterMonitoringModeAsync(EcuContext context, CancellationToken ct);
        ValueTask ExitMonitoringModeAsync(CancellationToken ct);
        ValueTask InitializeAndLockAsync(CancellationToken ct);
        IAsyncEnumerable<RawCanFrame> MonitorFramesAsync(CancellationToken ct);
        ValueTask<string[]> QueryAsync(string obdCommand, CancellationToken ct);
        ValueTask<string[]> QueryAsync(string obdCommand, EcuContext context, CancellationToken ct);
        ValueTask SetEcuContextAsync(EcuContext context, CancellationToken ct);
    }

    /// <summary>
    /// Represents a session for communicating with an ELM-based OBD-II adapter, managing protocol initialization,
    /// command execution, and error recovery.
    /// </summary>
    /// <remarks>An ElmSession encapsulates the state and logic required to reliably interact with an ELM
    /// adapter, including protocol detection and locking, command timeouts, and automatic recovery from communication
    /// failures. Instances are not thread-safe; callers should not use the same ElmSession concurrently from multiple
    /// threads.</remarks>
    public sealed class ElmSession : IElmSession
    {
        private readonly ElmFramer _framer;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _failures;
        private char? _lockedProtocol;
        private EcuContext? _activeContext;
        private EcuCommunicationMode _currentMode = EcuCommunicationMode.RequestResponse;

        /// <summary>
        /// Initializes a new instance of the ElmSession class using the specified ELM framer.
        /// </summary>
        /// <param name="framer">The ElmFramer instance used to frame and parse ELM protocol messages. Cannot be null.</param>
        public ElmSession(ElmFramer framer) => _framer = framer;

        /// <summary>
        /// Gets or sets the maximum amount of time to wait for a command to execute before timing out.
        /// </summary>
        public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(4);

        /// <summary>
        /// Gets or sets the timeout for protocol detection commands (0100 probe).
        /// Protocol detection can take longer as the adapter searches through protocols.
        /// The ELM327 shows "SEARCHING..." during this time.
        /// </summary>
        public TimeSpan ProtocolDetectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Gets or sets the maximum number of consecutive failures allowed before triggering a failure response.
        /// </summary>
        public int MaxConsecutiveFailures { get; set; } = 3;

        /// <summary>
        /// Enable verbose debug logging to console (useful for troubleshooting connectivity issues).
        /// </summary>
        public bool EnableDebugLogging { get; set; }

        /// <summary>
        /// Gets the current communication mode of the session.
        /// </summary>
        public EcuCommunicationMode CurrentMode => _currentMode;


        /// <summary>
        /// Initializes the component and acquires an exclusive lock to prevent concurrent initialization.
        /// </summary>
        /// <remarks>This method ensures that initialization and protocol locking are performed
        /// atomically. If another operation is already in progress, this method waits until the lock is available.
        /// Callers should await the returned task to ensure initialization is complete before proceeding.</remarks>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous initialization and locking operation.</returns>
        public async ValueTask InitializeAndLockAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                Log("Starting ELM327 initialization...");
                await BaselineInitAsync(ct);
                Log("Baseline initialization complete");

                Log("Detecting and locking protocol...");
                await DetectAndLockProtocolAsync(ct);
                Log($"Protocol locked: {_lockedProtocol}");

                _failures = 0;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Sends an OBD command asynchronously and returns the response lines after normalization and validation.
        /// </summary>
        /// <remarks>If the initial response from the device is invalid, the method attempts to recover
        /// and retries the command once. The method is thread-safe and may block if another query is in
        /// progress.</remarks>
        /// <param name="obdCommand">The OBD command to send to the device. Cannot be null or empty.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A string array containing the normalized response lines from the OBD device. The array is guaranteed to be
        /// valid according to the device's response validation logic.</returns>
        /// <exception cref="IOException">Thrown if the OBD device fails to provide a valid response after a recovery attempt.</exception>
        public async ValueTask<string[]> QueryAsync(string obdCommand, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                Log($">>> QUERY START: {obdCommand}");

                var lines = await SendAndNormalizeAsync(obdCommand, ct);

                if (IsValid(lines))
                {
                    _failures = 0;
                    Log($"<<< QUERY SUCCESS: {obdCommand} returned {lines.Length} line(s): {string.Join(", ", lines)}");
                    return lines;
                }

                _failures++;
                Log($"<<< QUERY INVALID: {obdCommand} failure #{_failures} - Invalid response: {string.Join(", ", lines)}");

                if (_failures >= MaxConsecutiveFailures)
                {
                    Log($"Reached max consecutive failures ({MaxConsecutiveFailures}). Attempting recovery...");
                    await RecoverAsync(ct);
                    _failures = 0;
                    Log("Recovery complete");
                }

                // retry once after (possible) recovery
                Log("Retrying query after recovery...");
                lines = await SendAndNormalizeAsync(obdCommand, ct);

                if (!IsValid(lines))
                {
                    Log($"<<< QUERY FAILED: {obdCommand} - Failed after recovery. Response: {string.Join(", ", lines)}");
                    throw new IOException($"ELM query '{obdCommand}' failed after recovery. Last response had {lines.Length} line(s).");
                }

                Log($"<<< QUERY SUCCESS (retry): {obdCommand} returned {lines.Length} line(s): {string.Join(", ", lines)}");
                return lines;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Configures the ELM adapter for communication with a specific ECU.
        /// </summary>
        /// <remarks>This method sets up CAN headers, receive filters, and ISO-TP flow control
        /// for the specified ECU. Configuration is cached and skipped if already active.</remarks>
        /// <param name="context">The ECU context containing headers and flow control settings.</param>
        /// <param name="ct">Cancellation token.</param>
        public async ValueTask SetEcuContextAsync(EcuContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);

            // Enforce that monitoring contexts must use EnterMonitoringModeAsync
            if (context.CommunicationMode == EcuCommunicationMode.PassiveMonitoring ||
                context.CommunicationMode == EcuCommunicationMode.ActiveMonitoring ||
                context.CommunicationMode == EcuCommunicationMode.FilteredMonitoring)
                throw new InvalidOperationException($"Use EnterMonitoringModeAsync() for monitoring contexts.");

            await _gate.WaitAsync(ct);
            try
            {
                // Always reset state before reconfiguring (even if same context name)
                // This prevents filter pollution from previous operations
                await ResetAdapterStateAsync(ct);

                Log($"Configuring ECU context: {context.Name}");

                await ConfigureEcuContextInternalAsync(context, ct);

                Log($"ECU context '{context.Name}' configured successfully");
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Sends an OBD command to a specific ECU context asynchronously.
        /// Automatically configures the adapter if needed.
        /// </summary>
        /// <remarks>This method first ensures the ECU context is configured, then executes the query.
        /// If the initial response is invalid, it attempts recovery and retries once.</remarks>
        /// <param name="obdCommand">The OBD command to send to the device. Cannot be null or empty.</param>
        /// <param name="context">The ECU context to use for this query.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A string array containing the normalized response lines from the ECU.</returns>
        /// <exception cref="IOException">Thrown if the ECU fails to provide a valid response after a recovery attempt.</exception>
        public async ValueTask<string[]> QueryAsync(string obdCommand, EcuContext context, CancellationToken ct)
        {
            // Enforce mode checking - cannot query while in monitoring mode
            if (_currentMode == EcuCommunicationMode.PassiveMonitoring)
                throw new InvalidOperationException("Cannot query while in monitoring mode. Call ExitMonitoringModeAsync() first.");

            // Configure context if needed (automatically handles switching between ECUs)
            await SetEcuContextAsync(context, ct);

            // Execute query using the existing QueryAsync method
            return await QueryAsync(obdCommand, ct);
        }

        /// <summary>
        /// Activates a diagnostic session with the specified ECU.
        /// Required for some ECUs before they will respond to queries or broadcast data.
        /// </summary>
        /// <param name="context">The ECU context with session configuration.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if session was activated (or no activation required), false if activation failed.</returns>
        public async ValueTask<bool> ActivateSessionAsync(EcuContext context, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(context.SessionActivationCommand))
            {
                Log($"No session activation required for {context.Name}");
                return true;
            }

            await _gate.WaitAsync(ct);
            try
            {
                Log($"Activating session for {context.Name}: {context.SessionActivationCommand}");

                // Ensure ECU context is configured
                if (_activeContext?.Name != context.Name)
                {
                    await ResetAdapterStateAsync(ct);
                    await ConfigureEcuContextInternalAsync(context, ct);
                }

                // Send session activation command
                var response = await SendAndNormalizeAsync(context.SessionActivationCommand, ct);

                // Interpret response
                // Positive response: 50 xx (session activated)
                // Negative response: 7F 10 xx (still useful as proof-of-life)
                // No response: May be expected for suppress-positive-response (0x81)

                var hasPositiveResponse = response.Any(line =>
                    line.Contains("50", StringComparison.OrdinalIgnoreCase));
                var hasNegativeResponse = response.Any(line =>
                    line.Contains("7F", StringComparison.OrdinalIgnoreCase));
                var isSuppressPositive = context.SessionActivationCommand.EndsWith("81", StringComparison.OrdinalIgnoreCase) ||
                                          context.SessionActivationCommand.EndsWith("C0", StringComparison.OrdinalIgnoreCase);

                if (hasPositiveResponse)
                {
                    Log($"Session activated successfully for {context.Name}");
                    return true;
                }
                else if (hasNegativeResponse)
                {
                    // Negative response still indicates ECU is alive and communicating
                    Log($"Session activation received negative response for {context.Name} (ECU is responsive)");
                    return true;
                }
                else if (isSuppressPositive && !response.Any(ElmParsing.LooksLikeAdapterError))
                {
                    // Suppress-positive-response bit set - no response is expected
                    Log($"Session activation sent (suppress-positive-response) for {context.Name}");
                    return true;
                }
                else
                {
                    Log($"Session activation failed for {context.Name}: {string.Join(", ", response)}");
                    return false;
                }
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Internal method to configure ECU context without acquiring gate (caller must hold gate).
        /// </summary>
        private async ValueTask ConfigureEcuContextInternalAsync(EcuContext context, CancellationToken ct)
        {
            // Configure headers and formatting
            await _framer.SendAndReadFrameAsync($"AT H{(context.EnableHeaders ? "1" : "0")}", CommandTimeout, ct);
            await _framer.SendAndReadFrameAsync($"AT CAF{(context.EnableAutoFormatting ? "1" : "0")}", CommandTimeout, ct);

            // Set CAN headers
            if (!string.IsNullOrEmpty(context.TxHeader) && context.TxHeader != "000")
                await _framer.SendAndReadFrameAsync($"AT SH {context.TxHeader}", CommandTimeout, ct);
            if (!string.IsNullOrEmpty(context.RxFilter) && context.RxFilter != "000")
                await _framer.SendAndReadFrameAsync($"AT CRA {context.RxFilter}", CommandTimeout, ct);

            // Configure ISO-TP flow control
            if (!string.IsNullOrEmpty(context.FlowControlHeader))
                await _framer.SendAndReadFrameAsync($"AT FC SH {context.FlowControlHeader}", CommandTimeout, ct);
            if (!string.IsNullOrEmpty(context.FlowControlData))
                await _framer.SendAndReadFrameAsync($"AT FC SD {context.FlowControlData}", CommandTimeout, ct);
            if (!string.IsNullOrEmpty(context.FlowControlMode))
                await _framer.SendAndReadFrameAsync($"AT FC SM {context.FlowControlMode}", CommandTimeout, ct);

            // Set adapter timeout if specified
            if (context.AdapterTimeoutUnits > 0)
                await _framer.SendAndReadFrameAsync($"AT ST {context.AdapterTimeoutUnits:X2}", CommandTimeout, ct);

            _activeContext = context;
            Log($"ECU context '{context.Name}' configured");
        }

        private static bool IsValid(string[] lines)
        => lines.Length > 0 && !lines.Any(ElmParsing.LooksLikeAdapterError);

        /// <summary>
        /// Resets ELM327 filter and addressing state to known baseline.
        /// Must be called before reconfiguring for a different ECU.
        /// </summary>
        private async ValueTask ResetAdapterStateAsync(CancellationToken ct)
        {
            Log("Resetting adapter state (ATAR, ATCEA, ATAR)");

            // Clear any receive address filter
            await _framer.SendAndReadFrameAsync("AT AR", CommandTimeout, ct);

            // Disable extended addressing (baseline for 11-bit ISO-TP)
            await _framer.SendAndReadFrameAsync("AT CEA", CommandTimeout, ct);

            // Reset address-related filtering state
            // Note: Some adapters use ATAR differently - this clears CRA filters
            await _framer.SendAndReadFrameAsync("AT AR", CommandTimeout, ct);
        }

        private async ValueTask BaselineInitAsync(CancellationToken ct)
        {
            // Keep these idempotent.
            Log("Baseline init: AT Z (reset)");
            await _framer.SendAndReadFrameAsync("AT Z", CommandTimeout, ct);

            // CRITICAL: Wait after reset for adapter to be ready
            // Many adapters (especially cheap clones) need time after ATZ before accepting commands
            Log("Waiting 500ms for adapter to stabilize after reset...");
            await Task.Delay(500, ct);

            // AT D (restore defaults) - some cheap clones don't support this, so make it optional
            Log("Baseline init: AT D (restore defaults) - optional");
            try
            {
                await _framer.SendAndReadFrameAsync("AT D", TimeSpan.FromSeconds(2), ct);
            }
            catch (Exception ex)
            {
                Log($"AT D command failed (adapter may not support it): {ex.Message}");
            }

            Log("Baseline init: AT E0 (echo off)");
            await _framer.SendAndReadFrameAsync("AT E0", CommandTimeout, ct);

            Log("Baseline init: AT L0 (linefeeds off)");
            await _framer.SendAndReadFrameAsync("AT L0", CommandTimeout, ct);

            Log("Baseline init: AT S0 (spaces off)");
            await _framer.SendAndReadFrameAsync("AT S0", CommandTimeout, ct);

            // Headers ON is required for proper CAN communication with Nissan Leaf and many EVs
            // The response needs the CAN ID prefix to identify which ECU responded
            Log("Baseline init: AT H1 (headers on)");
            await _framer.SendAndReadFrameAsync("AT H1", CommandTimeout, ct);

            // CAN auto-formatting off - required for proper ISO-TP multi-frame handling
            // Some ELM327 clones have issues with auto-formatting enabled
            Log("Baseline init: AT CAF0 (CAN auto-formatting off)");
            await _framer.SendAndReadFrameAsync("AT CAF0", CommandTimeout, ct);

            Log("Baseline init: AT AT1 (adaptive timing auto1)");
            await _framer.SendAndReadFrameAsync("AT AT1", CommandTimeout, ct);
        }

        private async ValueTask DetectAndLockProtocolAsync(CancellationToken ct)
        {
            // First, try to wake up the ECUs by sending a broadcast query
            // Many vehicles (especially EVs like Nissan Leaf) have ECUs that sleep
            // Sending to broadcast address 7DF wakes them up
            Log("Sending broadcast wakeup sequence...");
            await TryWakeupEcusAsync(ct);

            // If we already locked the protocol during wakeup (e.g., Nissan Leaf BMS responded),
            // verify it works and return early
            if (_lockedProtocol is not null)
            {
                Log($"Protocol already locked to {_lockedProtocol} during wakeup - verifying...");
                // Reset headers to default for standard OBD queries
                await _framer.SendAndReadFrameAsync("AT SH 7DF", CommandTimeout, ct);
                // For Nissan Leaf, 0100 won't work, but the protocol is already confirmed
                Log($"Protocol {_lockedProtocol} locked (EV-CAN mode - standard OBD-II queries may not work)");
                return;
            }

            // Try known protocols first before auto-detect
            // This is faster and more reliable for known vehicles
            // Protocol 6 = ISO 15765-4 CAN (11-bit, 500kbps) - most modern vehicles including Nissan Leaf
            // Protocol 7 = ISO 15765-4 CAN (29-bit, 500kbps) - some vehicles
            // Protocol 8 = ISO 15765-4 CAN (11-bit, 250kbps) - rare
            // Protocol 9 = ISO 15765-4 CAN (29-bit, 250kbps) - rare

            var protocolsToTry = new[]
            {
                ('6', "ISO 15765-4 CAN 11-bit 500k"),  // Most common - Nissan Leaf, most modern cars
                ('7', "ISO 15765-4 CAN 29-bit 500k"),  // Some vehicles
                ('0', "Auto-detect"),                   // Fallback to auto-detect
            };

            foreach (var (protocol, description) in protocolsToTry)
            {
                Log($"Trying protocol {protocol}: {description}");

                try
                {
                    await _framer.SendAndReadFrameAsync($"AT SP {protocol}", CommandTimeout, ct);

                    // For auto-detect, use longer timeout as it shows "SEARCHING..."
                    var timeout = protocol == '0' ? ProtocolDetectionTimeout : TimeSpan.FromSeconds(5);

                    Log($"Probing with 0100 (timeout: {timeout.TotalSeconds}s)...");
                    var probe = await SendAndNormalizeAsync("0100", timeout, ct);

                    if (IsValid(probe))
                    {
                        Log($"Protocol {protocol} successful!");

                        // If we used auto-detect, query what protocol was detected
                        var lockedProto = protocol;
                        if (protocol == '0')
                        {
                            var dpn = ElmParsing.NormalizeLines(await _framer.SendAndReadFrameAsync("AT DPN", CommandTimeout, ct))
                                .FirstOrDefault() ?? string.Empty;
                            Log($"AT DPN response: '{dpn}'");
                            var detectedProto = dpn.Trim().TrimStart('A', 'a').FirstOrDefault();
                            if (detectedProto != '\0')
                            {
                                lockedProto = detectedProto;
                                // Lock to the detected protocol
                                await _framer.SendAndReadFrameAsync($"AT SP {lockedProto}", CommandTimeout, ct);
                            }
                        }

                        _lockedProtocol = lockedProto;
                        Log($"Protocol locked to: {_lockedProtocol}");

                        // Verify the lock works
                        Log("Verifying protocol lock with 0100 probe...");
                        probe = await SendAndNormalizeAsync("0100", TimeSpan.FromSeconds(5), ct);
                        if (IsValid(probe))
                        {
                            Log("Protocol lock verified");
                            return;
                        }

                        Log("Protocol lock verification failed, trying next protocol...");
                    }
                    else
                    {
                        Log($"Protocol {protocol} returned invalid response, trying next...");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Protocol {protocol} failed with error: {ex.Message}");
                }
            }

            // If we get here, all protocols failed
            throw new IOException("All protocol detection attempts failed. Check vehicle connection and ensure ignition is ON.");
        }

        /// <summary>
        /// Attempts to wake up sleeping ECUs before protocol detection.
        /// Many vehicles (especially EVs) have ECUs that sleep when the car is off.
        /// Based on OVMS (Open Vehicle Monitoring System) wakeup sequences.
        /// </summary>
        private async ValueTask TryWakeupEcusAsync(CancellationToken ct)
        {
            try
            {
                // Set protocol to CAN 11-bit 500k (Protocol 6) for wakeup
                Log("Wakeup: Setting protocol 6 (CAN 11-bit 500k)");
                await _framer.SendAndReadFrameAsync("AT SP 6", CommandTimeout, ct);

                // Set header to broadcast address (0x7DF) - all ECUs listen to this
                Log("Wakeup: Setting broadcast header (7DF)");
                await _framer.SendAndReadFrameAsync("AT SH 7DF", CommandTimeout, ct);

                // Send Mode 01 PID 00 (supported PIDs) to wake all ECUs
                // This is a standard OBD-II query that all compliant ECUs respond to
                Log("Wakeup: Sending broadcast 0100 query...");
                var response = await _framer.SendAndReadFrameAsync("0100", TimeSpan.FromSeconds(5), ct);
                var lines = ElmParsing.NormalizeLines(response);

                if (lines.Any(l => !ElmParsing.LooksLikeAdapterError(l) && l.Length > 5))
                {
                    Log($"Wakeup: ECU responded! Response: {string.Join(", ", lines.Take(2))}");
                }
                else
                {
                    Log($"Wakeup: No standard OBD-II response - trying Nissan Leaf BMS...");
                    // Nissan Leaf doesn't respond to standard OBD-II queries
                    // Try the BMS directly with Mode 21
                    await TryNissanLeafBmsAsync(ct);
                }

                // Small delay for ECUs to wake up
                await Task.Delay(500, ct);

                // Reset to auto-protocol for detection phase
                Log("Wakeup: Resetting to auto-protocol");
                await _framer.SendAndReadFrameAsync("AT SP 0", CommandTimeout, ct);
            }
            catch (Exception ex)
            {
                Log($"Wakeup sequence error (non-fatal): {ex.Message}");
                // Don't throw - wakeup is best-effort
            }
        }

        /// <summary>
        /// Try to communicate with Nissan Leaf BMS using Mode 21 manufacturer-specific commands.
        /// The Leaf doesn't respond to standard OBD-II Mode 01 queries - it uses EV-CAN.
        /// </summary>
        private async ValueTask<bool> TryNissanLeafBmsAsync(CancellationToken ct)
        {
            try
            {
                // Configure for Nissan Leaf BMS communication
                // BMS TX: 0x79B, BMS RX: 0x7BB
                Log("Trying Nissan Leaf BMS (79B/7BB)...");

                await _framer.SendAndReadFrameAsync("AT SH 79B", CommandTimeout, ct);
                await _framer.SendAndReadFrameAsync("AT CRA 7BB", CommandTimeout, ct);
                await _framer.SendAndReadFrameAsync("AT FC SH 79B", CommandTimeout, ct);
                await _framer.SendAndReadFrameAsync("AT FC SD 30 00 00", CommandTimeout, ct);
                await _framer.SendAndReadFrameAsync("AT FC SM 1", CommandTimeout, ct);

                // Send Mode 21 Group 01 query (BMS SOC, Capacity, etc.)
                Log("Sending Mode 21 Group 01 query (2101)...");
                var response = await _framer.SendAndReadFrameAsync("2101", TimeSpan.FromSeconds(5), ct);
                var lines = ElmParsing.NormalizeLines(response);

                // Check if we got a valid response (should contain 7BB prefix)
                if (lines.Any(l => l.Contains("7BB") && l.Length > 10))
                {
                    Log($"Nissan Leaf BMS responded! Response: {string.Join(", ", lines.Take(2))}");
                    _lockedProtocol = '6'; // Lock to Protocol 6 (CAN 11-bit 500k)
                    return true;
                }

                Log("No Nissan Leaf BMS response");
                return false;
            }
            catch (Exception ex)
            {
                Log($"Nissan Leaf BMS probe failed: {ex.Message}");
                return false;
            }
        }

        private async ValueTask RecoverAsync(CancellationToken ct)
        {
            Log("Recovery Level 0: Attempting parser resync...");
            if (await TryResyncAsync(ct))
            {
                Log("Recovery successful at Level 0 (parser resync)");
                return;
            }

            Log("Recovery Level 1: Attempting protocol resync...");
            if (await TryProtocolResyncAsync(ct))
            {
                Log("Recovery successful at Level 1 (protocol resync)");
                return;
            }

            Log("Recovery Level 2: Reapplying baseline + locked protocol...");
            await BaselineInitAsync(ct);
            if (_lockedProtocol is not null)
            {
                Log($"Reapplying locked protocol: {_lockedProtocol}");
                await _framer.SendAndReadFrameAsync($"AT SP {_lockedProtocol}", CommandTimeout, ct);
            }
            if (await TryProbeAsync(ct))
            {
                Log("Recovery successful at Level 2 (baseline + protocol)");
                return;
            }

            Log("Recovery Level 3: Hard reset and full re-detect...");
            await BaselineInitAsync(ct);
            await DetectAndLockProtocolAsync(ct);
            Log("Recovery successful at Level 3 (full reset)");
        }

        private async ValueTask<string[]> SendAndNormalizeAsync(string cmd, CancellationToken ct)
            => await SendAndNormalizeAsync(cmd, CommandTimeout, ct);

        private async ValueTask<string[]> SendAndNormalizeAsync(string cmd, TimeSpan timeout, CancellationToken ct)
        {
            var frame = await _framer.SendAndReadFrameAsync(cmd, timeout, ct);
            return ElmParsing.NormalizeLines(frame);
        }

        private async ValueTask<bool> TryProbeAsync(CancellationToken ct)
        {
            try
            {
                // Use longer timeout for protocol probes as they may show "SEARCHING..."
                var probe = await SendAndNormalizeAsync("0100", ProtocolDetectionTimeout, ct);
                return IsValid(probe);
            }
            catch { return false; }
        }

        private async ValueTask<bool> TryProtocolResyncAsync(CancellationToken ct)
        {
            try
            {
                await _framer.SendAndReadFrameAsync("AT PC", CommandTimeout, ct);
                if (_lockedProtocol is not null)
                    await _framer.SendAndReadFrameAsync($"AT SP {_lockedProtocol}", CommandTimeout, ct);

                return await TryProbeAsync(ct);
            }
            catch { return false; }
        }

        private async ValueTask<bool> TryResyncAsync(CancellationToken ct)
        {
            try
            {
                var frame = await _framer.SendAndReadFrameAsync("", TimeSpan.FromSeconds(2), ct); // sends ju
                return ElmParsing.NormalizeLines(frame).Length >= 0; // If we got here, we got a prompt.
            }
            catch { return false; }
        }

        /// <summary>
        /// Enters passive monitoring mode for the specified ECU context.
        /// </summary>
        /// <remarks>
        /// This is a state transition. Once in monitoring mode, you cannot send queries
        /// until you call ExitMonitoringModeAsync(). Use MonitorFramesAsync() to read frames.
        /// </remarks>
        /// <param name="context">The ECU context configured for passive monitoring.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <exception cref="InvalidOperationException">Thrown if the context is not configured for passive monitoring.</exception>
        public async ValueTask EnterMonitoringModeAsync(EcuContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.CommunicationMode != EcuCommunicationMode.PassiveMonitoring &&
                context.CommunicationMode != EcuCommunicationMode.ActiveMonitoring &&
                context.CommunicationMode != EcuCommunicationMode.FilteredMonitoring)
                throw new InvalidOperationException($"ECU context '{context.Name}' does not support monitoring modes.");

            await _gate.WaitAsync(ct);
            try
            {
                if (_currentMode == EcuCommunicationMode.PassiveMonitoring)
                {
                    Log("Already in monitoring mode - exiting first");
                    await ExitMonitoringModeInternalAsync(ct);
                }

                Log($"Entering monitoring mode: {context.Name}");

                // CRITICAL: Reset adapter state before monitoring configuration
                await ResetAdapterStateAsync(ct);

                // Configure headers/formatting
                await _framer.SendAndReadFrameAsync($"AT H{(context.EnableHeaders ? "1" : "0")}", CommandTimeout, ct);
                await _framer.SendAndReadFrameAsync($"AT CAF{(context.EnableAutoFormatting ? "1" : "0")}", CommandTimeout, ct);

                // Enable spaces for monitoring - required for parsing (baseline init sets AT S0)
                await _framer.SendAndReadFrameAsync("AT S1", CommandTimeout, ct);

                // Configure CAN filter to prevent buffer overflow
                if (!string.IsNullOrEmpty(context.CanFilterMask) && !string.IsNullOrEmpty(context.CanFilterPattern))
                {
                    Log($"Setting CAN filter - Mask: {context.CanFilterMask}, Pattern: {context.CanFilterPattern}");
                    await _framer.SendAndReadFrameAsync($"AT CM {context.CanFilterMask}", CommandTimeout, ct);
                    await _framer.SendAndReadFrameAsync($"AT CF {context.CanFilterPattern}", CommandTimeout, ct);
                }
                else
                {
                    //// No filter specified - accept all frames (may cause BUFFER FULL on busy CAN buses)
                    Log("No CAN filter specified (accept all frames)");
                    await _framer.SendAndReadFrameAsync("AT AR", CommandTimeout, ct);
                }

                // Enter monitoring mode
                if (!string.IsNullOrEmpty(context.MonitoringCommand))
                {
                    Log($"Sending monitoring command: {context.MonitoringCommand}");
                    // Note: Monitoring mode doesn't return "OK" - it starts streaming immediately
                    //await _framer.SendAndReadFrameAsync(context.MonitoringCommand, CommandTimeout, ct);
                    await _framer.WriteAsync(context.MonitoringCommand + "\r", ct);
                    await Task.Delay(100, ct); // Give ELM327 time to enter monitoring mode
                }

                _currentMode = EcuCommunicationMode.PassiveMonitoring;
                _activeContext = context;
                Log($"Monitoring mode active: {context.Name}");
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        /// Exits passive monitoring mode and returns to request/response mode.
        /// Safe to call even if already in request/response mode (will reset ELM327 state).
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async ValueTask ExitMonitoringModeAsync(CancellationToken ct)
        {
            // Use TryWaitAsync with timeout to avoid blocking if gate is held
            if (!await _gate.WaitAsync(TimeSpan.FromSeconds(2), ct))
            {
                Log("Warning: Could not acquire gate for ExitMonitoringMode - forcing state change");
                _currentMode = EcuCommunicationMode.RequestResponse;
                _activeContext = null;
                return;
            }

            try
            {
                await ExitMonitoringModeInternalAsync(ct);
            }
            finally { _gate.Release(); }
        }

        private async ValueTask ExitMonitoringModeInternalAsync(CancellationToken ct)
        {
            // Note: _currentMode may already be RequestResponse if monitoring exited early
            // (e.g., due to BUFFER FULL). We still need to clean up the ELM327 state.
            var wasInMonitoringMode = _currentMode == EcuCommunicationMode.PassiveMonitoring;

            Log($"Exiting monitoring mode (wasInMonitoringMode={wasInMonitoringMode})");

            try
            {
                // Clear the buffer first - there may be residual data from monitoring
                _framer.ClearBuffer();

                // Send CR to exit monitoring mode (if device is still in it)
                // Even if already exited, this is harmless
                await _framer.WriteAsync("\r", CancellationToken.None);

                // Short delay for device to process exit
                try
                {
                    await Task.Delay(100, ct);
                }
                catch (OperationCanceledException)
                {
                    // User cancelled during delay - that's OK, continue cleanup
                    Log("Cancellation during exit delay - continuing cleanup");
                }

                // Clear buffer again after sending CR
                _framer.ClearBuffer();

                // Drain any remaining data until we see the prompt
                Log("Draining monitoring buffer...");
                var drainStartTime = DateTime.UtcNow;
                var maxDrainTime = TimeSpan.FromMilliseconds(500);

                while (DateTime.UtcNow - drainStartTime < maxDrainTime && !ct.IsCancellationRequested)
                {
                    try
                    {
                        var residual = await _framer.ReadUntilAsync(">", TimeSpan.FromMilliseconds(100), CancellationToken.None);
                        if (!string.IsNullOrEmpty(residual))
                        {
                            Log($"Drained: '{residual[..Math.Min(50, residual.Length)]}...'");
                        }
                        // Got prompt, buffer is drained
                        Log("Buffer drain complete (got prompt)");
                        break;
                    }
                    catch (TimeoutException)
                    {
                        // No more data - buffer is drained
                        Log("Buffer drain complete (timeout - buffer empty)");
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        Log("Buffer drain cancelled");
                        break;
                    }
                }

                // Final buffer clear
                _framer.ClearBuffer();

                // Reset ELM327 to request/response mode
                Log("Resetting ELM327 to query mode");
                var quickTimeout = TimeSpan.FromMilliseconds(500);

                try
                {
                    // Send commands to reset ELM327 state
                    await _framer.SendAndReadFrameAsync("AT AR", quickTimeout, CancellationToken.None);
                    await _framer.SendAndReadFrameAsync("AT SH 7DF", quickTimeout, CancellationToken.None);
                    await _framer.SendAndReadFrameAsync("AT CRA", quickTimeout, CancellationToken.None);
                    await _framer.SendAndReadFrameAsync("AT S0", quickTimeout, CancellationToken.None);
                    await _framer.SendAndReadFrameAsync("AT CAF0", quickTimeout, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Don't let AT command failures block cleanup
                    Log($"Warning: AT command failed during cleanup: {ex.Message}");
                }

                _currentMode = EcuCommunicationMode.RequestResponse;
                _activeContext = null;
                Log("Returned to request/response mode");
            }
            catch (Exception ex)
            {
                Log($"Error exiting monitoring mode: {ex.Message}");
                // Force mode change even on error
                _currentMode = EcuCommunicationMode.RequestResponse;
                _activeContext = null;
                throw;
            }
        }

        /// <summary>
        /// Reads CAN frames while in monitoring mode.
        /// </summary>
        /// <remarks>
        /// Must be called after EnterMonitoringModeAsync(). Returns parsed CAN frames
        /// as they arrive. Use with a loop and cancellation token to continuously monitor.
        /// </remarks>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>An async enumerable of CAN frames.</returns>
        /// <exception cref="InvalidOperationException">Thrown if not in monitoring mode.</exception>
        /// <exception cref="IOException">Thrown if ELM327 buffer overflows (BUFFER FULL).</exception>
        public async IAsyncEnumerable<RawCanFrame> MonitorFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (_currentMode != EcuCommunicationMode.PassiveMonitoring)
                throw new InvalidOperationException("Not in monitoring mode. Call EnterMonitoringModeAsync() first.");

            while (!ct.IsCancellationRequested)
            {
                // Check cancellation at the start of each iteration
                if (ct.IsCancellationRequested)
                {
                    Log("Monitoring cancelled (token check)");
                    yield break;
                }

                string? rawData = null;
                var hasData = false;

                try
                {
                    // Read raw data from framer (no prompt expected in monitoring mode)
                    rawData = await _framer.ReadUntilAsync("\r", TimeSpan.FromMilliseconds(500), ct);
                    hasData = !string.IsNullOrWhiteSpace(rawData);
                }
                catch (TimeoutException)
                {
                    // Normal - no data available, check cancellation and continue
                    continue;
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelled
                    Log("Monitoring cancelled");
                    yield break;
                }

                if (hasData && rawData != null)
                {
                    // Some ELM327 adapters emit an initial error line after starting monitoring (e.g. "TA ERROR").
                    // This is not a CAN frame and should not be treated as a failure.
                    if (rawData.Contains("TA ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"Monitoring transient adapter error (ignored): {rawData.Trim()}");
                        continue;
                    }

                    // Check for ELM327 error conditions that terminate monitoring
                    if (rawData.Contains("BUFFER FULL", StringComparison.OrdinalIgnoreCase))
                    {
                        Log("ELM327 buffer overflow detected - monitoring terminated by device");
                        _currentMode = EcuCommunicationMode.RequestResponse; // Device has exited monitoring mode
                        // Exit gracefully instead of throwing - this allows the session to continue
                        // The caller can check the frame count to know monitoring ended early
                        yield break;
                    }

                    // Check for prompt character indicating ELM327 exited monitoring mode
                    if (rawData.Contains('>'))
                    {
                        Log("Prompt detected - ELM327 has exited monitoring mode");
                        _currentMode = EcuCommunicationMode.RequestResponse;
                        yield break;
                    }

                    // Check for other error conditions
                    if (rawData.Contains("CAN ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"CAN ERROR detected in monitoring mode: {rawData}");
                        continue; // Continue monitoring, but skip this frame
                    }

                    if (rawData.Contains("DATA ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"DATA ERROR detected in monitoring mode: {rawData}");
                        continue; // Continue monitoring, but skip this frame
                    }

                    // Parse CAN frame from monitoring format
                    // Example with CAF0: "1DB 10 14 61 01 00 00 00"
                    if (TryParseMonitoringFrame(rawData, out var frame))
                    {
                        // Some adapters occasionally output lines that look like a CAN ID but contain no data.
                        // Treat these as noise rather than real frames to avoid confusing downstream parsers.
                        if (frame.Data.Length == 0)
                            continue;

                        yield return frame;
                    }
                    else if (!rawData.StartsWith('<') &&
                             !rawData.Contains('?'))
                    {
                        // Log unparseable frames (but not prompt characters or error markers)
                        Log($"Failed to parse monitoring frame: '{rawData}'");
                    }
                }
            }
        }

        private bool TryParseMonitoringFrame(string rawData, out RawCanFrame frame)
        {
            frame = default;

            // Skip ELM327 error messages
            if (rawData.Contains("DATA ERROR", StringComparison.OrdinalIgnoreCase) ||
                rawData.Contains("BUFFER FULL", StringComparison.OrdinalIgnoreCase) ||
                rawData.Contains("CAN ERROR", StringComparison.OrdinalIgnoreCase) ||
                rawData.Contains('?', StringComparison.OrdinalIgnoreCase) ||
                rawData.StartsWith('<') ||
                string.IsNullOrWhiteSpace(rawData))
            {
                return false;
            }

            // Monitoring format with CAF0: "CAN_ID BYTE1 BYTE2 BYTE3 ..."
            // Example: "1DB 10 14 61 01 00 00 00"
            var parts = rawData.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Need at least CAN ID (1 part) - data bytes are optional for valid frames
            if (parts.Length < 1)
                return false;

            // Parse CAN ID (3 hex digits for 11-bit CAN)
            if (!int.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var canId))
                return false;

            // Validate CAN ID range (11-bit CAN: 0x000-0x7FF)
            if (canId < 0 || canId > 0x7FF)
                return false;

            // Parse data bytes (if any)
            var dataBytes = new List<byte>();
            for (var i = 1; i < parts.Length; i++)
            {
                if (byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out var b))
                    dataBytes.Add(b);
                else
                    break; // Stop at first non-hex byte
            }

            // Valid CAN frames have 0-8 data bytes
            // If we got more than 8 bytes, something is wrong - reject the frame
            if (dataBytes.Count > 8)
                return false;

            frame = new RawCanFrame(canId, new ReadOnlyMemory<byte>([.. dataBytes]));
            return true;
        }

        private void Log(string message)
        {
            Serilog.Log.Debug("[ElmSession] {Message}", message);
            System.Diagnostics.Debug.WriteLine($"[ElmSession] {message}");

            if (EnableDebugLogging)
            {
                Console.WriteLine($"[ElmSession] {message}");
            }
        }
    }
}
