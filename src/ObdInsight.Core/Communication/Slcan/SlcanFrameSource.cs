using System.Runtime.CompilerServices;
using System.Text;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using Microsoft.Extensions.Logging;

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
///         Opened listen-only by default. On an SLCAN device that is a first-class protocol
///         command (<c>L</c>) with unambiguous meaning, unlike the ELM327's <c>AT CSM</c> whose
///         polarity varies by firmware version and has to be verified per adapter. Transmitting on
///         a powertrain bus is a physical-safety matter, so the safe mode is the default and
///         opening for transmission has to be asked for explicitly.
///     </para>
/// </remarks>
public sealed class SlcanFrameSource : ICanFrameSource
{
    private readonly ILogger<SlcanFrameSource>? _logger;
    private readonly string _bitrateCommand;
    private readonly bool _listenOnly;
    private readonly IElmTransport _transport;

    private bool _started;

    /// <summary>Carries a partial line between reads, so a frame split across two reads survives.</summary>
    private readonly StringBuilder _carryOver = new();

    public SlcanFrameSource(
        IElmTransport transport,
        string bitrateCommand = SlcanProtocol.Bitrate500K,
        bool listenOnly = true,
        ILogger<SlcanFrameSource>? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _bitrateCommand = bitrateCommand;
        _listenOnly = listenOnly;
        _logger = logger;
    }

    /// <summary>
    ///     Frames the device reported as CAN FD. Counted rather than dropped silently: an FD frame
    ///     arriving on a bus believed to be classic CAN is worth knowing about, and it is the
    ///     signal that a vehicle needs FD-capable hardware.
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
        await SendAsync(_bitrateCommand, ct);
        await SendAsync(_listenOnly ? SlcanProtocol.OpenListenOnly : SlcanProtocol.OpenNormal, ct);

        _started = true;
        LastEndReason = MonitoringEndReason.None;

        _logger?.LogInformation(
            "SLCAN opened, bitrate command {Bitrate}, {Mode}",
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

    /// <inheritdoc />
    public async IAsyncEnumerable<RawCanFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[4096];

        while (!ct.IsCancellationRequested)
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

            if (read <= 0)
            {
                continue;
            }

            _carryOver.Append(Encoding.ASCII.GetString(buffer, 0, read));

            // SLCAN terminates every response with CR. Anything after the last CR is an
            // incomplete line and stays buffered for the next read.
            var text = _carryOver.ToString();
            var lastTerminator = text.LastIndexOf('\r');
            if (lastTerminator < 0)
            {
                continue;
            }

            _carryOver.Clear();
            _carryOver.Append(text[(lastTerminator + 1)..]);

            foreach (var line in text[..lastTerminator].Split('\r'))
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

        LastEndReason = MonitoringEndReason.Stopped;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    private async ValueTask SendAsync(string command, CancellationToken ct)
    {
        await _transport.WriteAsync(Encoding.ASCII.GetBytes(command), ct);
        await _transport.FlushAsync(ct);
    }
}
