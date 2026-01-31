using System.Buffers;

namespace ObdInsight.Core.Communication.Elm327
{
    /// <summary>
    /// Provides framing and communication with an ELM-compatible device over a specified transport, handling command
    /// transmission and response parsing.
    /// </summary>
    /// <remarks>The ElmFramer class is designed for use with ELM327 and similar OBD-II adapters that use a
    /// prompt-based ASCII protocol. It manages the low-level details of sending commands and reading responses,
    /// ensuring that frames are correctly delimited and that communication adheres to ELM device expectations.
    /// Instances of this class are not thread-safe and should not be shared across concurrent operations.</remarks>
    public sealed class ElmFramer
    {
        private readonly byte _prompt = (byte)'>';
        private readonly IElmTransport _transport;

        /// <summary>
        /// Initializes a new instance of the ElmFramer class using the specified transport.
        /// </summary>
        /// <param name="transport">The transport mechanism used for sending and receiving Elm protocol frames. Cannot be null.</param>
        public ElmFramer(IElmTransport transport) => _transport = transport;

        /// <summary>
        /// Enable verbose debug logging to console (useful for troubleshooting connectivity issues).
        /// </summary>
        public bool EnableDebugLogging { get; set; }

        /// <summary>
        /// Gets or sets the duration of inactivity to wait before considering data as idle.
        /// </summary>
        /// <remarks>The default value is 1500 milliseconds. Adjust this property to optimize
        /// responsiveness or resource usage based on application requirements.</remarks>
        public TimeSpan DataIdleTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

        public async ValueTask<string> SendAndReadFrameAsync(
            string command,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Log($">>> FRAME SEND: '{command}' (timeout: {timeout.TotalSeconds:F1}s)");

            // ELM expects CR-terminated commands.
            var bytes = System.Text.Encoding.ASCII.GetBytes(command + "\r");
            await _transport.WriteAsync(bytes, ct);
            await _transport.FlushAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var sb = new System.Text.StringBuilder(256);
            var buf = ArrayPool<byte>.Shared.Rent(256);
            var startTime = DateTime.UtcNow;
            var lastDataTime = DateTime.UtcNow;
            var hasReceivedData = false;

            try
            {
                while (true)
                {
                    // Check for data-idle timeout: if we have data and haven't received anything for a while,
                    // treat it as end of response (handles missing > prompt after multi-frame responses)
                    if (hasReceivedData && sb.Length > 0)
                    {
                        var idleTime = DateTime.UtcNow - lastDataTime;
                        if (idleTime > DataIdleTimeout)
                        {
                            var response = sb.ToString();
                            var elapsed = DateTime.UtcNow - startTime;
                            var escaped = response.Replace("\r", "\\r").Replace("\n", "\\n");
                            Log($"<<< FRAME RECV (idle): {elapsed.TotalMilliseconds:F0}ms, {response.Length} bytes: '{escaped}'");
                            return response;
                        }
                    }

                    var n = await _transport.ReadAsync(buf.AsMemory(0, 256), cts.Token);
                    if (n <= 0) continue;

                    hasReceivedData = true;
                    lastDataTime = DateTime.UtcNow;

                    for (var i = 0; i < n; i++)
                    {
                        var b = buf[i];
                        if (b == 0x00) continue; // defensive: drop rare NULLs
                        if (b == _prompt)
                        {
                            var response = sb.ToString();
                            var elapsed = DateTime.UtcNow - startTime;
                            var escaped = response.Replace("\r", "\\r").Replace("\n", "\\n");
                            Log($"<<< FRAME RECV: {elapsed.TotalMilliseconds:F0}ms, {response.Length} bytes: '{escaped}'");
                            return response;
                        }
                        sb.Append((char)b);
                    }
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // On timeout, if we have partial data, return it instead of throwing
                if (sb.Length > 0)
                {
                    var response = sb.ToString();
                    var elapsed = DateTime.UtcNow - startTime;
                    var escaped = response.Replace("\r", "\\r").Replace("\n", "\\n");
                    Log($"<<< FRAME RECV (timeout with data): {elapsed.TotalMilliseconds:F0}ms, {response.Length} bytes: '{escaped}'");
                    return response;
                }
                Log($"<<< FRAME TIMEOUT: {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms for '{command}'. No data received.");
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        /// <summary>
        /// Writes raw data to the transport without waiting for a response.
        /// Used for entering monitoring mode.
        /// </summary>
        public async ValueTask WriteAsync(string text, CancellationToken ct)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(text);
            await _transport.WriteAsync(bytes, ct);
            await _transport.FlushAsync(ct);
        }

        /// <summary>
        /// Reads data from the transport until a delimiter is found or timeout occurs.
        /// Used for reading monitoring mode frames.
        /// </summary>
        public async ValueTask<string> ReadUntilAsync(string delimiter, TimeSpan timeout, CancellationToken ct)
        {
            // Check for already-cancelled token before starting
            ct.ThrowIfCancellationRequested();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var sb = new System.Text.StringBuilder(256);
            var buf = ArrayPool<byte>.Shared.Rent(256);

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();

                    var n = await _transport.ReadAsync(buf.AsMemory(0, 256), cts.Token);

                    // If we got 0 bytes and cancellation is pending, exit
                    if (n <= 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        continue;
                    }

                    for (var i = 0; i < n; i++)
                    {
                        var b = buf[i];
                        if (b == 0x00) continue;
                        sb.Append((char)b);

                        // Check if we've received the delimiter
                        if (sb.Length < delimiter.Length)
                        {
                            continue;
                        }

                        var end = sb.ToString()[^delimiter.Length..];
                        if (end == delimiter)
                        {
                            return sb.ToString()[..^delimiter.Length];
                        }
                    }
                }
                ct.ThrowIfCancellationRequested();

                throw new TimeoutException($"Timeout reading until '{delimiter}'");
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timeout reading until '{delimiter}'");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        /// <summary>
        /// Clears any pending data from the transport buffer.
        /// </summary>
        public void ClearBuffer()
        {
            _transport.ClearBuffer();
        }
        
        private void Log(string message)
        {
            // Always log to Serilog for file logging
            Serilog.Log.Debug("[ElmFramer] {Message}", message);
            if (EnableDebugLogging)
                System.Diagnostics.Debug.WriteLine($"[ElmFramer] {message}");
        }
    }
}
