using System.Text;
using ObdInsight.Core.Communication.Elm327;

namespace ObdInsight.Simulation;

/// <summary>
/// Deterministic in-memory <see cref="IElmTransport"/> for testing <c>ElmFramer</c>/<c>ElmSession</c>
/// and everything above them without hardware.
///
/// Behavior model (mirrors a real ELM327 adapter):
/// <list type="bullet">
/// <item>Commands arrive CR-terminated; each complete command is dispatched exactly once.</item>
/// <item>A scripted exchange (<see cref="Expect"/>) always wins: its response bytes (including any
/// '&gt;' prompt) are queued for reading. An empty response means "adapter stays silent" — the
/// framer's own timeout handling then applies.</item>
/// <item>Unscripted AT commands are answered with <see cref="DefaultAtResponse"/> when
/// <see cref="AutoRespondToAtCommands"/> is set (lenient mode), so session init scripts stay terse.</item>
/// <item>An unscripted, non-AT command throws immediately — tests fail loudly instead of hanging.</item>
/// <item><see cref="EnqueueIncoming"/> pushes unsolicited data (monitoring-mode CAN frames).</item>
/// </list>
///
/// Reads block until data is available or the caller's <see cref="CancellationToken"/> fires —
/// never returning 0 immediately, which would busy-spin the framer's read loop.
/// </summary>
public sealed class ReplayElmTransport : IConnectionAwareTransport
{
    private readonly object _gate = new();
    private readonly Queue<byte> _rx = new();
    private readonly SemaphoreSlim _dataSignal = new(0, int.MaxValue);
    private readonly Queue<(string Command, string Response)> _script = new();
    private readonly Dictionary<string, string> _autoResponses = new();
    private readonly List<string> _sent = [];
    private readonly StringBuilder _txBuffer = new();
    private volatile bool _connectionDead;

    /// <summary>Raised by <see cref="SimulateConnectionLost"/> (resilience testing).</summary>
    public event EventHandler? ConnectionLost;

    /// <summary>
    /// Failure injection (roadmap B10): marks the link dead — every subsequent read
    /// and write throws <see cref="IOException"/>, blocked readers wake to observe the
    /// failure, and <see cref="ConnectionLost"/> fires once. A
    /// <c>ReconnectingElmTransport</c> reacts by disposing this instance and asking
    /// its factory for a replacement.
    /// </summary>
    public void SimulateConnectionLost()
    {
        if (_connectionDead)
        {
            return;
        }

        _connectionDead = true;
        IsOpen = false;
        _dataSignal.Release();
        ConnectionLost?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>When true (default), unscripted AT commands get <see cref="DefaultAtResponse"/>.</summary>
    public bool AutoRespondToAtCommands { get; init; } = true;

    /// <summary>Response used for unscripted AT commands in lenient mode.</summary>
    public string DefaultAtResponse { get; init; } = "OK\r\r>";

    /// <summary>Every complete CR-terminated command received, in order.</summary>
    public IReadOnlyList<string> SentCommands
    {
        get { lock (_gate) return [.. _sent]; }
    }

    public bool IsOpen { get; private set; }

    /// <summary>
    /// Adds a scripted exchange: when <paramref name="command"/> is received (and is the oldest
    /// unmatched script entry for that command), <paramref name="response"/> is queued for reading.
    /// Include the trailing "\r\r&gt;" prompt in the response for request/response exchanges;
    /// pass an empty response for "adapter stays silent".
    /// </summary>
    public void Expect(string command, string response)
    {
        lock (_gate) _script.Enqueue((command, response));
    }

    /// <summary>
    /// Registers a canned response for every occurrence of <paramref name="command"/> that is
    /// not consumed by the ordered script. Useful for unbounded repeating commands
    /// (e.g. periodic keep-alive "3E80"). Script entries still take priority.
    /// </summary>
    public void AutoRespond(string command, string response)
    {
        lock (_gate) _autoResponses[command] = response;
    }

    /// <summary>Pushes unsolicited bytes (e.g. monitoring-mode CAN frame lines) to the read buffer.</summary>
    public void EnqueueIncoming(string data)
    {
        lock (_gate)
        {
            foreach (var b in Encoding.ASCII.GetBytes(data)) _rx.Enqueue(b);
        }
        _dataSignal.Release();
    }

    public ValueTask OpenAsync(CancellationToken ct)
    {
        IsOpen = true;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            if (_connectionDead)
            {
                throw new IOException("Simulated connection loss.");
            }

            lock (_gate)
            {
                if (_rx.Count > 0)
                {
                    // Deliver everything available in one chunk — deliberately allowing a single
                    // read to span multiple lines, like a bursty BLE notification would. The
                    // framer's carry-over buffering must cope without losing data.
                    var n = Math.Min(buffer.Length, _rx.Count);
                    for (var i = 0; i < n; i++) buffer.Span[i] = _rx.Dequeue();
                    return n;
                }
            }

            // Block until data arrives or the caller's (timeout) token fires.
            // Spurious wakeups are fine — the loop re-checks the buffer.
            await _dataSignal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_connectionDead)
        {
            throw new IOException("Simulated connection loss.");
        }

        var text = Encoding.ASCII.GetString(data.Span);
        lock (_gate)
        {
            foreach (var c in text)
            {
                if (c == '\r')
                {
                    var command = _txBuffer.ToString();
                    _txBuffer.Clear();
                    DispatchLocked(command);
                }
                else
                {
                    _txBuffer.Append(c);
                }
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public void ClearBuffer()
    {
        lock (_gate) _rx.Clear();
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        return ValueTask.CompletedTask;
    }

    private void DispatchLocked(string command)
    {
        _sent.Add(command);

        // Scripted exchange wins — including scripted AT commands (e.g. "ATMA" that must
        // NOT get an "OK>" reply because monitoring mode streams instead of prompting).
        if (_script.Count > 0 && _script.Peek().Command == command)
        {
            var (_, response) = _script.Dequeue();
            EnqueueLocked(response);
            return;
        }

        if (_autoResponses.TryGetValue(command, out var canned))
        {
            EnqueueLocked(canned);
            return;
        }

        var trimmed = command.Trim();

        if (AutoRespondToAtCommands && trimmed.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
        {
            EnqueueLocked(DefaultAtResponse);
            return;
        }

        // Bare CR (parser resync / monitoring exit) — answer with a prompt.
        if (AutoRespondToAtCommands && trimmed.Length == 0)
        {
            EnqueueLocked("\r>");
            return;
        }

        throw new InvalidOperationException(
            $"ReplayElmTransport received unscripted command '{command}'. " +
            $"Commands so far: {string.Join(" | ", _sent)}");
    }

    private void EnqueueLocked(string response)
    {
        if (response.Length == 0) return;
        foreach (var b in Encoding.ASCII.GetBytes(response)) _rx.Enqueue(b);
        _dataSignal.Release();
    }
}
