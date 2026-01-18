using Serilog;
using System.Buffers;

namespace ObdTestApp
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
        /// Sends a command asynchronously and reads the response frame as a string.
        /// </summary>
        /// <remarks>The method appends a carriage return to the command before sending. The operation is
        /// canceled if the response is not received within the specified timeout or if the provided cancellation token
        /// is triggered. The returned string does not include the prompt character that indicates the end of the
        /// frame.</remarks>
        /// <param name="command">The command to send. The command should not include a carriage return; it will be appended automatically.</param>
        /// <param name="timeout">The maximum duration to wait for a response before the operation is canceled.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains the response frame as a string,
        /// excluding the prompt character.</returns>
        /// <summary>
        /// Time to wait for more data after receiving some data before considering the response complete.
        /// This handles cases where the ELM327 doesn't send the > prompt after multi-frame responses.
        /// </summary>
        public TimeSpan DataIdleTimeout { get; set; } = TimeSpan.FromMilliseconds(500);

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

                var partial = sb.ToString().Replace("\r", "\\r").Replace("\n", "\\n");
                Log($"<<< FRAME TIMEOUT: {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms for '{command}'. No data received.");
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
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