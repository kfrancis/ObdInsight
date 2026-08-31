using System.Text;

namespace ObdInsight.Core.Communication.Elm327;

/// <summary>
/// An <see cref="IElmTransport"/> decorator that hard-blocks any write which is not on an
/// explicit whitelist of listen-only ELM327 commands.
///
/// This exists because the safety property "we never transmit on the powertrain bus" cannot be
/// carried by a comment or by careful coding in one command. The normal bring-up path
/// (<c>Elm327Adapter.InitializeAsync</c>) issues <c>AT SP 0</c> and <c>0100</c> probes, and
/// <c>0100</c> is a transmitted CAN request frame. On a Leaf EV-CAN pair - which carries motor
/// torque demand (0x1D4) and main-relay control (0x1DB) - injecting request frames is a
/// physical-safety issue, not a data issue.
///
/// Wrapping the transport means the guard holds regardless of what any future command tries to
/// send through the framer above it. A blocked write throws rather than being silently dropped:
/// a caller that believes it configured the adapter and did not must fail loudly.
/// </summary>
public sealed class ListenOnlyElmTransport : IElmTransport
{
    /// <summary>
    /// Commands permitted while listen-only is armed, in normalized form (uppercase, all
    /// whitespace removed). Deliberately minimal.
    ///
    /// Notably absent and therefore blocked:
    ///   ATSP0  - protocol auto-detect, which probes the bus
    ///   ATSH / ATCRA with an argument beyond reset, ATFCSH - configuring to transmit
    ///   anything beginning with a hex digit (0100, 2101, ...) - OBD/UDS requests
    /// </summary>
    private static readonly HashSet<string> s_allowed = new(StringComparer.Ordinal)
    {
        "ATZ",      // reset
        "ATE0",     // echo off
        "ATL0",     // linefeeds off
        "ATS0",     // spaces off
        "ATH1",     // headers on (need the CAN ID)
        "ATCAF0",   // auto-formatting off
        "ATSP6",    // force ISO 15765-4 11-bit/500k. NEVER ATSP0.
        "ATCSM1",   // silent monitoring on
        "ATCSM0",   // silent monitoring off - allowed so it can be probed/reported, see note
        "ATCRA",    // reset receive filter (no argument)
        "ATMA",     // monitor all - listen
        "ATI",      // adapter identification, read-only
        "AT@1",     // device description, read-only
    };

    private readonly IElmTransport _inner;

    public ListenOnlyElmTransport(IElmTransport inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>Commands this transport refused, in the order they were attempted.</summary>
    public IReadOnlyList<string> BlockedAttempts => _blocked;

    private readonly List<string> _blocked = [];

    public bool IsOpen => _inner.IsOpen;

    public ValueTask FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public ValueTask OpenAsync(CancellationToken ct) => _inner.OpenAsync(ct);

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) =>
        _inner.ReadAsync(buffer, ct);

    public void ClearBuffer() => _inner.ClearBuffer();

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var text = Encoding.ASCII.GetString(data.Span);
        var normalized = Normalize(text);

        // A bare CR is how monitoring is stopped - any character terminates AT MA.
        if (normalized.Length == 0)
        {
            return _inner.WriteAsync(data, ct);
        }

        if (!s_allowed.Contains(normalized))
        {
            _blocked.Add(normalized);
            throw new InvalidOperationException(
                $"Listen-only mode is armed: refusing to send '{text.Replace("\r", "\\r")}'. " +
                "Only the listen-only bring-up whitelist may be transmitted while armed. " +
                "Disarm listen-only mode if you intend to query the vehicle.");
        }

        return _inner.WriteAsync(data, ct);
    }

    /// <summary>
    /// Ownership of the wrapped transport stays with the session, so this decorator must not
    /// dispose it - doing so would tear down a connection the session still believes it holds.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Uppercases and strips all whitespace so "AT CRA", "atcra" and "ATCRA\r" compare equal.
    /// </summary>
    private static string Normalize(string command)
    {
        var sb = new StringBuilder(command.Length);
        foreach (var c in command)
        {
            if (!char.IsWhiteSpace(c))
            {
                sb.Append(char.ToUpperInvariant(c));
            }
        }

        return sb.ToString();
    }
}
