# ObdInsight.Transports.Serial

Serial-port transport for [ObdInsight](https://github.com/kfrancis/ObdInsight): an
`IElmTransport` over `System.IO.Ports`, for USB-CAN adapters that enumerate as a COM port
(CANable and compatible, SLCAN firmware) and for serial ELM327 devices.

Kept out of `ObdInsight.Core` so Core stays free of platform dependencies — `System.IO.Ports`
carries native runtime assets per OS.

Hardware-verified 2026-09-03 on a CANable 2.0 with stock `canable2-fw` and with ElmüSoft
slcan 2.5. Full notes: `docs/CANABLE_SUPPORT.md`.

## Usage

`IElmTransport` is byte I/O, so the transport is protocol-agnostic. Pair it with
`SlcanFrameSource` for a USB-CAN adapter, and feed that to `CanMonitor` for fan-out, the
latest-frame cache and typed streams — the same consumer surface the ELM327 path uses:

```csharp
await using var transport = new SerialElmTransport("COM7");
await transport.OpenAsync(ct);

// Probes the firmware with 'V', picks the right listen-only sequence for it.
await using var source = new SlcanFrameSource(transport);            // listen-only by default
await using var monitor = new CanMonitor(source);
await monitor.StartAsync(ct);
Console.WriteLine($"{source.FirmwareVersion} -> {source.Dialect}");

await foreach (var frame in monitor.Subscribe<BatteryFrame_1DB_AZE0>(ct))
{
    Console.WriteLine($"{frame.Voltage:F1} V {frame.Current:F1} A");
}
```

Or the whole Leaf broadcast capability set over the adapter:

```csharp
var commands = new LeafAze0CommandSet(source);   // HVAC, VCM, Brake, ABS, BCM, Charger, Motor
var vehicle = new VehicleSession(commands);      // BMS/VIN/DTC report unsupported (UDS needs transmit)
```

or with `ElmFramer` for a serial ELM327.

Discovering ports:

```csharp
foreach (var name in SerialElmTransport.AvailablePorts())
{
    Console.WriteLine(name);
}
```

Console app: `dotnet run --project src/ObdInsight -- --serial=COM7 [--bitrate=500] [--duration=10] [--tx]`.

## Notes for USB-CAN adapters

**Firmware dialect matters.** CANable stock firmware has no Lawicel `L`; listen-only there is
`M1` then `O`, and `L` leaves the channel silently closed. `SlcanFrameSource` detects the
firmware from its `V` banner (`SlcanDialect`) unless told explicitly. See `docs/CANABLE_SUPPORT.md`
for the full comparison table.

**Flow control is off, deliberately.** CANable firmware asserts neither RTS/CTS nor DTR/DSR.
With handshaking enabled a write blocks forever waiting for a signal the device never sends.

**The baud rate is ignored.** A USB CDC device runs at USB speed regardless; the parameter
exists only because `SerialPort` requires one. The CAN bitrate is set through the SLCAN
protocol (`S6` for 500 kbit/s; `SlcanProtocol.BitrateCommand(500)`), not here.

**Reads are synchronous on a pool thread.** On Windows `SerialPort.BaseStream.ReadAsync`
honours neither `ReadTimeout` nor cancellation once in flight — it never returns on a quiet
port (measured). `ReadAsync` here loops a 250 ms synchronous read until data arrives or the
token is cancelled.

**Unplugging is reported as end-of-stream.** A read on a vanished device returns 0 rather than
throwing; `SlcanFrameSource` turns that into `MonitoringEndReason.TransportError` so a capture
loop terminates instead of spinning.

## Safety

`SlcanFrameSource` opens **listen-only** unless told otherwise. On a vehicle powertrain bus that
distinction matters: those buses carry torque demand and relay control, and a transmitting node
is a physical-safety concern rather than a data one. Opening for transmission has to be
requested explicitly, and an unidentified firmware gets the one open command (`L`) that cannot
open normal mode by accident.
