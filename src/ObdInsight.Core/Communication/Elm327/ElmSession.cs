using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Communication.Elm327
{
    public interface IElmSession
    {
        TimeProvider TimeProvider => TimeProvider.System;
        /// <summary>Permanent response-boundary failure; null if no such failure has been observed.</summary>
        ElmSessionInvalidatedException? Failure => null;

        /// <summary>Query completion evidence, captured before outer arbitration resumes monitoring.</summary>
        async ValueTask<Observed<string[]>> QueryResponseAsync(string command, EcuContext context, CancellationToken ct)
        {
            var lines = await QueryAsync(command, context, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new(lines, ObservationMetadata.Capture(TimeProvider, ObservationSource.DiagnosticQuery, query: command));
        }
        TimeSpan CommandTimeout { get; set; }
        EcuCommunicationMode CurrentMode { get; }
        bool EnableDebugLogging { get; set; }
        TimeSpan ProtocolDetectionTimeout { get; set; }

        /// <summary>
        ///     Why the most recent <see cref="MonitorFramesAsync" /> enumeration ended.
        ///     <see cref="MonitoringEndReason.None" /> while a run is in progress.
        /// </summary>
        MonitoringEndReason LastMonitoringEndReason { get; }

        ValueTask<bool> ActivateSessionAsync(EcuContext context, CancellationToken ct);

        /// <summary>
        ///     Sends the context's keep-alive command (typically TesterPresent, e.g. "3E80") with
        ///     tolerance for suppress-positive-response silence. Returns true when the command was
        ///     sent and no adapter error came back; false when keep-alive could not be sent
        ///     (e.g. session is in monitoring mode). No-op true when the context has no keep-alive.
        /// </summary>
        ValueTask<bool> SendKeepAliveAsync(EcuContext context, CancellationToken ct);

        ValueTask EnterMonitoringModeAsync(EcuContext context, CancellationToken ct);
        ValueTask ExitMonitoringModeAsync(CancellationToken ct);
        ValueTask InitializeAndLockAsync(CancellationToken ct);
        IAsyncEnumerable<RawCanFrame> MonitorFramesAsync(CancellationToken ct);
        ValueTask<string[]> QueryAsync(string obdCommand, CancellationToken ct);
        ValueTask<string[]> QueryAsync(string obdCommand, EcuContext context, CancellationToken ct);
        ValueTask SetEcuContextAsync(EcuContext context, CancellationToken ct);
    }

    /// <summary>
    ///     Represents a session for communicating with an ELM-based OBD-II adapter, managing protocol initialization,
    ///     command execution, and error recovery.
    /// </summary>
    /// <remarks>
    ///     An ElmSession encapsulates the state and logic required to reliably interact with an ELM
    ///     adapter. Context-bearing queries serialize configuration and one command exchange.
    ///     Monitoring owns the reader until its enumeration is disposed; suspend/join it before
    ///     querying. Settings must not be mutated during operations. The supplied framer/transport
    ///     are exclusive to this session and must not be used concurrently through another path.
    /// </remarks>
    public sealed class ElmSession : IElmSession
    {
        private readonly ElmFramer _framer;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly ILogger _logger;
        private readonly IEcuWakeupStrategy? _wakeupStrategy;
        private EcuContext? _activeContext;
        private char? _lockedProtocol;
        private bool _monitorPromptPending;

        /// <summary>
        ///     Initializes a new instance of the ElmSession class using the specified ELM framer.
        /// </summary>
        /// <param name="framer">The ElmFramer instance used to frame and parse ELM protocol messages. Cannot be null.</param>
        /// <param name="wakeupStrategy">
        ///     Optional vehicle-specific wakeup/probe strategy, tried when the standard OBD-II
        ///     broadcast probe gets no response (e.g. EVs whose ECUs ignore Mode 01 queries).
        /// </param>
        /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
        public ElmSession(ElmFramer framer, IEcuWakeupStrategy? wakeupStrategy = null,
            ILogger<ElmSession>? logger = null, TimeProvider? timeProvider = null)
        {
            _framer = framer;
            TimeProvider = timeProvider ?? TimeProvider.System;
            _wakeupStrategy = wakeupStrategy;
            _logger = logger ?? NullLogger<ElmSession>.Instance;
        }

        /// <summary>
        ///     Gets or sets the maximum amount of time to wait for a command to execute before timing out.
        /// </summary>
        public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(4);
        public TimeProvider TimeProvider { get; }
        public ElmSessionInvalidatedException? Failure => _framer.Failure;

        /// <summary>
        ///     Gets or sets the timeout for protocol detection commands (0100 probe).
        ///     Protocol detection can take longer as the adapter searches through protocols.
        ///     The ELM327 shows "SEARCHING..." during this time.
        /// </summary>
        public TimeSpan ProtocolDetectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        ///     Retained for API compatibility. Logging is routed through the injected
        ///     <see cref="ILogger{ElmSession}" />; configure that logger's level/sinks instead.
        /// </summary>
        public bool EnableDebugLogging { get; set; }

        /// <summary>
        ///     Gets the current communication mode of the session.
        /// </summary>
        public EcuCommunicationMode CurrentMode { get; private set; } = EcuCommunicationMode.RequestResponse;

        /// <summary>
        ///     Why the most recent <see cref="MonitorFramesAsync" /> enumeration ended.
        ///     Reset to <see cref="MonitoringEndReason.None" /> when a new enumeration starts.
        /// </summary>
        public MonitoringEndReason LastMonitoringEndReason { get; private set; }


        /// <summary>
        ///     Initializes the component and acquires an exclusive lock to prevent concurrent initialization.
        /// </summary>
        /// <remarks>
        ///     This method ensures that initialization and protocol locking are performed
        ///     atomically. If another operation is already in progress, this method waits until the lock is available.
        ///     Callers should await the returned task to ensure initialization is complete before proceeding.
        /// </remarks>
        /// <param name="ct">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous initialization and locking operation.</returns>
        public async ValueTask InitializeAndLockAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                EnsureRequestResponse();
                Log("Starting ELM327 initialization...");
                await BaselineInitAsync(ct);
                Log("Baseline initialization complete");

                Log("Detecting and locking protocol...");
                await DetectAndLockProtocolAsync(ct);
                Log($"Protocol locked: {_lockedProtocol}");

            }
            catch (OperationCanceledException ex)
            {
                _framer.Invalidate(ex); // an admitted state transition may be only partially applied
                throw;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        ///     Sends an OBD command asynchronously and returns the response lines after normalization and validation.
        /// </summary>
        /// <remarks>
        ///     Executes exactly once under the session gate. For concurrent ECU-specific work,
        ///     use the context-bearing overload; SetEcuContextAsync followed by this overload is
        ///     two operations, not one transaction. Interrupted delivery invalidates the connection.
        /// </remarks>
        /// <param name="obdCommand">The OBD command to send to the device. Cannot be null or empty.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>
        ///     A string array containing the normalized response lines from the OBD device. The array is guaranteed to be
        ///     valid according to the device's response validation logic.
        /// </returns>
        /// <exception cref="IOException">Thrown for a rejected complete response or an invalidated session; queries never retry implicitly.</exception>
        public async ValueTask<string[]> QueryAsync(string obdCommand, CancellationToken ct)
        {
            ValidateQuery(obdCommand);
            EnsureRequestResponse();
            await _gate.WaitAsync(ct);
            try
            {
                return await QueryInternalAsync(obdCommand, ct).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        private void EnsureRequestResponse()
        {
            _framer.ThrowIfInvalidated();
            if (CurrentMode != EcuCommunicationMode.RequestResponse || _monitorPromptPending)
                throw new InvalidOperationException("Suspend and join monitoring before issuing a diagnostic transaction.");
        }

        private static void ValidateQuery(string command)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            if (command.Contains('\r') || command.Contains('\n'))
                throw new ArgumentException("A query must contain exactly one command.", nameof(command));
        }

        private async ValueTask<string[]> QueryInternalAsync(string command, CancellationToken ct)
        {
            EnsureRequestResponse();
            var lines = await SendAndNormalizeAsync(command, ct).ConfigureAwait(false);
            if (!IsValid(lines)) throw new ElmQueryRejectedException(command);
            return lines;
        }

        /// <summary>
        ///     Configures the ELM adapter for communication with a specific ECU.
        /// </summary>
        /// <remarks>
        ///     This method sets up CAN headers, receive filters, and ISO-TP flow control
        ///     for the specified ECU. Always reconfigures. This does not reserve the context for
        ///     a subsequent call; use QueryAsync(command, context, ct) for atomic ECU access.
        /// </remarks>
        /// <param name="context">The ECU context containing headers and flow control settings.</param>
        /// <param name="ct">Cancellation token.</param>
        public async ValueTask SetEcuContextAsync(EcuContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);

            // Enforce that monitoring contexts must use EnterMonitoringModeAsync
            if (context.CommunicationMode == EcuCommunicationMode.PassiveMonitoring ||
                context.CommunicationMode == EcuCommunicationMode.ActiveMonitoring ||
                context.CommunicationMode == EcuCommunicationMode.FilteredMonitoring)
            {
                throw new InvalidOperationException("Use EnterMonitoringModeAsync() for monitoring contexts.");
            }

            await _gate.WaitAsync(ct);
            try
            {
                EnsureRequestResponse();
                await ResetAndConfigureAsync(context, ct).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        ///     Sends an OBD command to a specific ECU context asynchronously.
        ///     Automatically configures the adapter if needed.
        /// </summary>
        /// <remarks>
        ///     Configuration and a single query exchange are one serialized transaction.
        ///     No command is implicitly retried, including on prompt-terminated NO DATA.
        /// </remarks>
        /// <param name="obdCommand">The OBD command to send to the device. Cannot be null or empty.</param>
        /// <param name="context">The ECU context to use for this query.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A string array containing the normalized response lines from the ECU.</returns>
        /// <exception cref="IOException">Thrown for a rejected complete response or an invalidated session.</exception>
        public async ValueTask<string[]> QueryAsync(string obdCommand, EcuContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);
            ValidateQuery(obdCommand);
            EnsureRequestResponse();
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                EnsureRequestResponse();
                if (context.CommunicationMode != EcuCommunicationMode.RequestResponse)
                    throw new InvalidOperationException("Query requires a request/response ECU context.");
                await ResetAndConfigureAsync(context, ct).ConfigureAwait(false);
                return await QueryInternalAsync(obdCommand, ct).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        ///     Activates a diagnostic session with the specified ECU.
        ///     Required for some ECUs before they will respond to queries or broadcast data.
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
                EnsureRequestResponse();
                Log($"Activating session for {context.Name}: {context.SessionActivationCommand}");

                // Ensure ECU context is configured
                if (!ReferenceEquals(_activeContext, context))
                {
                    await ResetAndConfigureAsync(context, ct);
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
                var isSuppressPositive =
                    context.SessionActivationCommand.EndsWith("81", StringComparison.OrdinalIgnoreCase) ||
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
        ///     Enters passive monitoring mode for the specified ECU context.
        /// </summary>
        /// <remarks>
        ///     This is a state transition. Once in monitoring mode, you cannot send queries
        ///     until you call ExitMonitoringModeAsync(). Use MonitorFramesAsync() to read frames.
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
            {
                throw new InvalidOperationException($"ECU context '{context.Name}' does not support monitoring modes.");
            }

            await _gate.WaitAsync(ct);
            try
            {
                _framer.ThrowIfInvalidated();
                if (CurrentMode == EcuCommunicationMode.PassiveMonitoring || _monitorPromptPending)
                {
                    Log("Already in monitoring mode - exiting first");
                    await ExitMonitoringModeInternalAsync(ct);
                }

                Log($"Entering monitoring mode: {context.Name}");

                // Exit above consumes the actual stop prompt. Clearing buffered bytes
                // cannot establish a boundary: a late prompt may still be in flight.

                // CRITICAL: Reset adapter state before monitoring configuration
                await ResetAdapterStateAsync(ct);

                // Configure headers/formatting
                await _framer.SendAndReadFrameAsync($"AT H{(context.EnableHeaders ? "1" : "0")}", CommandTimeout, ct);
                await _framer.SendAndReadFrameAsync($"AT CAF{(context.EnableAutoFormatting ? "1" : "0")}",
                    CommandTimeout, ct);

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
                    // Publish mode immediately after the write; cancellation during a
                    // subsequent caller delay must not leave an unrecorded mode transition.
                }

                CurrentMode = EcuCommunicationMode.PassiveMonitoring;
                _activeContext = context;
                Log($"Monitoring mode active: {context.Name}");
            }
            catch (OperationCanceledException ex)
            {
                _framer.Invalidate(ex); // an admitted state transition may be only partially applied
                throw;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        ///     Exits passive monitoring mode and returns to request/response mode.
        ///     Join/dispose the monitor reader before calling. Cancellation while waiting for
        ///     the gate does not force a mode change; a missing stop prompt invalidates framing.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        public async ValueTask ExitMonitoringModeAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                await ExitMonitoringModeInternalAsync(ct);
            }
            catch (OperationCanceledException ex)
            {
                _framer.Invalidate(ex); // an admitted state transition may be only partially applied
                throw;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        ///     Reads CAN frames while in monitoring mode.
        /// </summary>
        /// <remarks>
        ///     Must be called after EnterMonitoringModeAsync(). Returns parsed frames
        ///     as they arrive. Use with a loop and cancellation token to continuously monitor.
        /// </remarks>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>An async enumerable of frames.</returns>
        /// <exception cref="InvalidOperationException">Thrown if not in monitoring mode.</exception>
        /// <exception cref="IOException">Thrown if ELM327 buffer overflows (BUFFER FULL).</exception>
        public async IAsyncEnumerable<RawCanFrame> MonitorFramesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
            _framer.ThrowIfInvalidated();
            if (CurrentMode != EcuCommunicationMode.PassiveMonitoring)
            {
                throw new InvalidOperationException("Not in monitoring mode. Call EnterMonitoringModeAsync() first.");
            }

            LastMonitoringEndReason = MonitoringEndReason.None;

            while (!ct.IsCancellationRequested)
            {
                // Check cancellation at the start of each iteration
                if (ct.IsCancellationRequested)
                {
                    Log("Monitoring cancelled (token check)");
                    LastMonitoringEndReason = MonitoringEndReason.Stopped;
                    yield break;
                }

                string? rawData;
                bool hasData;

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
                    LastMonitoringEndReason = MonitoringEndReason.Stopped;
                    yield break;
                }

                if (!hasData)
                {
                    continue;
                }

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
                    CurrentMode = EcuCommunicationMode.RequestResponse; // Device has exited monitoring mode
                    // Exit gracefully instead of throwing - this allows the session to continue.
                    // The caller can check LastMonitoringEndReason to know why monitoring ended.
                    LastMonitoringEndReason = MonitoringEndReason.BufferFull;
                    _monitorPromptPending = true;
                    yield break;
                }

                // Check for prompt character indicating ELM327 exited monitoring mode
                if (rawData.Contains('>'))
                {
                    Log("Prompt detected - ELM327 has exited monitoring mode");
                    CurrentMode = EcuCommunicationMode.RequestResponse;
                    LastMonitoringEndReason = MonitoringEndReason.PromptDetected;
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
                    {
                        continue;
                    }

                    yield return frame with { Observation = ObservationMetadata.Capture(TimeProvider, ObservationSource.CanBroadcast, frame.CanId) };
                }
                else if (!rawData.StartsWith('<') &&
                         !rawData.Contains('?'))
                {
                    // Log unparseable frames (but not prompt characters or error markers)
                    Log($"Failed to parse monitoring frame: '{rawData}'");
                }
            }

            // While-condition exit: caller's token was cancelled.
            LastMonitoringEndReason = MonitoringEndReason.Stopped;
            }
            finally { _gate.Release(); }
        }

        /// <summary>
        ///     Sends one keep-alive command. ECU suppression does not suppress the ELM prompt:
        ///     a prompt-terminated empty reply is acceptable, but a timeout invalidates framing.
        /// </summary>
        public async ValueTask<bool> SendKeepAliveAsync(EcuContext context, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (string.IsNullOrEmpty(context.KeepAliveCommand))
            {
                return true;
            }

            if (CurrentMode == EcuCommunicationMode.PassiveMonitoring)
            {
                Log("Keep-alive skipped: session is in monitoring mode");
                return false;
            }

            await _gate.WaitAsync(ct);
            try
            {
                EnsureRequestResponse();
                // Monitoring exit clears the active context, so (re)configure headers each time.
                if (!ReferenceEquals(_activeContext, context))
                {
                    await ResetAndConfigureAsync(context, ct);
                }

                var response = await SendAndNormalizeAsync(context.KeepAliveCommand, CommandTimeout, ct);
                return !response.Any(ElmParsing.LooksLikeAdapterError);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        ///     Reconfigure under the caller's gate. Any partial configuration failure invalidates
        ///     the session rather than leaving a misleading cached ECU context.
        /// </summary>
        private async ValueTask ResetAndConfigureAsync(EcuContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ResetAdapterStateAsync(ct).ConfigureAwait(false);
                await ConfigureEcuContextInternalAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _activeContext = null;
                _framer.Invalidate(ex); // partial configuration is not a usable ECU context
                throw;
            }
        }

        private async ValueTask ConfigureEcuContextInternalAsync(EcuContext context, CancellationToken ct)
        {
            // Configure headers and formatting
            await _framer.SendAndReadFrameAsync($"AT H{(context.EnableHeaders ? "1" : "0")}", CommandTimeout, ct);
            await _framer.SendAndReadFrameAsync($"AT CAF{(context.EnableAutoFormatting ? "1" : "0")}", CommandTimeout,
                ct);

            // Set CAN headers
            if (!string.IsNullOrEmpty(context.TxHeader) && context.TxHeader != "000")
            {
                await _framer.SendAndReadFrameAsync($"AT SH {context.TxHeader}", CommandTimeout, ct);
            }

            if (!string.IsNullOrEmpty(context.RxFilter) && context.RxFilter != "000")
            {
                await _framer.SendAndReadFrameAsync($"AT CRA {context.RxFilter}", CommandTimeout, ct);
            }

            // Configure ISO-TP flow control
            if (!string.IsNullOrEmpty(context.FlowControlHeader))
            {
                await _framer.SendAndReadFrameAsync($"AT FC SH {context.FlowControlHeader}", CommandTimeout, ct);
            }

            if (!string.IsNullOrEmpty(context.FlowControlData))
            {
                await _framer.SendAndReadFrameAsync($"AT FC SD {context.FlowControlData}", CommandTimeout, ct);
            }

            if (!string.IsNullOrEmpty(context.FlowControlMode))
            {
                await _framer.SendAndReadFrameAsync($"AT FC SM {context.FlowControlMode}", CommandTimeout, ct);
            }

            // Set adapter timeout if specified
            if (context.AdapterTimeoutUnits > 0)
            {
                await _framer.SendAndReadFrameAsync($"AT ST {context.AdapterTimeoutUnits:X2}", CommandTimeout, ct);
            }

            _activeContext = context;
            Log($"ECU context '{context.Name}' configured");
        }

        private static bool IsValid(string[] lines)
        {
            return lines.Length > 0 && !lines.Any(ElmParsing.LooksLikeAdapterError);
        }

        /// <summary>
        ///     Resets ELM327 filter and addressing state to known baseline.
        ///     Must be called before reconfiguring for a different ECU.
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
            catch (Exception ex) when (ex is not OperationCanceledException && _framer.Failure is null)
            {
                Log($"AT D command failed (adapter may not support it): {ex.Message}");
            }

            Log("Baseline init: AT E0 (echo off)");
            await _framer.SendAndReadFrameAsync("AT E0", CommandTimeout, ct);

            Log("Baseline init: AT L0 (linefeeds off)");
            await _framer.SendAndReadFrameAsync("AT L0", CommandTimeout, ct);

            Log("Baseline init: AT S0 (spaces off)");
            await _framer.SendAndReadFrameAsync("AT S0", CommandTimeout, ct);

            // Headers ON is required for proper CAN communication with many EVs
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
            // Many vehicles (especially EVs) have ECUs that sleep
            // Sending to broadcast address 7DF wakes them up
            Log("Sending broadcast wakeup sequence...");
            await TryWakeupEcusAsync(ct);

            // If we already locked the protocol during wakeup (vehicle-specific strategy responded),
            // verify it works and return early
            if (_lockedProtocol is not null)
            {
                Log($"Protocol already locked to {_lockedProtocol} during wakeup - verifying...");
                // Reset headers to default for standard OBD queries
                await _framer.SendAndReadFrameAsync("AT SH 7DF", CommandTimeout, ct);
                // Standard 0100 may not work on this vehicle, but the protocol is already confirmed
                Log($"Protocol {_lockedProtocol} locked (EV-CAN mode - standard OBD-II queries may not work)");
                return;
            }

            // Try known protocols first before auto-detect
            // This is faster and more reliable for known vehicles
            // Protocol 6 = ISO 15765-4 CAN (11-bit, 500kbps) - most modern vehicles
            // Protocol 7 = ISO 15765-4 CAN (29-bit, 500kbps) - some vehicles
            // Protocol 8 = ISO 15765-4 CAN (11-bit, 250kbps) - rare
            // Protocol 9 = ISO 15765-4 CAN (29-bit, 250kbps) - rare

            var protocolsToTry = new[]
            {
                ('6', "ISO 15765-4 CAN 11-bit 500k"), // Most common - most modern cars
                ('7', "ISO 15765-4 CAN 29-bit 500k"), // Some vehicles
                ('0', "Auto-detect") // Fallback to auto-detect
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
                            var dpn = ElmParsing
                                .NormalizeLines(await _framer.SendAndReadFrameAsync("AT DPN", CommandTimeout, ct))
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
                catch (Exception ex) when (ex is not OperationCanceledException && _framer.Failure is null)
                {
                    Log($"Protocol {protocol} failed with error: {ex.Message}");
                }
            }

            // If we get here, all protocols failed
            throw new IOException(
                "All protocol detection attempts failed. Check vehicle connection and ensure ignition is ON.");
        }

        /// <summary>
        ///     Attempts to wake up sleeping ECUs before protocol detection.
        ///     Many vehicles (especially EVs) have ECUs that sleep when the car is off.
        ///     Based on OVMS (Open Vehicle Monitoring System) wakeup sequences.
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
                else if (_wakeupStrategy is not null)
                {
                    // Some vehicles (especially EVs) don't respond to standard OBD-II queries;
                    // fall back to the vehicle-specific probe supplied by the caller.
                    Log($"Wakeup: No standard OBD-II response - trying wakeup strategy '{_wakeupStrategy.Name}'...");
                    var lockedProtocol = await _wakeupStrategy.TryWakeupAsync(_framer, CommandTimeout, ct);
                    if (lockedProtocol is not null)
                    {
                        Log($"Wakeup strategy '{_wakeupStrategy.Name}' confirmed protocol {lockedProtocol}");
                        _lockedProtocol = lockedProtocol;
                    }
                }
                else
                {
                    Log("Wakeup: No standard OBD-II response and no wakeup strategy configured");
                }

                // Small delay for ECUs to wake up
                await Task.Delay(500, ct);

                // Reset to auto-protocol for detection phase
                Log("Wakeup: Resetting to auto-protocol");
                await _framer.SendAndReadFrameAsync("AT SP 0", CommandTimeout, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && _framer.Failure is null)
            {
                Log($"Wakeup sequence error (non-fatal): {ex.Message}");
                // Don't throw - wakeup is best-effort
            }
        }

        private async ValueTask<string[]> SendAndNormalizeAsync(string cmd, CancellationToken ct)
        {
            return await SendAndNormalizeAsync(cmd, CommandTimeout, ct);
        }

        private async ValueTask<string[]> SendAndNormalizeAsync(string cmd, TimeSpan timeout, CancellationToken ct)
        {
            var frame = await _framer.SendAndReadFrameAsync(cmd, timeout, ct);
            return ElmParsing.NormalizeLines(frame);
        }

        private async ValueTask ExitMonitoringModeInternalAsync(CancellationToken ct)
        {
            _framer.ThrowIfInvalidated();
            // The monitor reader has been joined before this gate is acquired. Do not
            // discard the stop prompt or treat a timeout as proof that the adapter stopped.
            if (CurrentMode == EcuCommunicationMode.PassiveMonitoring)
                await _framer.SendAndReadFrameAsync("", CommandTimeout, ct).ConfigureAwait(false);
            else if (_monitorPromptPending)
                await _framer.ReadUntilAsync(">", CommandTimeout, ct).ConfigureAwait(false);
            _monitorPromptPending = false;
            await ResetAdapterStateAsync(ct).ConfigureAwait(false);
            await _framer.SendAndReadFrameAsync("AT SH 7DF", CommandTimeout, ct).ConfigureAwait(false);
            await _framer.SendAndReadFrameAsync("AT CRA", CommandTimeout, ct).ConfigureAwait(false);
            await _framer.SendAndReadFrameAsync("AT S0", CommandTimeout, ct).ConfigureAwait(false);
            await _framer.SendAndReadFrameAsync("AT CAF0", CommandTimeout, ct).ConfigureAwait(false);
            CurrentMode = EcuCommunicationMode.RequestResponse;
            _activeContext = null;
        }

        private static bool TryParseMonitoringFrame(string rawData, out RawCanFrame frame)
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

            // Monitoring format: "CAN_ID BYTE1 BYTE2 BYTE3 ..." (11-bit or 29-bit ID, spaced
            // or contiguous depending on AT S0/S1). See RawCanFrameParser for the format rules.
            return RawCanFrameParser.TryParse(rawData, out frame);
        }

        private void Log(string message)
        {
            _logger.LogDebug("[ElmSession] {Message}", message);
            Debug.WriteLine($"[ElmSession] {message}");
        }
    }
}
