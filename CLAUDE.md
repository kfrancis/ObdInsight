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
`docs/STREAMING_MONITOR_DESIGN.md` is the streaming-API design (P1 + P2 typed layer
implemented; capability migration pending).

## Solution layout

| Project | What it is |
|---|---|
| `src/ObdInsight.Core` | The library: ELM327 session, protocols, vehicle capabilities, Leaf implementation. net9.0, no UI/platform deps, logging via `ILogger` (never Serilog/Console here) |
| `src/ObdInsight` | Windows console app: BLE transports (`BleElmTransport`, `BleScanner`), Spectre UI, Serilog wiring. NOTE: its transport files declare `ObdInsight.Core.*` namespaces but live in this project |
| `src/ObdInsight.SourceGeneration` | Roslyn incremental generators (netstandard2.0): CAN signal decoders + UDS query methods. Referenced by Core both as analyzer AND runtime lib (runtime ref exists for attribute types + `CanBits`) |
| `src/ObdInsight.DevTools` | Windows diagnostic console. Partially ported to current architecture; several commands stubbed; `*.cs.broken` files are dead old code |
| `src/ObdInsight.Maui` | Empty MAUI template. `src/ObdInsight.Drivers` is an empty leftover folder |
| `tests/ObdInsight.Tests` | Deterministic unit tests (TUnit) — run these |
| `tests/ObdInsight.Tests.Base` | Shared test infra: `ReplayElmTransport`, `LeafGoldenData` |
| `tests/ObdInsight.IntegrationTests` | Hardware tests — auto-skip unless `LEAF_BLE_ADDRESS` env var set (needs a real Leaf + BLE adapter) |
| `tests/ObdInsight.SourceGeneration.Tests` | Generator snapshot tests (Verify) |

Solution file is `ObdInsight.slnx` (XML format, no `.sln`). SDK pinned by `global.json` to
.NET 10 (`rollForward: latestFeature`); projects target net8/net9.

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

```
IElmTransport            byte I/O (BLE impls live in the console app; ReplayElmTransport in tests)
  └─ ElmFramer           CR/prompt framing, carry-over buffering (bytes past a delimiter are preserved)
    └─ ElmSession        init, protocol detect/lock, query vs monitoring state machine, 4-level recovery
                         optional IEcuWakeupStrategy (vehicle-specific probe, e.g. LeafBmsWakeupStrategy)
                         MonitorFramesAsync + LastMonitoringEndReason
       ├─ EcuContext     per-ECU headers/filters/flow control (presets in LeafAze0Contexts, EcuContext statics)
       ├─ CanMonitor     long-lived monitoring: one read loop, per-subscriber bounded channels (drop-oldest),
       │                 latest-frame cache, BUFFER FULL auto-restart, EndReason. Typed streams via
       │                 CanMonitor.Subscribe<T>() / TryGetLatest<T>() for ICanFrame<T> frame types
       └─ Capabilities   IHvac, IBatteryManagementSystem, ... (vehicle-specific impls under
                         Vehicles/Vehicles/Implementations/), wired by LeafAze0CommandSet,
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
  `ParseIsoTpFrames`/`ReassembleIsoTpPayload`/`_session`/`_context`.
- `MinValue`/`MaxValue` are **documentation only** — no runtime validation is emitted.
- **Limitations:** 11-bit CAN IDs only; Intel (little-endian) bit order only — no Motorola
  support. Bit 0 = LSB of byte 0.
- Generators report no diagnostics; malformed attributes are silently skipped. Enum named
  arguments arrive from Roslyn as boxed ints — convert to member names before string-matching
  (two past production bugs came from getting this layer wrong; see AUDIT.md C1 and the UDS
  FrameType finding).

## Testing conventions

- Unit tests must exercise **production code** — never re-implement parsers test-side. Drive
  `ElmSession`/capabilities through `ReplayElmTransport`
  (`tests/ObdInsight.Tests.Base/ReplayElmTransport.cs`): scripted `Expect(cmd, response)`
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
  (`QueryAsync` throws while monitoring — arbitration is design-doc P3, not built yet).
- Test fixtures/launchSettings contain a hardcoded adapter MAC + a real VIN (audit M3.5:
  scrub before making the repo public).
- Stale docs exist: `README.md` (old architecture diagram), `tests/ObdInsight.Tests/README.md`.
  `src/ObdInsight/ARCHITECTURE.md` predates `CanMonitor` but is otherwise accurate.
- NU1900 warnings about `nuget.telerik.com` are machine-local feed noise — ignore.
