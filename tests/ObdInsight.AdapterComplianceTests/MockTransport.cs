using ObdInsight.Core.Transports;
using System.Collections.Concurrent;
using System.Text;

namespace ObdInsight.AdapterComplianceTests;

/// <summary>
/// A mock transport for controlled compliance testing scenarios.
/// </summary>
/// <remarks>
/// Unlike ReplayTransport which plays back recorded sessions, MockTransport
/// allows programmatic control over responses, timing, and error injection
/// for testing specific edge cases.
/// </remarks>
public sealed class MockTransport : IObdTransport
{
    private readonly ConcurrentQueue<MockResponse> _responses = new();
    private readonly StringBuilder _rxBuffer = new();
    private readonly Lock _lock = new();
    private bool _connected;
    private bool _disposed;

    /// <inheritdoc />
    public string Name => "MockTransport";

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <inheritdoc />
    public event EventHandler<string>? DataReceived;

    /// <inheritdoc />
    public event EventHandler<string>? DataSent;

    /// <summary>
    /// All commands that were sent to the transport.
    /// </summary>
    public List<string> SentCommands { get; } = [];

    /// <summary>
    /// Simulate a delay before each response is available.
    /// </summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// If true, throw TimeoutException when no responses are queued.
    /// </summary>
    public bool ThrowOnNoResponse { get; set; } = true;

    /// <summary>
    /// Queue a response to be returned on the next read operation.
    /// </summary>
    /// <param name="response">The response string to return</param>
    public void EnqueueResponse(string response)
    {
        _responses.Enqueue(new MockResponse(response, ResponseDelay, null));
    }

    /// <summary>
    /// Queue a response with specific delay.
    /// </summary>
    /// <param name="response">The response string to return</param>
    /// <param name="delay">Delay before response is available</param>
    public void EnqueueResponse(string response, TimeSpan delay)
    {
        _responses.Enqueue(new MockResponse(response, delay, null));
    }

    /// <summary>
    /// Queue a response that throws an exception.
    /// </summary>
    /// <param name="exception">The exception to throw</param>
    public void EnqueueException(Exception exception)
    {
        _responses.Enqueue(new MockResponse(null, TimeSpan.Zero, exception));
    }

    /// <summary>
    /// Queue a response for a specific command pattern.
    /// </summary>
    /// <param name="commandPattern">Command to match (normalized)</param>
    /// <param name="response">Response to return</param>
    public void SetupCommandResponse(string commandPattern, string response)
    {
        _commandResponses[NormalizeCommand(commandPattern)] = response;
    }

    private readonly Dictionary<string, string> _commandResponses = [];

    /// <summary>
    /// Clear all queued responses.
    /// </summary>
    public void ClearResponses()
    {
        while (_responses.TryDequeue(out _)) { }
        _commandResponses.Clear();
        lock (_lock)
        {
            _rxBuffer.Clear();
        }
    }

