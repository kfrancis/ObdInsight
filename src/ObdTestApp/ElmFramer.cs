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
        public async ValueTask<string> SendAndReadFrameAsync(
            string command,
            TimeSpan timeout,
            CancellationToken ct)
        {
            // ELM expects CR-terminated commands.
            var bytes = System.Text.Encoding.ASCII.GetBytes(command + "\r");
            await _transport.WriteAsync(bytes, ct);
            await _transport.FlushAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var sb = new System.Text.StringBuilder(256);
            var buf = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                while (true)
                {
                    var n = await _transport.ReadAsync(buf.AsMemory(0, 256), cts.Token);
                    if (n <= 0) continue;
                    for (var i = 0; i < n; i++)
                    {
                        var b = buf[i];
                        if (b == 0x00) continue; // defensive: drop rare NULLs
                        if (b == _prompt) return sb.ToString(); // end-of-frame
                        sb.Append((char)b);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }
}