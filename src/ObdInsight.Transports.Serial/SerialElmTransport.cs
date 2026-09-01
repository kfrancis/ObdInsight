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

            // ReadAsync is cancelled by the caller's token rather than by a port timeout, but
            // SerialPort still needs finite values or a read can block past cancellation.
            ReadTimeout = 500,
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
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        var port = _port;
        if (port is null || !port.IsOpen)
        {
            return 0;
        }

        try
        {
            return await port.BaseStream.ReadAsync(buffer, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            // A quiet bus is not an error; report "nothing yet" and let the caller loop.
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The device was unplugged mid-read. Surface it as end-of-stream so a capture loop
            // terminates cleanly instead of spinning on a dead handle.
            _logger?.LogWarning(ex, "Serial read failed on {Port}; treating as end of stream", _portName);
            return 0;
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