    /// <inheritdoc />
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _connected = true;
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task DisconnectAsync()
    {
        _connected = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
            throw new InvalidOperationException("Transport not connected.");

        SentCommands.Add(data);
        DataSent?.Invoke(this, data);

        // Check if we have a command-specific response
        var normalizedCmd = NormalizeCommand(data);
        if (_commandResponses.TryGetValue(normalizedCmd, out var cmdResponse))
        {
            await Task.Delay(ResponseDelay, cancellationToken);
            lock (_lock)
            {
                _rxBuffer.Append(cmdResponse);
            }
        }
        else if (_responses.TryDequeue(out var response))
        {
            if (response.Exception != null)
                throw response.Exception;

            await Task.Delay(response.Delay, cancellationToken);

            if (response.Data != null)
            {
                lock (_lock)
                {
                    _rxBuffer.Append(response.Data);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return await ReadUntilAsync("\r", timeout, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> ReadUntilAsync(string terminator, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
            throw new InvalidOperationException("Transport not connected.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var startTime = DateTime.UtcNow;

        while (!cts.Token.IsCancellationRequested)
        {
            string currentBuffer;
            lock (_lock)
            {
                currentBuffer = _rxBuffer.ToString();
            }

            var terminatorIndex = currentBuffer.IndexOf(terminator, StringComparison.Ordinal);
            if (terminatorIndex >= 0)
            {
                var result = currentBuffer[..(terminatorIndex + terminator.Length)];
                lock (_lock)
                {
                    _rxBuffer.Remove(0, terminatorIndex + terminator.Length);
                }

                DataReceived?.Invoke(this, result);
                return result;
            }

            // Check if buffer is non-empty but no terminator found
            if (!string.IsNullOrEmpty(currentBuffer))
            {
                // Small delay to simulate data accumulation
                await Task.Delay(5, cts.Token);
            }
            else if (ThrowOnNoResponse)
            {
                // If no data at all, wait briefly then timeout
                await Task.Delay(10, cts.Token);
            }
            else
            {
                break;
            }
        }

        // Return whatever is in buffer even without terminator
        lock (_lock)
        {
            if (_rxBuffer.Length > 0)
            {
                var result = _rxBuffer.ToString();
                _rxBuffer.Clear();
                DataReceived?.Invoke(this, result);
                return result;
            }
        }

        throw new TimeoutException($"Timeout waiting for terminator: '{EscapeForDisplay(terminator)}'");
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadBytesAsync(int count, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_connected)
            throw new InvalidOperationException("Transport not connected.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var result = new List<byte>();

        while (result.Count < count && !cts.Token.IsCancellationRequested)
        {
            string currentBuffer;
            lock (_lock)
            {
                currentBuffer = _rxBuffer.ToString();
            }

            if (currentBuffer.Length > 0)
            {
                var bytesToRead = Math.Min(count - result.Count, currentBuffer.Length);
                var bytes = Encoding.ASCII.GetBytes(currentBuffer[..bytesToRead]);
                result.AddRange(bytes);

                lock (_lock)
                {
                    _rxBuffer.Remove(0, bytesToRead);
                }
            }
            else
            {
                await Task.Delay(10, cts.Token);
            }
        }

        if (result.Count == 0)
            throw new TimeoutException($"Timeout reading {count} bytes");

        return [.. result];
    }

    /// <inheritdoc />
    public Task WriteBytesAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var stringData = Encoding.ASCII.GetString(data);
        return WriteAsync(stringData, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _connected = false;
    }

    private static string NormalizeCommand(string command) =>
        command.Trim().TrimEnd('\r', '\n').ToUpperInvariant();

    private static string EscapeForDisplay(string s) =>
        s.Replace("\r", "\\r").Replace("\n", "\\n");

    private sealed record MockResponse(string? Data, TimeSpan Delay, Exception? Exception);
}

/// <summary>
/// Builder for creating common mock transport scenarios.
/// </summary>
public static class MockTransportScenarios
{
    /// <summary>
    /// Creates a transport configured for successful ELM327 initialization.
    /// </summary>
    public static MockTransport CreateSuccessfulElm327Init(string version = "ELM327 v1.5")
    {
        var transport = new MockTransport();

        // ATZ response
        transport.EnqueueResponse($"\r\n\r\n{version}\r\n\r\n>");

        // ATE0 response
        transport.EnqueueResponse("OK\r\n\r\n>");

        // ATL0 response
        transport.EnqueueResponse("OK\r\n\r\n>");

        // ATS0 response
        transport.EnqueueResponse("OK\r\n\r\n>");

        // ATH0 response
        transport.EnqueueResponse("OK\r\n\r\n>");

        // ATST32 response
        transport.EnqueueResponse("OK\r\n\r\n>");

        // ATAT1 response
        transport.EnqueueResponse("OK\r\n\r\n>");

        // ATSP0 response
        transport.EnqueueResponse("OK\r\n\r\n>");

        // 0100 response (supported PIDs)
        transport.EnqueueResponse("4100BE1FA813\r\n\r\n>");

        // ATDP response
        transport.EnqueueResponse("AUTO, ISO 15765-4 CAN\r\n\r\n>");

        return transport;
    }

    /// <summary>
    /// Creates a transport configured for ELM327 init with no vehicle connected.
    /// </summary>
    public static MockTransport CreateNoVehicleElm327Init()
    {
        var transport = new MockTransport();

        // ATZ response
        transport.EnqueueResponse("\r\n\r\nELM327 v1.5\r\n\r\n>");

        // ATE0-ATSP0 responses
        for (var i = 0; i < 7; i++)
        {
            transport.EnqueueResponse("OK\r\n\r\n>");
        }

        // 0100 response (no vehicle)
        transport.EnqueueResponse("UNABLE TO CONNECT\r\n\r\n>");

        return transport;
    }

    /// <summary>
    /// Creates a transport that times out on the specified command number.
    /// </summary>
    public static MockTransport CreateTimeoutOnCommand(int commandNumber)
    {
        var transport = new MockTransport { ThrowOnNoResponse = true };

        // Queue normal responses up to timeout point
        for (var i = 0; i < commandNumber - 1; i++)
        {
            transport.EnqueueResponse("OK\r\n\r\n>");
        }

        // No response for the timeout command
        return transport;
    }

    /// <summary>
    /// Creates a transport with specific error response.
    /// </summary>
    public static MockTransport CreateWithError(string errorResponse)
    {
        var transport = CreateSuccessfulElm327Init();
        transport.EnqueueResponse($"{errorResponse}\r\n\r\n>");
        return transport;
    }
}