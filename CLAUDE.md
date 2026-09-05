# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.
Last verified against the codebase: 2026-07-18. If this file contradicts the code, trust the
code and fix this file.

## What this project is

ObdInsight talks to EVs (currently Nissan Leaf AZE0; Honda CR-V stubbed) through ELM327-family
BLE OBD-II adapters. The actively developed artifact is a **Windows console app**
(`src/ObdInsight`, Spectre.Console). `src/ObdInsight.Maui` is an untouched template shell with
no project references — do not assume the MAUI app works.

`AUDIT.md` (repo root) tracks the improvement plan and what has been fixed;
`docs/STREAMING_MONITOR_DESIGN.md` is the streaming-API design (P1–P4 implemented:
shared monitor, typed layer, capability migration, `SuspendAsync` UDS arbitration,
hardware filter rotation, and the P4 consumer streaming surface — `StreamStatusAsync`
on the broadcast capabilities, typed `ITelemetrySession.Stream<T>`, short-frame decoding); `docs/EVTESTDRIVE_ROADMAP.md` is the consumer-app readiness plan.

## Solution layout

| Project | What it is |
|---|---|
| `src/ObdInsight.Core` | The library: ELM327 session, protocols, vehicle capabilities, Leaf implementation. net10.0, no UI/platform deps, logging via `ILogger` (never Serilog/Console here) |
| `src/ObdInsight` | Windows console app: Spectre UI, Serilog wiring, diagnostic runs. Owns no transports — they come from the `Transports.*` packages |
| `src/ObdInsight.SourceGeneration` | Roslyn incremental generators (netstandard2.0): CAN signal decoders + UDS query methods. Analyzer-only reference from Core; compiles the Annotations sources as linked source |
| `src/ObdInsight.Annotations` | Runtime annotations (net10.0, dependency-free): `[CanFrame]`/`[CanSignal]`/`[Uds*]` attribute types + `CanBits`. Namespaces stay `ObdInsight.SourceGeneration.*` (generator matches by full name) |
| `src/ObdInsight.Telemetry` | Consumer telemetry facade (net10.0, refs Core): `ITelemetrySession` — cadence-tiered polling, decimal DTOs, availability report, snapshots. See `docs/TELEMETRY_SESSION_DESIGN.md` |
| `src/ObdInsight.Simulation` | Shippable sim package (net10.0, refs Core, no test deps): `ReplayElmTransport` (scripted, test workhorse), `LeafGoldenData` (golden captures), `SimulatedLeafAze0Transport` + `LeafDriveProfile` (time-driven fake Leaf for zero-hardware dev) |
| `src/ObdInsight.Transports.WindowsBle` | Windows BLE transport on WinRT (`Windows.Devices.Bluetooth`): `BleElmTransport` + `BleScanner`. Extracted from the console app 2026-08-31; logs via `ILogger`, not Serilog |
| `src/ObdInsight.Transports.Serial` | COM-port `IElmTransport` (`System.IO.Ports`) for USB-CAN adapters (CANable, SLCAN firmware) and serial ELM327s. Pairs with `SlcanFrameSource` (Core) → `CanMonitor(ICanFrameSource)`. Hardware-verified 2026-09-03; see `docs/CANABLE_SUPPORT.md` |
| `src/ObdInsight.Transports.Ble` | Cross-platform BLE transport (net10.0;-android;-ios) on Plugin.BLE: GATT profile table + pure auto-probe resolver, `PluginBleElmTransport`. See `docs/BLE_TRANSPORT_DESIGN.md` |
| `src/ObdInsight.DevTools` | Windows diagnostic console. Partially ported to current architecture; several commands stubbed; `*.cs.broken` files are dead old code |
| `src/ObdInsight.Maui` | Empty MAUI template. `src/ObdInsight.Drivers` is an empty leftover folder |
| `tests/ObdInsight.Tests` | Deterministic unit tests (TUnit) — run these. Test infra (`ReplayElmTransport`, `LeafGoldenData`) comes from `src/ObdInsight.Simulation` (the former `tests/ObdInsight.Tests.Base` was folded into it 2026-07-19) |
| `tests/ObdInsight.IntegrationTests` | Hardware tests — auto-skip unless `LEAF_BLE_ADDRESS` env var set (needs a real Leaf + BLE adapter) |
| `tests/ObdInsight.SourceGeneration.Tests` | Generator snapshot tests (Verify) |

Solution file is `ObdInsight.slnx` (XML format, no `.sln`). SDK pinned by `global.json` to
.NET 10 (`rollForward: latestFeature`); projects target net10 (SourceGeneration stays netstandard2.0).

## Build and test

