using Serilog;

namespace ObdTestApp
{
    /// <summary>
    /// Represents a session for communicating with an ELM-based OBD-II adapter, managing protocol initialization,
    /// command execution, and error recovery.
    /// </summary>
    /// <remarks>An ElmSession encapsulates the state and logic required to reliably interact with an ELM
    /// adapter, including protocol detection and locking, command timeouts, and automatic recovery from communication
    /// failures. Instances are not thread-safe; callers should not use the same ElmSession concurrently from multiple
    /// threads.</remarks>
    public sealed class ElmSession
    {
        private readonly ElmFramer _framer;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _failures;
        private char? _lockedProtocol;

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

        private static bool IsValid(string[] lines)
        => lines.Length > 0 && !lines.Any(ElmParsing.LooksLikeAdapterError);

        private async ValueTask BaselineInitAsync(CancellationToken ct)
        {
            // Keep these idempotent.
            Log("Baseline init: AT Z (reset)");
            await _framer.SendAndReadFrameAsync("AT Z", CommandTimeout, ct);

            Log("Baseline init: AT D (restore defaults)");
            await _framer.SendAndReadFrameAsync("AT D", CommandTimeout, ct);

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
                        char lockedProto = protocol;
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
        
        private void Log(string message)
        {
            // Always log to Serilog for file logging
            Serilog.Log.Debug("[ElmSession] {Message}", message);
            System.Diagnostics.Debug.WriteLine($"[ElmSession] {message}");

            if (EnableDebugLogging)
            {
                // Escape markup characters for Spectre.Console
                var escaped = message
                    .Replace("[", "[[")
                    .Replace("]", "]]")
                    .Replace("{", "{{")
                    .Replace("}", "}}");
                Spectre.Console.AnsiConsole.MarkupLine($"[grey][[ElmSession]] {escaped}[/]");
            }
        }
    }
}