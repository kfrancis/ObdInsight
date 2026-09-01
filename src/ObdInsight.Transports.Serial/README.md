# ObdInsight.Transports.Serial

Serial-port transport for [ObdInsight](https://github.com/kfrancis/ObdInsight): an
`IElmTransport` over `System.IO.Ports`, for USB-CAN adapters that enumerate as a COM port
(CANable and compatible) and for serial ELM327 devices.

Kept out of `ObdInsight.Core` so Core stays free of platform dependencies — `System.IO.Ports`
carries native runtime assets per OS.

## Usage

`IElmTransport` is byte I/O, so the transport is protocol-agnostic. Pair it with
`SlcanFrameSource` for a USB-CAN adapter:

```csharp
await using var transport = new SerialElmTransport("COM7");
await transport.OpenAsync(ct);

await using var source = new SlcanFrameSource(transport);   // listen-only by default
await source.StartAsync(ct);

await foreach (var frame in source.ReadFramesAsync(ct))
{
    Console.WriteLine($"{frame.CanIdHex} {Convert.ToHexString(frame.Data.Span)}");
}
```

or with `ElmFramer` for a serial ELM327.

Discovering ports:

```csharp
foreach (var name in SerialElmTransport.AvailablePorts())
{
    Console.WriteLine(name);
}
```

## Notes for USB-CAN adapters

**Flow control is off, deliberately.** CANable firmware asserts neither RTS/CTS nor DTR/DSR.
With handshaking enabled a write blocks forever waiting for a signal the device never sends.

**The baud rate is ignored.** A USB CDC device runs at USB speed regardless; the parameter
exists only because `SerialPort` requires one. The CAN bitrate is set through the SLCAN
protocol (`S6` for 500 kbit/s), not here.

**Unplugging is reported as end-of-stream.** A read on a vanished device returns 0 rather than
throwing, so a capture loop terminates cleanly instead of spinning on a dead handle.

## Safety

`SlcanFrameSource` opens **listen-only** unless told otherwise. On a vehicle powertrain bus that
distinction matters: those buses carry torque demand and relay control, and a transmitting node
is a physical-safety concern rather than a data one. Opening for transmission has to be
requested explicitly.
