using System.IO.Ports;
using Microsoft.Extensions.Logging;
using ObdInsight.Core.Communication.Elm327;

namespace ObdInsight.Transports.Serial;

/// <summary>
///     <see cref="IElmTransport" /> over a serial port, for USB-CAN adapters that enumerate as a
///     COM port (CANable and compatible) and for serial ELM327 devices.
/// </summary>
/// <remarks>
///     <para>
///         The interface is byte I/O, so nothing here is protocol-specific: pair it with
///         <c>SlcanFrameSource</c> for a CANable, or with <c>ElmFramer</c> for an ELM327.
///     </para>
///     <para>
///         A USB CDC device is not a real UART, so the line settings below are mostly ceremony -
///         the baud rate in particular is ignored by the device and exists only because the API
///         demands one. What does matter is that handshaking is off: CANable firmware asserts
///         neither RTS/CTS nor DTR/DSR, and leaving flow control enabled makes writes block
///         forever waiting for a signal that never arrives.
///     </para>
/// </remarks>
public sealed class SerialElmTransport : IElmTransport
{
    /// <summary>
    ///     Ignored by USB CDC devices, which run at USB speed regardless. Present because
    ///     <see cref="SerialPort" /> requires a value.
    /// </summary>
    public const int DefaultBaudRate = 115200;

    /// <summary>
    ///     Synchronous read timeout. Bounds cancellation latency for <see cref="ReadAsync" />
    ///     (see its remarks); not a data timeout - a quiet bus just loops.
    /// </summary>
    public const int ReadTimeoutMs = 250;

    private readonly int _baudRate;

    private readonly ILogger<SerialElmTransport>? _logger;
    private readonly string _portName;

    private SerialPort? _port;

    public SerialElmTransport(
        string portName,
        int baudRate = DefaultBaudRate,
        ILogger<SerialElmTransport>? logger = null)
    {
        _portName = portName ?? throw new ArgumentNullException(nameof(portName));
        _baudRate = baudRate;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsOpen => _port?.IsOpen == true;

    /// <inheritdoc />
    public ValueTask OpenAsync(CancellationToken ct)
    {
        if (IsOpen)
        {
            return ValueTask.CompletedTask;
        }

        var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            // Off deliberately: CANable firmware drives none of these lines, and with handshaking
            // enabled a write waits for a signal the device never asserts.
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,

            // ReadAsync polls with synchronous reads (see its remarks); this timeout is how often
            // a blocked read wakes to notice cancellation.
            ReadTimeout = ReadTimeoutMs,
            WriteTimeout = 2000
        };

        try
        {
            port.Open();
        }
        catch
        {
            port.Dispose();
            throw;
        }

        // Anything buffered predates this session and would be parsed as if it were current.
        port.DiscardInBuffer();
        port.DiscardOutBuffer();

        _port = port;
        _logger?.LogInformation("Opened serial port {Port} at {Baud}", _portName, _baudRate);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Blocks until at least one byte arrives or <paramref name="ct" /> is cancelled; returns 0
    ///     only at end of stream (device unplugged or port closed). A quiet bus therefore never
    ///     produces a 0, which keeps the caller's loop asleep rather than spinning.
    ///     Implemented with synchronous reads on a pool thread because
    ///     <see cref="SerialPort.BaseStream" />'s <c>ReadAsync</c> on Windows honours neither
    ///     <see cref="SerialPort.ReadTimeout" /> nor the cancellation token once the overlapped
    ///     read is in flight - it simply never returns on a silent port (measured 2026-09-03 on a
    ///     CANable 2.0: a 3 s token and a 500 ms timeout both blocked indefinitely). The
    ///     synchronous <c>Read</c> does honour the timeout, so the loop wakes every
    ///     <see cref="ReadTimeoutMs" /> to check for cancellation.
    /// </remarks>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var port = _port;
        if (port is null || !port.IsOpen)
        {
            return 0;
        }

        var stream = port.BaseStream;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var read = await Task.Run(() => stream.Read(buffer.Span), ct).ConfigureAwait(false);
                if (read > 0)
                {
                    return read;
                }

                // A synchronous serial read returns 0 only when the stream has ended.
                return 0;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // Quiet bus: nothing arrived within ReadTimeout. Loop to re-check cancellation.
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The device was unplugged mid-read (or the port was closed under us). Surface it
                // as end-of-stream so a capture loop terminates cleanly instead of spinning on a
                // dead handle.
                _logger?.LogWarning(ex, "Serial read failed on {Port}; treating as end of stream", _portName);
                return 0;
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var port = _port;
        if (port is null || !port.IsOpen)
        {
            throw new InvalidOperationException($"Serial port {_portName} is not open.");
        }

        await port.BaseStream.WriteAsync(data, ct);
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken ct)
    {
        var port = _port;
        if (port is not null && port.IsOpen)
        {
            await port.BaseStream.FlushAsync(ct);
        }
    }

    /// <inheritdoc />
    public void ClearBuffer()
    {
        var port = _port;
        if (port is null || !port.IsOpen)
        {
            return;
        }

        try
        {
            port.DiscardInBuffer();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _logger?.LogDebug(ex, "DiscardInBuffer failed on {Port}", _portName);
        }
    }

    public ValueTask DisposeAsync()
    {
        var port = _port;
        _port = null;

        if (port is not null)
        {
            try
            {
                if (port.IsOpen)
                {
                    port.Close();
                }
            }
            catch (Exception ex)
            {
                // Closing a port whose device has already gone throws; nothing useful follows.
                _logger?.LogDebug(ex, "Closing {Port} failed", _portName);
            }

            port.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Serial ports available on this machine, for presenting a choice to an operator.</summary>
    public static string[] AvailablePorts() => SerialPort.GetPortNames();
}
