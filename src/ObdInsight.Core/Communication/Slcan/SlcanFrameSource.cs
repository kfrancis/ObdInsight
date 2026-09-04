using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Communication.Slcan;

/// <summary>
///     An <see cref="ICanFrameSource" /> over an SLCAN device - a CANable or compatible adapter
///     presenting itself as a serial port.
/// </summary>
/// <remarks>
///     <para>
///         Reuses <see cref="IElmTransport" /> purely as byte I/O. Nothing about that interface is
///         ELM-specific; it reads and writes bytes, which is exactly what a COM port does. That
///         also means the existing replay transport can drive this in tests, so the whole path is
///         exercised without hardware.
///     </para>
///     <para>
///         Opened listen-only by default. How that is requested depends on the firmware
///         (<see cref="SlcanDialect" />): Lawicel devices take <c>L</c>, CANable firmware takes
///         <c>M1</c> then <c>O</c> and silently ignores <c>L</c>. Unless a dialect is given, the
///         source asks the device for its version banner first and picks the sequence from the
///         reply. Transmitting on a powertrain bus is a physical-safety matter, so the safe mode
///         is the default and opening for transmission has to be asked for explicitly.
///     </para>
///     <para>
///         Ends its frame stream with <see cref="MonitoringEndReason.TransportError" /> when the
///         transport reports end-of-stream or throws - an unplugged adapter must terminate a
///         capture loop, not spin it.
///     </para>
/// </remarks>
public sealed class SlcanFrameSource : ICanFrameSource
{
    private readonly string _bitrateCommand;

    /// <summary>Carries a partial line between reads, so a frame split across two reads survives.</summary>
    private readonly StringBuilder _carryOver = new();

    private readonly SlcanDialect? _configuredDialect;
    private readonly bool _listenOnly;
    private readonly ILogger<SlcanFrameSource>? _logger;

    /// <summary>
    ///     Complete lines read while waiting for a query reply but not consumed by it. Handed to
    ///     <see cref="ReadFramesAsync" /> first so nothing the device sent is lost.
    /// </summary>
    private readonly Queue<string> _pendingLines = new();

    private readonly IElmTransport _transport;

    private bool _started;

