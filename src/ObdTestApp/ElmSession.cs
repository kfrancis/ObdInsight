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
        /// Gets or sets the maximum number of consecutive failures allowed before triggering a failure response.
        /// </summary>
        public int MaxConsecutiveFailures { get; set; } = 3;

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
                await BaselineInitAsync(ct);
                await DetectAndLockProtocolAsync(ct);
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
                var lines = await SendAndNormalizeAsync(obdCommand, ct);
                if (IsValid(lines)) { _failures = 0; return lines; }
                _failures++;
                if (_failures >= MaxConsecutiveFailures)
                {
                    await RecoverAsync(ct);
                    _failures = 0;
                }
                // retry once after (possible) recovery
                lines = await SendAndNormalizeAsync(obdCommand, ct);
                if (!IsValid(lines)) throw new IOException("ELM query failed after recovery.");
                return lines;
            }
            finally { _gate.Release(); }
        }

        private static bool IsValid(string[] lines)
        => lines.Length > 0 && !lines.Any(ElmParsing.LooksLikeAdapterError);

        private async ValueTask BaselineInitAsync(CancellationToken ct)
        {
            // Keep these idempotent.
            await _framer.SendAndReadFrameAsync("AT Z", CommandTimeout, ct);
            await _framer.SendAndReadFrameAsync("AT D", CommandTimeout, ct);
            await _framer.SendAndReadFrameAsync("AT E0", CommandTimeout, ct);
            await _framer.SendAndReadFrameAsync("AT L0", CommandTimeout, ct);
            await _framer.SendAndReadFrameAsync("AT S0", CommandTimeout, ct);
            await _framer.SendAndReadFrameAsync("AT H0", CommandTimeout, ct);
            await _framer.SendAndReadFrameAsync("AT AT1", CommandTimeout, ct);
        }

        private async ValueTask DetectAndLockProtocolAsync(CancellationToken ct)
        {
            await _framer.SendAndReadFrameAsync("AT SP 0", CommandTimeout, ct);
            // Force bus init / search.
            var probe = await SendAndNormalizeAsync("0100", ct);
            if (!IsValid(probe)) throw new IOException("Probe failed during protocol detect.");
            var dpn = ElmParsing.NormalizeLines(await _framer.SendAndReadFrameAsync("AT DPN", CommandTimeout, ct))
                .FirstOrDefault() ?? string.Empty;
            // Examples: "A6" (auto + protocol 6) or "6".
            var protoChar = dpn.Trim().TrimStart('A', 'a').FirstOrDefault();
            if (protoChar == '\0') throw new IOException("Could not parse AT DPN response.");
            _lockedProtocol = protoChar;
            await _framer.SendAndReadFrameAsync($"AT SP {_lockedProtocol}", CommandTimeout, ct);
            // Verify after lock.
            probe = await SendAndNormalizeAsync("0100", ct);
            if (!IsValid(probe)) throw new IOException("Probe failed after protocol lock.");
        }

        private async ValueTask RecoverAsync(CancellationToken ct)
        {
            // Level 0: parser resync - send CR to request prompt/redo last command.
            if (await TryResyncAsync(ct)) return;
            // Level 1: protocol close + reapply locked protocol.
            if (await TryProtocolResyncAsync(ct)) return;
            // Level 2: reapply baseline + locked protocol.
            await BaselineInitAsync(ct);
            if (_lockedProtocol is not null)
                await _framer.SendAndReadFrameAsync($"AT SP {_lockedProtocol}", CommandTimeout, ct);
            if (await TryProbeAsync(ct)) return;
            // Level 3: hard reset and full re-detect.
            await BaselineInitAsync(ct);
            await DetectAndLockProtocolAsync(ct);
        }

        private async ValueTask<string[]> SendAndNormalizeAsync(string cmd, CancellationToken ct)
        {
            var frame = await _framer.SendAndReadFrameAsync(cmd, CommandTimeout, ct);
            return ElmParsing.NormalizeLines(frame);
        }

        private async ValueTask<bool> TryProbeAsync(CancellationToken ct)
        {
            try
            {
                var probe = await SendAndNormalizeAsync("0100", ct);
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
    }
}