# Stationary consumer-path smoke test

This Windows console mode exercises the same `VehicleConnection` / `ITelemetrySession`
consumer graph intended for TestDrive. It is not a vehicle-health report, road-test
approval, or Android/iOS BLE validation. Use a parked vehicle, a suitable ignition state,
and verified wiring/bitrate. Do not operate a laptop while driving. Run adapters separately.

## Commands (from the repository root)

First validate the runner without hardware:

```powershell
dotnet run --project src/ObdInsight -c Release -- --smoke=simulation --duration=10
```

Then use your BLE adapter's colon-separated MAC address:

```powershell
dotnet run --project src/ObdInsight -c Release -- --smoke=ble --device=AA:BB:CC:DD:EE:FF --duration=60 --timeout=240
```

This Windows transport supports FFF0 service / FFF1 notify / FFF2 write only. It does
not scan, select a favorite, use legacy ECU wakeups, or automatically try other GATT
profiles. Obtain the address from your existing discovery workflow. A failed VIN or
profile detection is evidence to investigate, not permission to force an unrelated
vehicle profile. The only registered detection profile is currently Nissan Leaf.
BLE actively sends initialization and diagnostic queries; it is not passive capture.

For the SLCAN device, replace the COM port and verify the bus bitrate independently:

```powershell
dotnet run --project src/ObdInsight -c Release -- --smoke=slcan --serial=COM7 --bitrate=500 --duration=60 --timeout=240
```

SLCAN is always listen-only. `--tx` is rejected. Firmware selects Lawicel `L` or
CANable/ElmüSoft `M1` + `O`; see [firmware dialects](CANABLE_SUPPORT.md).
Unknown firmware falls back to Lawicel listen-only, which may leave other devices
closed. No active UDS, VIN detection, or reconnect is implemented on this path.
The Leaf AZE0 broadcast decoder is explicitly configured, **not vehicle detection**.
A different bus/vehicle may produce raw coverage but no supported measurements.

## Lifetime and limits

Leaf ABS, HVAC, and VCM status reads now return the available cache after monitor
startup, without repeated waits for absent frames. Initial snapshots can be partial
or empty; later polls/streams acquire arriving data. Explicit monitor waits remain
available to expert callers. This is a pre-1.0 behavior change, not a signature change.

Both paths take a pre-snapshot, start telemetry, record for the requested duration,
stop telemetry, and take a post-snapshot. SLCAN monitoring stays open through both
snapshots and is then stopped. All owned resources are disposed before a successful
`shutdown-complete` record. SLCAN close is best-effort in the underlying source:
local shutdown completion does not prove the hardware acknowledged its close command.

Duration is 1–1800 seconds (default 60), starting after initial telemetry startup.
The total cooperative deadline includes connection, snapshots, and startup; default
is duration + 180 seconds. `--timeout` must exceed duration and be at most 3600.
Cleanup is joined even after deadline/Ctrl+C; an unresponsive platform operation may
make shutdown outlast that deadline. No task is intentionally abandoned.

BLE loss while recording ends the old generation and waits for the owner's fresh
generation, then explicitly starts a new stream. Recovery time consumes the original
recording window. No interrupted snapshot or individual command is replayed. A loss
during pre/post snapshot fails this run; a recording window ending without a live
generation cannot produce a successful post-snapshot. The owner has bounded recovery
(six retries by default). Windows physical disconnects now notify it; quiet reads
wait for data/cancellation, not a false EOF after 250 ms.

## Evidence and interpretation

ELM-owner initialization emits sanitized `connection-diagnostic` records: transport open,
ELM initialization, detection outcome, and failed-attempt phase. Errors include type/HResult
and a small allowlisted failure category, never exception messages. Records retain event
time and are drained at the next normal smoke output (including terminal failure), so they
are not a live progress feed. The pending diagnostic queue is bounded to 128 records.

Each run creates a unique `.local/smoke/*.jsonl` file. Optional `--output=PATH` uses
create-new semantics: an existing file is never overwritten. Unknown, contradictory,
duplicate, or malformed options fail before hardware access. No legacy debug logger
is installed in smoke mode.

The source-generated JSON schema (version 1, tooling-local) includes:

- Stage and write time; snapshot/batch publication time separately.
- Connection generation for BLE/simulation (owner-local, not a durable vehicle ID).
- Per-signal scalar/vector/boolean values, acquisition metadata, quality, age, freshness.
- VIN-read boolean only, and stored/pending DTC outcome statuses, not a health verdict.
- SLCAN firmware dialect, observed per-CAN-ID counts, skipped CAN-FD/non-frame counts.
- Terminal result or failure stage/category, without platform exception messages.

It omits VINs, MACs, device names, serial-port names, raw firmware banners (which can
contain device IDs), raw CAN payloads, stack traces, and arbitrary object dumps.
Telemetry remains potentially sensitive operational data; review before sharing.
Signals retain the library's normalized units; see `TelemetrySignal` and
[observation semantics](OBSERVATION_SEMANTICS.md). Freshness is assessed at publication,
not when a saved file is later read. Missing/unsupported/stale data is not zero.

Streams have bounded drop-oldest buffers. Counts are subscriber observations, not
lossless wire totals; output backpressure can lose batches/frames. This runner is not
a durable drive recorder. Frame coverage spans pre/start/record/post, not just the
timed recording window. A firmware banner is deliberately not retained.

Exit codes: **0** = workflow completed with at least one nonempty recorded telemetry
value (which can be stale/partial; inspect evidence); **2** = workflow completed but
no measurements; **1** = failed/cancelled/deadline/output error. None means the car
is healthy. An interrupted run may have useful partial JSONL; absence of the terminal
record must not be treated as success.

## Stationary acceptance checklist

1. Simulation exits 0; file contains pre-snapshot, batches, post-snapshot, shutdown-complete.
2. BLE reaches generation-ready with VIN-read true and expected measurement evidence.
   Inspect DTC outcomes and unsupported signals rather than treating null as healthy.
3. On a separate longer BLE run, deliberately disconnect/reconnect the adapter while
   recording. Expect generation-ended followed by a larger generation number and a
   new stream. Never merge stale samples across the gap as uninterrupted acquisition.
4. On a separate run, press Ctrl+C during recording. Expect exit 1 and no lingering
   console process; start another run to check the adapter can be opened again.
5. SLCAN records the expected dialect and bus IDs; inspect decoded measurement quality.
   Zero frames can mean wiring, bitrate, ignition state, or an unsupported firmware
   open command—not absence of vehicle faults. CAN-FD is counted, not decoded.
6. Retain the sanitized JSONL and separately note adapter model/firmware, bus, ignition
   state, and observed shutdown behavior. Do not include the VIN or MAC when sharing.

Real GATT disconnect timing, CCCD cleanup, and firmware behavior remain hardware checks.
This Windows run does not validate Plugin.BLE, mobile permissions/backgrounding,
linker behavior, or an actual Android/iOS published application.