    /// <param name="transport">Byte I/O to the adapter (serial port, or the replay transport in tests).</param>
    /// <param name="bitrateCommand">Nominal bitrate command; see <see cref="SlcanProtocol.BitrateCommand" />.</param>
    /// <param name="listenOnly">Open silent (default) or normal. Normal mode puts acknowledgements on the bus.</param>
    /// <param name="dialect">
    ///     The firmware's command dialect. <c>null</c> (default) probes the device with <c>V</c>
    ///     during <see cref="StartAsync" /> and detects it from the banner.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public SlcanFrameSource(
        IElmTransport transport,
        string bitrateCommand = SlcanProtocol.Bitrate500K,
        bool listenOnly = true,
        SlcanDialect? dialect = null,
        ILogger<SlcanFrameSource>? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _bitrateCommand = bitrateCommand;
        _listenOnly = listenOnly;
        _configuredDialect = dialect;
        Dialect = dialect ?? SlcanDialect.Unknown;
        _logger = logger;
    }

    /// <summary>
    ///     The dialect in use: the one given to the constructor, or the one detected from the
    ///     version banner during <see cref="StartAsync" />. <see cref="SlcanDialect.Unknown" />
    ///     means the device did not identify itself and the Lawicel sequence was used.
    /// </summary>
    public SlcanDialect Dialect { get; private set; }

    /// <summary>The device's reply to <c>V</c>, when probed. Null until <see cref="StartAsync" /> or if it stayed silent.</summary>
    public string? FirmwareVersion { get; private set; }

    /// <summary>How long to wait for the version banner when auto-detecting the dialect.</summary>
    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>
    ///     Frames the device reported as CAN FD (with or without bit-rate switch). Counted rather
    ///     than dropped silently: an FD frame arriving on a bus believed to be classic CAN is
    ///     worth knowing about, and it is the signal that a vehicle needs FD-capable hardware.
    /// </summary>
    public int CanFdFrameCount { get; private set; }

    /// <summary>Lines received that were not frames - banners, errors, transmit acknowledgements.</summary>
    public int NonFrameLineCount { get; private set; }

    /// <inheritdoc />
    public MonitoringEndReason LastEndReason { get; private set; } = MonitoringEndReason.None;

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        // Close first: the device may still be open from a previous process that exited without
        // closing, and an open channel rejects the bitrate command.
        await SendAsync(SlcanProtocol.Close, ct);

        if (_configuredDialect is null)
        {
            var banner = await QueryAsync(SlcanProtocol.Version, ProbeTimeout, ct);
            FirmwareVersion = banner;
            Dialect = SlcanProtocol.DetectDialect(banner ?? string.Empty);
            _logger?.LogInformation("SLCAN firmware banner {Banner} -> dialect {Dialect}",
                banner ?? "(none)", Dialect);
        }

        await SendAsync(_bitrateCommand, ct);

        foreach (var command in SlcanProtocol.OpenCommands(Dialect, _listenOnly))
        {
            await SendAsync(command, ct);
        }

        _started = true;
        LastEndReason = MonitoringEndReason.None;

        _logger?.LogInformation(
            "SLCAN opened ({Dialect}), bitrate command {Bitrate}, {Mode}",
            Dialect,
            _bitrateCommand.Trim(),
            _listenOnly ? "listen-only" : "NORMAL (can transmit)");
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct)
    {
        if (!_started)
        {
            return;
        }

        _started = false;

        try
        {
            await SendAsync(SlcanProtocol.Close, ct);
        }
        catch (Exception ex)
        {
            // Losing the port on the way out is not worth throwing over.
            _logger?.LogDebug(ex, "SLCAN close failed");
        }
    }

    /// <summary>
    ///     Sends a command and returns the first non-empty reply line within
    ///     <paramref name="timeout" />, or null if the device stays silent (CANable stock firmware
    ///     acknowledges nothing, so silence is normal for most commands there).
    ///     Meant for the closed channel - version, error register - because while the channel is
    ///     open the reply interleaves with frames and the first line back may well be one.
    /// </summary>
    public async ValueTask<string?> QueryAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        await SendAsync(command, ct);
        return await ReadLineAsync(timeout, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RawCanFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[4096];
        var reason = MonitoringEndReason.Stopped;

        while (!ct.IsCancellationRequested)
        {
            List<string> lines;
            if (_pendingLines.Count > 0)
            {
                // Lines that arrived alongside a query reply (see ReadLineAsync) come first.
                lines = [.. _pendingLines];
                _pendingLines.Clear();
            }
            else
            {
                int read;
                try
                {
                    read = await _transport.ReadAsync(buffer, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    _logger?.LogWarning(ex, "SLCAN transport failed; ending frame stream");
                    reason = MonitoringEndReason.TransportError;
                    break;
                }

                if (read <= 0)
                {
                    // End of stream: the adapter is gone. The serial transport reports an unplugged
                    // device this way rather than throwing.
                    _logger?.LogWarning("SLCAN transport reported end of stream; ending frame stream");
                    reason = MonitoringEndReason.TransportError;
                    break;
                }

                _carryOver.Append(Encoding.ASCII.GetString(buffer, 0, read));
                lines = DrainCompleteLines();
            }

            foreach (var line in lines)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (SlcanProtocol.TryParseFrame(line, out var frame, out var isCanFd))
                {
                    if (isCanFd)
                    {
                        CanFdFrameCount++;
                    }

                    yield return frame;
                }
                else
                {
                    NonFrameLineCount++;
                }
            }
        }

        LastEndReason = reason;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    /// <summary>
    ///     Splits the carry-over buffer at CR. Anything after the last CR is an incomplete line and
    ///     stays buffered for the next read.
    /// </summary>
    private List<string> DrainCompleteLines()
    {
        var text = _carryOver.ToString();
        var lastTerminator = text.LastIndexOf('\r');
        if (lastTerminator < 0)
        {
            return [];
        }

        _carryOver.Clear();
        _carryOver.Append(text[(lastTerminator + 1)..]);

        return [.. text[..lastTerminator].Split('\r')];
    }

    private async ValueTask<string?> ReadLineAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(timeout);

        var buffer = new byte[512];
        while (true)
        {
            int read;
            try
            {
                read = await _transport.ReadAsync(buffer, window.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                _logger?.LogDebug(ex, "SLCAN transport failed while awaiting a reply");
                return null;
            }

            if (read <= 0)
            {
                return null;
            }

            _carryOver.Append(Encoding.ASCII.GetString(buffer, 0, read));

            var lines = DrainCompleteLines();
            for (var i = 0; i < lines.Count; i++)
            {
                // Lawicel acknowledges with a bare CR (empty line) and rejects with BEL; neither
                // is the answer being waited for.
                var trimmed = lines[i].Trim('\a', ' ');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                // Whatever followed the reply in the same read is not ours to drop.
                for (var j = i + 1; j < lines.Count; j++)
                {
                    _pendingLines.Enqueue(lines[j]);
                }

                return trimmed;
            }
        }
    }

    private async ValueTask SendAsync(string command, CancellationToken ct)
    {
        await _transport.WriteAsync(Encoding.ASCII.GetBytes(command), ct);
        await _transport.FlushAsync(ct);
    }
}
