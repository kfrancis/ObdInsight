using System.Buffers;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ObdInsight.Core.Communication.Elm327
{
    /// <summary>
    ///     Provides framing and communication with an ELM-compatible device over a specified transport, handling command
    ///     transmission and response parsing.
    /// </summary>
    /// <remarks>
    ///     The ElmFramer class is designed for use with ELM327 and similar OBD-II adapters that use a
    ///     prompt-based ASCII protocol. It manages the low-level details of sending commands and reading responses,
    ///     ensuring that frames are correctly delimited and that communication adheres to ELM device expectations.
    ///     Instances of this class are not thread-safe and should not be shared across concurrent operations.
    /// </remarks>
    public sealed class ElmFramer
    {
        // Bytes read from the transport beyond a frame delimiter, preserved for the next read.
        // Single transport read can span a delimiter (e.g. one BLE notification carrying two
        // monitoring lines); without this carry-over the trailing bytes would be lost.
        private readonly Queue<byte> _carryOver = new();
        private readonly ILogger _logger;
        private readonly IElmTransport _transport;

        /// <summary>
        ///     Initializes a new instance of the ElmFramer class using the specified transport.
        /// </summary>
        /// <param name="transport">The transport mechanism used for sending and receiving Elm protocol frames. Cannot be null.</param>
        /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
        public ElmFramer(IElmTransport transport, ILogger<ElmFramer>? logger = null)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _logger = logger ?? NullLogger<ElmFramer>.Instance;
        }

        /// <summary>
        ///     Enable verbose debug logging to console (useful for troubleshooting connectivity issues).
        /// </summary>
        public bool EnableDebugLogging { get; set; }

        /// <summary>
        ///     Sends a command and returns only a prompt-terminated response.
        ///     The deadline covers write, flush, and read. Partial data is never returned.
        /// </summary>
        /// <exception cref="TimeoutException">The deadline expired before the prompt.</exception>
        /// <exception cref="EndOfStreamException">The transport ended before the prompt.</exception>
        /// <exception cref="OperationCanceledException">The caller canceled the operation.</exception>
        public async ValueTask<string> SendAndReadFrameAsync(
            string command, TimeSpan timeout, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(command);
            ct.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                Log($">>> FRAME SEND: '{command}'");
                await WriteAsync(command + "\r", deadline.Token).ConfigureAwait(false);
                var response = await ReadUntilAsync(">", Timeout.InfiniteTimeSpan, deadline.Token).ConfigureAwait(false);
                Log($"<<< FRAME RECV: {response.Length} bytes");
                return response;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException($"Timeout waiting for response to '{command}'.", ex);
            }
        }

        /// <summary>
        ///     Writes raw data to the transport without waiting for a response.
        ///     Used for entering monitoring mode.
        /// </summary>
        public async ValueTask WriteAsync(string text, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = Encoding.ASCII.GetBytes(text);
            await _transport.WriteAsync(bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await _transport.FlushAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        /// <summary>
        ///     Reads data from the transport until a delimiter is found or timeout occurs.
        ///     Used for reading monitoring mode frames.
        /// </summary>
        public async ValueTask<string> ReadUntilAsync(string delimiter, TimeSpan timeout, CancellationToken ct)
        {
            ArgumentException.ThrowIfNullOrEmpty(delimiter);
            // Check for already-cancelled token before starting
            ct.ThrowIfCancellationRequested();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var sb = new StringBuilder(256);
            var buf = ArrayPool<byte>.Shared.Rent(256);

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();

                    var n = await ReadChunkAsync(buf, cts.Token).ConfigureAwait(false);

                    cts.Token.ThrowIfCancellationRequested();
                    // A nonempty read returning zero is EOF, never a quiet polling result.
                    if (n <= 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        throw new EndOfStreamException("Transport ended before the frame delimiter.");
                    }

                    for (var i = 0; i < n; i++)
                    {
                        var b = buf[i];
                        if (b == 0x00)
                        {
                            continue;
                        }

                        sb.Append((char)b);

                        // Check if we've received the delimiter
                        if (sb.Length < delimiter.Length)
                        {
                            continue;
                        }

                        var matches = true;
                        for (var d = 0; d < delimiter.Length; d++)
                        {
                            if (sb[sb.Length - delimiter.Length + d] == delimiter[d]) continue;
                            matches = false;
                            break;
                        }
                        if (matches)
                        {
                            StashRemainder(buf, i + 1, n);
                            return sb.ToString(0, sb.Length - delimiter.Length);
                        }
                    }
                }

                ct.ThrowIfCancellationRequested();

                throw new TimeoutException($"Timeout reading until '{delimiter}'");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                throw new TimeoutException($"Timeout reading until '{delimiter}'");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
        }

        /// <summary>
        ///     Clears any pending data from the transport buffer and the framer's carry-over buffer.
        /// </summary>
        public void ClearBuffer()
        {
            _carryOver.Clear();
            _transport.ClearBuffer();
        }

        /// <summary>
        ///     Reads the next chunk of bytes, serving carried-over bytes (read past a previous
        ///     delimiter) before touching the transport, so stream order is preserved.
        /// </summary>
        private async ValueTask<int> ReadChunkAsync(byte[] buf, CancellationToken ct)
        {
            if (_carryOver.Count > 0)
            {
                var n = Math.Min(buf.Length, _carryOver.Count);
                for (var i = 0; i < n; i++)
                {
                    buf[i] = _carryOver.Dequeue();
                }

                return n;
            }

            return await _transport.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
        }

        /// <summary>
        ///     Preserves bytes read beyond a delimiter for the next read instead of discarding them.
        /// </summary>
        private void StashRemainder(byte[] buf, int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                _carryOver.Enqueue(buf[i]);
            }
        }

        private void Log(string message)
        {
            _logger.LogDebug("[ElmFramer] {Message}", message);
            if (EnableDebugLogging)
            {
                Debug.WriteLine($"[ElmFramer] {message}");
            }
        }
    }
}