```powershell
dotnet build ObdInsight.slnx          # builds everything incl. MAUI (needs mobile workloads; may fail)
dotnet build tests/ObdInsight.Tests   # transitively builds Core, app, SourceGeneration, Tests.Base

# TESTS: `dotnet test` DOES NOT WORK for these TUnit/Microsoft.Testing.Platform projects
# under the .NET 10 SDK ("Testing with VSTest target is no longer supported").
# Test projects are exes — run them:
dotnet run --project tests/ObdInsight.Tests -c Debug
dotnet run --project tests/ObdInsight.SourceGeneration.Tests -c Debug
dotnet run --project tests/ObdInsight.IntegrationTests   # skips everything without LEAF_BLE_ADDRESS (MTP exit code 8 = "zero tests ran" — expected)

# Filter: dotnet run --project tests/ObdInsight.Tests --no-build -- --treenode-filter "/*/*/*/*NameFragment*"
```

CI (`.github/workflows/ci.yml`): windows-latest, builds non-MAUI projects, runs unit +
source-generator suites. Integration tests compile-only in CI.

**Verify snapshot workflow** (source-generator tests): a failing snapshot writes
`*.received.cs` next to the test. Review the diff, then rename received → verified to accept.
Never hand-edit `*.verified.cs`.

## Architecture (bottom-up)

Consumer recovery now uses `VehicleConnection` in Telemetry (see
`docs/RESILIENCE_DESIGN.md`). It owns fresh transport/framing/initialized ELM/detected
command-set/telemetry generations. `ReconnectingElmTransport` was removed: interrupted
I/O is never redirected to a replacement. `IVehicleCommandSet` is async-disposable;
Leaf command-set disposal owns monitor disposal. Recording explicitly starts new
subscriptions after a generation ends. Lower-level expert composition remains available.

```
IElmTransport            byte I/O (BLE impls in Transports.WindowsBle/Ble, SerialElmTransport in
                         Transports.Serial; ReplayElmTransport in Simulation for tests)
  ├─ SlcanFrameSource    raw USB-CAN path (CANable): ICanFrameSource, firmware-dialect handshake,
  │                      feeds CanMonitor(ICanFrameSource) directly — no ElmSession, no UDS
  └─ ElmFramer           CR/prompt framing, carry-over buffering (bytes past a delimiter are preserved)
    └─ ElmSession        init, protocol detect/lock, query vs monitoring state machine, 4-level recovery
                         optional IEcuWakeupStrategy (vehicle-specific probe, e.g. LeafBmsWakeupStrategy)
                         MonitorFramesAsync + LastMonitoringEndReason
       ├─ EcuContext     per-ECU headers/filters/flow control (presets in LeafAze0Contexts, EcuContext statics)
       ├─ CanMonitor     long-lived monitoring: one read loop, per-subscriber bounded channels (drop-oldest),
       │                 latest-frame cache, BUFFER FULL auto-restart, EndReason. Typed streams via
       │                 CanMonitor.Subscribe<T>() / TryGetLatest<T>() for ICanFrame<T> frame types;
       │                 StreamSnapshots() backs the capabilities' StreamStatusAsync
       └─ Capabilities   IHvac, IBatteryManagementSystem, ... (vehicle-specific impls under
                         Vehicles/Implementations/), wired by LeafAze0CommandSet,
                         looked up via VehicleSession.TryGet<T>()
```

Layering rules (enforced; audit tracks violations): `Communication/` contains **no vehicle
names** — vehicle-specific wakeup goes through `IEcuWakeupStrategy`. Core has **no
Console/Serilog** — inject `ILogger<T>` (defaults to no-op).

## Source generation

Frame definitions are declarative, DBC-shaped, in `Frames/*.cs`:

```csharp
[CanFrame(0x1DB, Description = "...")]
public partial class BatteryFrame_1DB_AZE0
{
    [CanSignal(13, 11, IsSigned = true, Factor = 0.5, Unit = "A", MinValue = -400, MaxValue = 500)]
    public partial double Current { get; init; }
}
```

- `CanSignalGenerator` emits `Parse(ReadOnlySpan<byte>)`, a per-namespace `CanBits` helper, and
  a `CanFrameRouter`. When the compilation defines `ObdInsight.Core.Protocols.ICanFrame<TSelf>`
  (Core does), frames also implement it + `FrameCanId` — that's what typed `Subscribe<T>()` uses.
- `UdsGenerator` emits `Query{Name}Async` methods from `[UdsService]`/`[UdsPid]`/`[UdsField]`
  (see `LeafBmsDiagnostics` in `BmsFrames.cs`). Partial classes must supply
  `_session`/`_context`; generated code uses Core's strict ISO-TP parser.
  Queries return `Observed<Response?>`, preserving invalid-query evidence; see
  `docs/OBSERVATION_SEMANTICS.md`. Match generator and Core package versions.
- `MinValue`/`MaxValue` are **documentation only** — no runtime validation is emitted.
- **Bit order:** both DBC conventions are supported. `ByteOrder = CanByteOrder.Motorola`
  (DBC `@0`) makes the start bit the signal's MSB; the default `Intel` (DBC `@1`) makes it
  the LSB. Both number bits the same way — bit `N` = byte `N/8`, bit `N%8`, bit 7 being
  that byte's MSB. Most Leaf signals are Motorola; see `CanByteOrder` for why the
  hand-conversion this replaced produced several wrong layouts.
