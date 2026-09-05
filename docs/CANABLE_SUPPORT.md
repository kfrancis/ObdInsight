# CANable / USB-CAN adapter support

Status as of 2026-09-03. Hardware-verified on a CANable 2.0 (USB `VID_16D0&PID_117E`,
STM32G431) with two firmwares on the bench: stock `canable2-fw` (`16e7497-dirty`) and
ElmüSoft slcan 2.5 (`Slcan: 105`). No vehicle attached yet.

## What works

```
SerialElmTransport (Transports.Serial)   COM port bytes; sync reads on a pool thread
  └─ SlcanFrameSource (Core/Slcan)        C → V (probe) → S6 → open sequence per dialect
     └─ CanMonitor(ICanFrameSource)       same fan-out / cache / typed streams as the ELM path
        └─ LeafAze0CommandSet(source)     broadcast capabilities only (HVAC, VCM, Brake, ABS,
                                          BCM, Charger, MotorController); no UDS
```

- **Console:** `dotnet run --project src/ObdInsight -- --serial=COM7 [--bitrate=500] [--duration=10] [--tx]`
  Listen-only unless `--tx`. Prints the firmware banner, detected dialect, per-ID table, and a
  0x1DB pack voltage/current line when EV-CAN frames are visible.
- **Unit tests:** `SlcanProtocolTests`, `SlcanFrameSourceTests`, `CanMonitorFrameSourceTests`
  (SLCAN bytes → `CanMonitor` → typed decode → Leaf capabilities, over the replay transport).
- **Hardware tests:** `tests/ObdInsight.IntegrationTests/CanableHardwareTests.cs`, opt-in with
  `CANABLE_PORT=COM7`. Needs only the adapter, not a car. 5/5 green on the bench.

## Firmware dialects (the part that bit us)

"SLCAN" is a family. The letters overlap; the safety-relevant ones differ.

| | Lawicel CANUSB | normaldotcom CANable 1.0 / 2.0 stock | ElmüSoft slcan 2.5 |
|---|---|---|---|
| Listen-only open | `L` | `M1` then `O` (**`L` ignored**, channel stays closed) | `M1`+`O` or `OS` (`L` = bus-load report!) |
| Command ACK | CR / BEL | **none at all** | CR / BEL (or `#`-codes via `MF`) |
| `V` reply | `V1013` | `16e7497-dirty github.com/normaldotcom/canable2.git` | `+Board: Multiboard\tMCU: STM32G431\tDevID: ...\tSlcan: 105\t...` |
| `E` error register | BEL | `CANable Error Register: X` | BEL (use `ME` mode instead) |
| `S7` | 800 kbit/s | 750 kbit/s | 800 kbit/s (Lawicel table) |
| CAN FD frames | – | `d/D`, `b/B` (BRS), `Y2/Y5` data rate | same, plus `y[...]` custom |
| Filters | `M`/`m` acceptance code+mask | none | `F[id,mask]` up to 8 |

`SlcanFrameSource` sends `V` first (unless a `SlcanDialect` is passed), classifies the banner
with `SlcanProtocol.DetectDialect`, and picks the open sequence with `SlcanProtocol.OpenCommands`.
Unknown/silent devices get the Lawicel `L`: on a CANable that leaves the channel closed (no
frames, but nothing transmitted either), which is the right failure direction on a powertrain
bus. `BitrateCommand` refuses 750/800 kbit/s because `S7` is ambiguous.

Sources: `normaldotcom/canable2-fw` `src/slcan.c` + `inc/can.h`, `normaldotcom/cantact-fw`
`src/slcan.c`, netcult.ch/elmue/CANable Firmware Update (all read 2026-09-03), plus the two
banners captured from the bench device.

## Serial-port findings (Windows, .NET 10 `System.IO.Ports`)

- `SerialPort.BaseStream.ReadAsync` **never returns on a quiet port**: it honours neither
  `ReadTimeout` nor the cancellation token once the overlapped read is in flight (measured: a
  500 ms timeout and a 3 s token both blocked past 2 minutes). Synchronous `Read` does honour
  `ReadTimeout` (returned in ~515 ms). `SerialElmTransport.ReadAsync` therefore runs sync reads
  on a pool thread with a 250 ms timeout and loops until data or cancellation; it returns 0 only
  at end of stream (unplugged / closed), and `SlcanFrameSource` ends its stream with
  `MonitoringEndReason.TransportError` on that.
- Handshaking off, DTR/RTS asserted, baud ignored (USB CDC). Unchanged from the first commit.
- Stock firmware sends nothing back for most commands, so "did the open work" is only
  observable as frames arriving. ElmüSoft ACKs with CR, which the source skips as empty lines.

## Which bus you are on

Stock ELM327 adapters sit on OBD pins 6/14 (CAR-CAN). A CANable is a bare transceiver: wire it
to pins 6/14 for the HVAC/BCM/VCM/ABS broadcast set, or to **pins 12/13 (EV-CAN)** for 0x1DB,
0x1DC, 0x1DA, 0x11A, 0x1CA, 0x55A, 0x59E, the set no stock ELM327 can see
(`docs/FRAME_LAYOUT_AUDIT.md`). Same `LeafAze0CommandSet(source)` either way; the capabilities
for the other bus time out on cold cache as they do today. Leaf buses are all 500 kbit/s.

## Not done / next

1. **UDS over raw CAN** (BMS SOC/cells, VIN, DTC, Steering activation). Needs transmit
   (`--tx`, `t7BB8...` lines) plus an ISO-TP layer on `ICanFrameSource`. The generated
   `Query*Async` methods currently assume an `IElmSession`; the cleanest path is an
   `ICanFrameSource`-backed `IElmSession`-shaped adapter or a second UDS transport abstraction.
   Until then a CANable gives broadcast data only, and the console says so.
2. **candleLight (gs_usb) firmware.** ElmüSoft 2.5 also ships as candleLight, which is USB-bulk
   over WinUSB, not a COM port. `SerialElmTransport` cannot talk to it; it needs a
   `Transports.GsUsb` package (WinUSB/LibUsbDotNet, 20-byte host frames, timestamps in
   hardware). Higher throughput (91 % vs 48 % USB efficiency per ElmüSoft) and real Tx
   feedback, but a new transport. slcan is the pragmatic choice until UDS-over-raw-CAN exists.
3. **ElmüSoft extras** worth using once on a car: `F[id,mask]` host filters (offloads a busy
   EV-CAN), `MF` advanced feedback (`#` codes tell you *why* an open failed), `L[interval]`
   bus-load reports, `y[...]`/`s[...]` custom bit timings for FD vehicles.
4. **Real-bus checkpoint:** frame rates and per-ID coverage on CAR-CAN and EV-CAN, FD frame
   count (expect 0 on the Leaf), cross-check 0x1DB against the BMS 2101 values from the BLE path.
5. **Reconnect layer:** `SlcanFrameSource` ends with `TransportError` on unplug; nothing yet
   re-opens the port. `VehicleConnection` currently owns ELM vehicle recovery, not raw SLCAN recovery.