- **Limitations:** the generator places no constraint on CAN ID width, but only 11-bit IDs
  are defined and tested today.
- Generators report no diagnostics; malformed attributes are silently skipped. Enum named
  arguments arrive from Roslyn as boxed ints — convert to member names before string-matching
  (two past production bugs came from getting this layer wrong; see AUDIT.md C1 and the UDS
  FrameType finding).

## Testing conventions

- Unit tests must exercise **production code** — never re-implement parsers test-side. Drive
  `ElmSession`/capabilities through `ReplayElmTransport`
  (`src/ObdInsight.Simulation/ReplayElmTransport.cs`): scripted `Expect(cmd, response)`
  exchanges (responses include the `\r\r>` prompt), lenient auto-`OK` for unscripted AT
  commands, `EnqueueIncoming()` for monitoring frames, scripted `Expect("ATMA", "")` keeps
  monitoring silent.
- Golden captured data lives in `LeafGoldenData` (data only, no logic).
- TUnit specifics: `[Timeout(30_000)]` requires each test method to take a
  `CancellationToken` parameter. Assertions are `await Assert.That(...)`.
- The only sanctioned test-side parser copy is `BmsParsingHelpers` inside the
  IntegrationTests project (quarantined; see its header comment).

## Common tasks

- **Add a CAN frame/signal:** define in the vehicle's `Frames/*.cs` with `[CanFrame]`/
  `[CanSignal]` (partial class + partial properties). Generator does the rest. Add a decode
  unit test with hand-computed bytes (see `GeneratedFrameDecodingTests`).
- **Add a capability:** interface in `VehicleCapabilities.cs` (keep records physics-only —
  no vehicle-specific fields), implementation under the vehicle's `Capabilities/`, register in
  the vehicle's `CommandSet`.
- **Add a vehicle:** profile extending `VehicleProfile` (VIN detection), contexts, frames,
  capabilities, command set; wakeup quirks go in an `IEcuWakeupStrategy` implementation.
- **Change generator output:** update/add snapshot tests; accept baselines deliberately in a
  reviewed commit.

## Gotchas

- ELM327: commands CR-terminated; responses end with `>`; monitoring mode streams without
  prompts; `BUFFER FULL` means the adapter exited monitoring itself.
- **EV-CAN broadcast frames don't appear in passive monitoring with stock ELM327 adapters**
  (hardware-confirmed 2026-07-18, Veepeak BLE): 0x1DB, 0x1DC, 0x1DA, 0x11A, 0x1CA, 0x55A,
  0x59E were absent all session. Stock adapters wire OBD pins 6/14 = CAR-CAN only; EV-CAN
  is present on OBD pins 12/13 and needs a rewired/modified adapter to monitor. EV-CAN
  *data* is still reachable on stock adapters via active UDS queries over CAR-CAN (BMS
  79B→7BB works — how LeafSpy-style apps and our BMS capability get SOC/cells). Broadcast
  capabilities that depend on those IDs (MotorController 1DA/55A, Vcm gear 11A, Brake 1CA)
  time out on data-absence until UDS alternatives exist; their frame definitions stay for
  tests/modified-adapter transports. See `docs/FRAME_LAYOUT_AUDIT.md`.
- `ElmSession` is not thread-safe; query and monitoring modes are mutually exclusive
  (`QueryAsync` throws while monitoring). Arbitration exists: `CanMonitor.SuspendAsync` +
  `MonitorSuspendingElmSession` decorator let UDS capabilities coexist with a running monitor.
- Test fixtures/launchSettings contain a hardcoded adapter MAC + a real VIN (audit M3.5:
  scrub before making the repo public).
- Stale docs exist: `README.md` (old architecture diagram), `tests/ObdInsight.Tests/README.md`.
  `src/ObdInsight/ARCHITECTURE.md` predates `CanMonitor` but is otherwise accurate.
- **CANable firmware has no Lawicel `L`**: listen-only is `M1` then `O`; `L` is silently
  ignored and the channel stays closed (stock firmware ACKs nothing). `SlcanFrameSource`
  probes `V` and picks the sequence per `SlcanDialect`. ElmüSoft slcan 2.5 ACKs with CR/BEL
  and rejects `E`. `S7` differs between firmwares (750 vs 800 kbit/s) — `BitrateCommand` refuses it.
- **`SerialPort.BaseStream.ReadAsync` never returns on a quiet port on Windows** (ignores
  `ReadTimeout` and cancellation once in flight). `SerialElmTransport` uses synchronous reads
  on a pool thread; do not "simplify" it back to `ReadAsync`.
- NU1900 warnings about `nuget.telerik.com` are machine-local feed noise — ignore.
