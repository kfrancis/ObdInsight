# ObdInsight Repository Audit

**Date:** 2026-07-18
**Scope:** Full repository at commit `c608f0e` (working tree has one uncommitted change: `global.json`).
**Method:** Directory-wide discovery, deep reads of the core communication/decoding path (~20% of code that does most of the work: `ObdInsight.Core`, `ObdInsight.SourceGeneration`, console app `Program.cs`, test suite), plus targeted verification of every Critical/High finding. Areas with lighter review are noted in the Repo Map.
**Calibration:** This is a pre-release project mid-migration to a new architecture, with a stated ambition to become a reusable OBD/CAN library ("useful for everyone"). Recommendations are calibrated to that maturity — no production-service ceremony is proposed.

Facts are cited as `file:line`. Judgments are labeled as such.

---

## 1. Executive Summary

Overall health: **C**. The architectural direction is genuinely good — clean layering (transport → framer → session → capability), a modern declarative CAN-signal model backed by an incremental source generator, and a capability pattern that lets app code stay vehicle-agnostic. That direction is undermined by four things: a **confirmed correctness bug in generated signed-signal decoding** that silently corrupts core EV data (battery current, motor torque, RPM); a test suite whose unit tests **assert re-implemented copies of production parsers** while the shipped code path has zero coverage; **no CI whatsoever**, so nothing catches regressions; and top-level docs (README, CLAUDE.md) that **describe an architecture that no longer exists**. Top 3 risks: (1) the signed-decode bug, (2) the untested production parse path with no automated gate, (3) the doc/reality inversion that will misdirect any contributor. Top 3 opportunities: a replay transport that converts ~54 hardware-dependent integration tests into deterministic ones, a small high-leverage fix-and-test pass on the bit-decode layer, and a streaming consumer API — the actual product differentiator for "consume CAN bus messages simply." The foundations deserve the investment; the safety net must come first.

---

## 2. Repo Map

**Purpose:** OBD-II / CAN-bus diagnostic tool for EVs (currently Nissan Leaf AZE0 as the reference vehicle, Honda CR-V stubbed), communicating with ELM327-family BLE adapters.

**Stack:** .NET 9 (C#, nullable enabled everywhere), TUnit test framework, Spectre.Console (console UI), .NET MAUI (template shell only), Roslyn incremental source generators, Serilog, WinRT BLE APIs. Solution file is the new XML format `ObdInsight.slnx` (no `.sln`).

**Architecture (current, actual):**

```
IElmTransport (BLE/BT Classic implementations)
    → ElmFramer            (line/prompt framing)
    → ElmSession           (init, protocol lock, query/monitor state machine, recovery)
    → EcuContext           (per-ECU headers, filters, flow control)
    → Capabilities         (IHvac, IBatteryManagementSystem, ... via [CanFrame]/[CanSignal]
                            source-generated decoders and UDS query generators)
    → VehicleSession       (TryGet<T> capability lookup)
```

**Projects:**

| Path | What it is |
|---|---|
| `src/ObdInsight.Core` | Core library: ELM327 session, protocols (ISO-TP, CAN frames), vehicle capabilities and Leaf AZE0 implementation |
| `src/ObdInsight` | **The active artifact**: Windows console app (Spectre.Console) — transports, scanning, diagnostics UI |
| `src/ObdInsight.SourceGeneration` | Roslyn incremental generators: CAN signal decoders + UDS query methods |
| `src/ObdInsight.Maui` | Untouched MAUI App Accelerator template — no project references, no OBD code (82 lines of non-generated C#) |
| `src/ObdInsight.DevTools` | Windows diagnostic console; partially ported to new architecture, several commands stubbed out |
| `src/ObdInsight.Drivers` | **Empty** — only an `obj/` folder, no source |
| `tests/ObdInsight.Tests` | Unit tests (~19 methods, parsing) |
| `tests/ObdInsight.IntegrationTests` | ~54 tests requiring a real car + BLE adapter |
| `tests/ObdInsight.Tests.Base` | Shared helpers (contains re-implemented parsers — see T1) |
| `tests/ObdInsight.SourceGeneration.Tests` | Generator snapshot tests (Verify) — strongest part of the suite |

**Surprises:**
- The README and CLAUDE.md advertise a MAUI Android/iOS app; the actually-developed artifact is the Windows console app. The MAUI project is an empty template shell.
- Concrete BLE transports live in the **console app** project but are declared in `ObdInsight.Core.Communication.*` namespaces (`src/ObdInsight/Core/Communication/Elm327/BleElmTransport.cs:6`, `.../Bluetooth/BleScanner.cs:5`) — app types masquerading as Core.
- Doubled folder nesting: all vehicle code lives under `src/ObdInsight.Core/Vehicles/Vehicles/`.
- 3,511 lines of dead code checked in as `*.cs.broken` files under DevTools.

**Lighter-review areas:** MAUI platform folders (template stubs), DevTools' Windows BLE stack internals, mockup/reference documents (`vehicle_nissanleaf.cpp`, glossaries). Nothing load-bearing was skipped.

---

## 3. Audit Report

Severity scale: **Critical** (wrong data or silent failure in shipped path) → **High** (blocks the project's stated goals) → **Medium** (friction, debt) → **Low** (polish).

### 3.1 Correctness (Critical findings)

> **Status 2026-07-18: C1, C1b, C1c, and C2 are FIXED** (generator emits `int ReadSigned` with 32-bit-safe sign extension; hand-written and test-copy `CanBits` fixed identically; snapshot baselines re-accepted plus new signed+Factor case; `MinValue`/`MaxValue` docs corrected to documentation-only). Regression coverage: `tests/ObdInsight.SourceGeneration.Tests/CanBitsTests.cs` (bit-level matrix) and `tests/ObdInsight.Tests/NissanLeaf/AZE0/Unit/GeneratedFrameDecodingTests.cs` (real generated `BatteryFrame_1DB_AZE0`/`InvMcFrame_1DA_AZE0` decoders). Verified failing before the fix, green after.

**C1 — Generated `ReadSigned` decodes negative values as garbage when scaled. [Critical] [Fact — verified directly]**
The source generator emits a per-namespace `CanBits` helper whose `ReadSigned` returns `uint` and sign-extends without casting to `int` (`src/ObdInsight.SourceGeneration/CanSignalGenerator.cs:509-519`). For a signed signal with `Factor`/`Offset` (or a `double`/`float` property), the generated decode is `(double)(CanBits.ReadSigned(...) * factor)` (`CanSignalGenerator.cs:428-448`). Because the operand is `uint`, a negative raw value like `-16` promotes to `4294967280` before scaling — the result is a huge positive number, not a negative one.

The hand-written runtime `CanBits` is **correct** (`int` return with cast, `src/ObdInsight.SourceGeneration/CanBits.cs:34-51`) — but generated frame decoders call the generated copy, not this one.

Real signals hit today (all `IsSigned = true` with scaling or wide ranges):
- Battery current, `Factor = 0.5`, range −400..500 A — `HvbatFrames.cs:16` (charge current is *negative*; this is the core EV metric)
- Motor effective torque, `Factor = 0.5`, −274..274 Nm — `InvMcFrames.cs:21`
- Motor RPM, −16382..16382 (negative = reverse) — `InvMcFrames.cs:36`
- Target motor torque, `Factor = 0.25` — `EvVcmFrames.cs:108`
- Motor current ×2 — `CarVcmFrames.cs:159,164`

Consequence: any regen/charging current, reverse RPM, or negative torque decodes silently wrong. No exception, no log — plausible-looking garbage.

**C1b — The test suite enshrines the bug. [Critical] [Fact]**
Verify snapshot baselines contain the buggy `uint ReadSigned` (`tests/ObdInsight.SourceGeneration.Tests/CanSignalGeneratorTests.GeneratesBoolSignal#CanBits_TestNamespace.g.verified.cs:14` and siblings). The only signed-signal test case is an **unscaled `int`** property (`...GeneratesSignedIntSignal#BatteryFrame.g.verified.cs:23`), where a trailing `(int)` cast happens to rescue the value. The broken path — signed + Factor — has no test case. Fixing the generator will churn every snapshot; that churn must be reviewed and accepted deliberately.

**C1c — `bitLen == 32` sign-extension is wrong in both copies. [Medium] [Fact]**
`~((1u << bitLen) - 1)` with `bitLen = 32` relies on C# shift-count masking (`1u << 32` ≡ `1u << 0`), producing mask `0xFFFFFFFF` and clobbering the value (`CanBits.cs:46`, `CanSignalGenerator.cs:515`). `ReadUnsigned` special-cases 32 (`CanBits.cs:73`); `ReadSigned` does not. No current signal uses 32 bits signed — an edge case, but it should be fixed alongside C1.

**C2 — `MinValue`/`MaxValue` promise validation that is never emitted. [Medium] [Fact for the checked case; judgment on breadth]**
`CanSignalAttribute` documents "Values above this are considered invalid" (`Attributes/CanSignalAttribute.cs:62-71`), but the generated `Parse` contains no range checks (verified in the `BatteryFrame` snapshot — decode and assign only). Either emit validation or correct the attribute docs; today the metadata is decorative.

### 3.2 Architecture & Design

**A1 — Vehicle-specific logic inside the generic session layer. [High] [Fact + judgment]**
`ElmSession.TryNissanLeafBmsAsync` hardcodes Leaf BMS headers/flow-control and a Mode 21 probe inside the adapter-generic session (`src/ObdInsight.Core/Communication/Elm327/ElmSession.cs:529-564`), called from the generic wakeup path (`ElmSession.cs:505-508`). This violates the project's own layering (session should be vehicle-agnostic) and won't scale to vehicle #2. Wakeup/probe should be a strategy supplied by the vehicle profile.

**A2 — Per-call monitoring session churn; pull-only API fights broadcast CAN. [High] [Fact + judgment]**
`LeafAze0Hvac.GetStatusAsync` enters monitoring mode, collects ~400 ms of frames, and exits — per call (`.../Capabilities/LeafAze0Hvac.cs:42-86`). Enter/exit each cost ~10 AT commands plus delays and buffer drains (`ElmSession.cs:654-713`, `738-834`). Each capability owns its own `EcuContext`/filter, so reading HVAC + ABS + VCM for a dashboard thrashes modes continuously. Broadcast frames arrive every ~100 ms regardless; the natural model is a long-lived monitoring session with a wide filter and a demux that feeds capability subscribers — plus a streaming surface (`IAsyncEnumerable<T>`/events). This is the single biggest gap between the current API and "a simple way to consume CAN bus messages."

**A3 — 11-bit CAN and Intel byte order only. [Medium] [Fact]**
`TryParseMonitoringFrame` rejects CAN IDs above `0x7FF` (`ElmSession.cs:971-973`) — no 29-bit extended-ID support, which several manufacturers require. The generator reads frames exclusively as `ReadUInt64LittleEndian` (`CanSignalGenerator.cs:524`; `CanBits.cs:70`) and `CanSignalAttribute` has no byte-order property — Motorola (big-endian) signals, common in OEM DBC definitions, cannot be expressed. Acceptable to defer; must be documented as a limitation.

**A4 — Core is not yet library-clean. [Medium] [Fact]**
- Static `Serilog.Log` + `Console.WriteLine` inside Core (`ElmSession.cs:994-1003`) — forces logging choices on any consumer.
- The source-generator project is referenced **twice** — once correctly as an analyzer, once as a normal `ProjectReference` (`ObdInsight.Core.csproj:20-21`, `ObdInsight.csproj:25-26`) — which ships the netstandard2.0 generator assembly (with Roslyn references) in the runtime closure. The runtime need is only the attribute types and `CanBits`; those belong in a tiny annotations assembly or as linked source.

**A5 — Transport implementations misplaced and duplicated. [Medium] [Fact]**
Concrete `BleElmTransport` (327 lines) and `BleScanner` (150 lines) live in the console app but declare `ObdInsight.Core.*` namespaces (paths/lines in Repo Map). DevTools carries a parallel ~2,200-line BLE stack implementing the same `IElmTransport` (`WindowsBleTransport.cs` 784 lines, `WindowsBinaryBleTransport.cs` 479, `WindowsBleScanner.cs` 166, plus profiles/factories). Two Windows BLE implementations to maintain; neither reusable by the MAUI app.

**A6 — `Program.cs` god-file with a hardcoded vehicle. [Medium] [Fact]**
`src/ObdInsight/Program.cs` is 1,074 lines; `RunElm327SessionAsync` spans lines 307–963 with ten copy-pasted diagnostic blocks. A literal VIN `"1N4AZ0CP7HC308656"` and `new NissanLeaf()` are baked in (`Program.cs:276-278`); the generic vehicle-selection path is commented out (`Program.cs:253-274`), making `Application/VehicleSelector.cs` dead code. Two competing VIN parsers exist in the same file (`DecodeVin` at 20–112, `TryParseVin` at 971–1072, the latter writing UI output from inside parsing).

**A7 — Smaller design debts. [Low] [Fact + judgment]**
- "Generic" records drifting Leaf-specific: `VcmStatus.EcoTree`, `EcoIndicator`, `ChargeMode` (`src/ObdInsight.Core/Vehicles/Vehicles/VehicleCapabilities.cs:294-301`). Vehicle #2 will strain these; keep generic records physics-only.
- `BUFFER FULL` during monitoring → silent `yield break` (`ElmSession.cs:893-899`); callers cannot distinguish "adapter died" from "stream ended."
- Doubled folder `Vehicles/Vehicles/` in Core — refactor artifact.

### 3.3 Testing

**T1 — Unit tests assert re-implementations, not production code. [Critical] [Fact]**
`BmsParsingHelpers` re-implements the BMS/ISO-TP parsing ("extracted from LeafAze0Bms" per its own comment, `tests/ObdInsight.Tests.Base/LeafBmsParsingHelpers.cs:9`); VIN parsing is copy-pasted into two test files (`LeafChargerTests.cs:140`, `LeafChargerIntegrationTests.cs:97`). Production `IsoTpParser` and `ElmParsing` have **zero** references from any test. The green unit suite validates the algorithm as copied, not the code that ships — the copies can drift silently.

**T2 — Core session/framer only reachable through real hardware. [High] [Fact]**
No fake or replay `IElmTransport` exists despite the interface; the sole fixture opens a live BLE connection to a hardcoded adapter MAC (`tests/ObdInsight.IntegrationTests/Fixtures/BleSessionFixture.cs:22,52-70`, env override `LEAF_BLE_ADDRESS`). All ~54 integration tests fail hard when the car/adapter is absent — no skip-on-missing-hardware. `ElmSession.QueryAsync`, ISO-TP-through-session, monitoring, and recovery have no deterministic tests at all. Captured session data already exists in the repo (DevTools `Reports/leaf_session_*.txt`, golden lines in `LeafBmsParsingHelpers.cs:17-26`) — the raw material for a replay transport is sitting there unused.

**T3 — `CanBits` untested and triplicated. [High] [Fact]**
The sign-extension/bit-extraction logic exists three times (runtime `CanBits.cs`, test-project copy `tests/ObdInsight.SourceGeneration.Tests/CanBits.cs`, generated per-namespace copy) and none has a direct unit test. This is exactly the code C1 lives in.

**T4 — Weak assertions and missing tooling. [Medium] [Fact]**
Tautological assertions (`IsTypeOf<bool>()` on a bool field, `LeafAze0PassiveMonitoringIntegrationTests.cs:200`; similar at :157-159); two diagnostic "tests" contain no assertions (Console output only, `LeafAze0SessionActivationDiagnosticsTests.cs`); a test named `..._ReturnsErrorCodes` asserts `HasValue == false`. No coverage tooling anywhere (no coverlet, no runsettings). VIN detection in `VehicleProfile`/`VehicleProfileRegistry` untested. Test assembly name typo: `OdbTestApp.Tests`.

**Strength:** the source-generator snapshot tests are genuinely good — Verify baselines (~30), diagnostics assertions, scrubbed headers (`ModuleInitializer.cs:5-25`). The strongest testing in the repo; the model for the rest.

### 3.4 Security

Healthy for the domain — this is a local tool talking to a BLE dongle; there are no secrets, no injection surfaces, no network services, and no tracked credentials (verified repo-wide). One note: a personal BLE adapter MAC `66:1E:87:02:C2:DB` is hardcoded as the default in ~11 places (`src/ObdInsight/Properties/launchSettings.json:5`, both `BleSessionFixture.cs:22` files, `LeafBmsParsingHelpers.cs:14`, committed DevTools session captures) and a real VIN is embedded in `Program.cs:277` and captured reports. Mild privacy leak for a public repo, not a security hole. **[Low]**

### 3.5 Performance

Adequate for a prototype; nothing blocks current use. Noted for later: `BleElmTransport.ReadAsync` busy-polls with `Task.Delay(10)` (`BleElmTransport.cs:240-248`) instead of using the already-wired notification events; synchronous `_bufferLock.Wait()` inside the BLE notification callback (`BleElmTransport.cs:315`); `GetAwaiter().GetResult()` in three `Dispose` methods (`BleScanner.cs:33`, `WindowsBleScanner.cs:45`, `WindowsBinaryBleTransport.cs:388`). The dominant real-world cost is A2's session churn, an architecture item, not a micro-optimization. **[Low–Medium]**

### 3.6 Dependencies

All versions explicitly pinned (good); no floating ranges. Debts: no central package management despite `TUnit 1.12.3` in four projects and `Serilog`/`Spectre.Console` duplicated (`Directory.Packages.props` absent); generator built against `Microsoft.CodeAnalysis.CSharp 4.11.0` but tested against `5.0.0` (`ObdInsight.SourceGeneration.csproj:12` vs `ObdInsight.SourceGeneration.Tests.csproj:11-12`); TFM spread net8.0/net9.0/net9.0-windows; **uncommitted `global.json` bump to SDK `10.0.201`** while no project targets net10 — anyone cloning needs the .NET 10 SDK for no functional reason (open question §6); `sqlite-net-pcl 1.9.172` referenced but unused in the dormant MAUI shell. **[Medium]**

### 3.7 DevEx & Operations

**No CI/CD exists at all** — no `.github/` directory, no pipeline of any kind (verified). Testing evidence is manual (`test-output*.log` at repo root). The only automation is `clean-rebuild.ps1` and a `taskkill`/`ping` pre-build hack in DevTools (`ObdInsight.DevTools.csproj:20-25`). No `TreatWarningsAsErrors`. Strengths: nullable enabled everywhere, `.editorconfig` present, modern `slnx` solution. For a project inviting contributors, absent CI is the single largest process gap. **[High]**

### 3.8 Documentation

- **`CLAUDE.md` — stale, actively misleading. [High] [Fact]** Describes the pre-refactor architecture: `IObdTransport`/`IObdAdapter`/`IVehicleProfile` with PID lists, an `ObdInsight.Drivers` project, `Elm327Adapter`, `ReplayTransport`, `tests/ObdInsight.AdapterComplianceTests/`. Its Key Files Reference (`CLAUDE.md:261-280`) points at paths that do not exist; build commands reference nonexistent test projects (`CLAUDE.md:45-48`).
- **`README.md` — stale. [Medium] [Fact]** Mermaid architecture diagram names defunct types (`README.md:61-70`); MAUI-first framing inverts reality.
- **`tests/ObdInsight.Tests/README.md` — stale. [Low]** Claims `[Skip]`-based integration gating; the mechanism is now project separation (commit `164c394`).
- **`src/ObdInsight/ARCHITECTURE.md` — current and accurate.** The one doc that matches the code; the seed for rewriting the other two.

### 3.9 Dead Code & Hygiene

**[Medium, aggregate] [Facts]** Five tracked `*.cs.broken` files totaling 3,511 lines under DevTools (`NissanLeafCommands.cs.broken` 1,873 among them); `MissingTypeStubs.cs` with five `[Obsolete]` empty types; five gutted stub commands with TODOs; dead `VehicleSelector.cs`; empty `ObdInsight.Drivers` project; `HondaCrv.cs:174` throws `NotImplementedException`; ~7 MB of mockup PNGs at repo root plus 19 screenshots and 2 stray logs inside `src/ObdInsight/`; committed `.claude/` local settings; leftover csproj scaffolding (`Compile Remove="Nissan\NewFolder\**"`, `Folder Include="Nissan\Leaf\"` in `ObdInsight.IntegrationTests.csproj`); orphaned `ObdInsight.sln.DotSettings`. Swallowed exceptions cluster in DevTools (`DevToolsSession.cs:258,265,330,354`, `Elm327AdapterCompat.cs:26-29`) and scan callbacks (`BleScanner.cs:140-143`). `.gitignore` itself is comprehensive; no bin/obj/log files are tracked.

### 3.10 Strengths (preserve these)

- Layered Core design with real seams — capabilities take `IElmSession` by constructor; interfaces exist at every boundary.
- The declarative `[CanFrame]`/`[CanSignal]` model: DBC-shaped (bit position, length, factor, offset, ranges, units), zero reflection, readable frame definitions doubling as documentation (`Frames/*.cs`).
- Capability pattern (`Supports<T>`/`TryGet<T>`) — app code asks "can this car do X," never "is this a Leaf."
- Modern `IIncrementalGenerator` implementations with correct analyzer packaging flags (`IsRoslynComponent`, `EnforceExtendedAnalyzerRules`, `PrivateAssets="all"`).
- `ElmSession`'s explicit query/monitoring state machine and 4-level recovery ladder — hard-won robustness for cheap clone adapters.
- Verify-based snapshot testing of generators.
- Nullable everywhere, pinned dependency versions, comprehensive `.gitignore`, accurate `ARCHITECTURE.md`, real captured vehicle data usable as future test fixtures.

---

## 4. Improvement Strategy

Five themes explain nearly all findings.

### Theme 1 — Decode correctness (C1, C1b, C1c, C2, T3)
**Target:** one authoritative, exhaustively tested bit-decode implementation; generated code provably matches it.
**Principle:** the decode layer is the product's foundation of trust — if numbers can be silently wrong, nothing above matters.
**Trade-off:** snapshot baselines will churn wholesale; accept in one reviewed commit. Defer Motorola byte order until a target vehicle needs it — document the limitation instead.
**Done signals:** matrix unit tests (sign × scaling × widths incl. 32) pass against production `CanBits` and generated output; a signed+Factor snapshot case exists; single `CanBits` source of truth.

### Theme 2 — Test the real code (T1, T2, T4)
**Target:** production parsers tested directly; core session testable without a car.
**Principle:** a test that exercises a copy proves nothing about the shipped path.
**Trade-off:** keep the hardware integration suite — it is valuable validation — but it must not be the *only* coverage of core logic. Don't chase a coverage percentage; chase the parse path.
**Done signals:** `grep` finds no parser re-implementations under `tests/`; `IsoTpParser`, `ElmParsing`, `ElmSession` (via replay transport), and `LeafAze0Bms` have direct deterministic tests; integration tests skip (not fail) when hardware is absent.

### Theme 3 — CI + guardrails (DevEx findings)
**Target:** every push/PR builds and runs unit + source-generator tests automatically.
**Principle:** cheapest possible ratchet — regressions become visible the day they are introduced.
**Trade-off:** no Android/iOS pipeline while the MAUI project is a shell; no coverage gate initially (tooling first, thresholds later).
**Done signals:** a red PR on test failure; `dotnet build` warning-clean or warnings tracked deliberately.

### Theme 4 — Library boundary & consumer API (A1, A2, A4, A5, A7)
**Target:** `ObdInsight.Core` publishable as a clean library; consumers can stream decoded frames simply.
**Principle:** "useful for everyone" means Core dictates no logging stack, contains no vehicle names, and offers push-shaped access to push-shaped data.
**Trade-off:** the shared-monitor/streaming rework is the largest item (XL) — design doc first, land after the safety net (Themes 1–3) exists. Namespace/transport relocation is mechanical but touches many files; sequence it to avoid colliding with the streaming rework.
**Done signals:** Core has no `Console.WriteLine`, no static Serilog, no `Nissan` references outside `Implementations/`; generator is analyzer-only in consuming projects; a sample app subscribes to a decoded-frame stream while multiple capabilities share one monitoring session.

### Theme 5 — Truthful repo (docs + hygiene findings)
**Target:** every doc statement and tracked file reflects the current codebase.
**Principle:** contributors (and future-you) trust the repo's self-description; stale docs are negative documentation.
**Trade-off:** deleting `.cs.broken` files loses nothing (git history retains them) but the recording/replay feature they contain relates to Theme 2 — decide port-vs-delete first (§6).
**Done signals:** every path referenced in CLAUDE.md exists; no `*.broken` tracked; README diagram matches `ARCHITECTURE.md`; MAUI story stated honestly.

**Explicitly not fixing now:** Motorola byte order, 29-bit CAN (document both as limitations until a vehicle profile needs them); sqlite-net-pcl swap (dormant project); BLE busy-poll and Dispose blocking (working; revisit inside the Theme 4 transport consolidation); mobile CI.

---

## 5. Task Plan

### Quick wins (high impact, S effort)

| # | Task | Why |
|---|---|---|
| QW1 — **DONE 2026-07-18** | Five `*.cs.broken` files deleted (3,511 lines; git history preserves them, incl. the recording feature — `ReplayElmTransport` covers replay-testing needs now) | Largest single noise reduction; git keeps history |
| QW2 — **DONE 2026-07-18** | `CLAUDE.md` rewritten from current ground truth: real layout (console-first, MAUI shell), correct test invocation (`dotnet run --project`, SDK-10 `dotnet test` caveat, `LEAF_BLE_ADDRESS` skip gate), current architecture (ElmSession/CanMonitor/capabilities/IEcuWakeupStrategy), source-gen model + limitations, replay-testing conventions, Verify accept workflow, gotchas. Every referenced path exists. | Stops misdirecting every AI-assisted and human session |
| QW3 | Decide `global.json`: commit the SDK-10 bump or revert to 9.0.3xx | Unblocks CI task; removes clone-time surprise |
| QW4 | Remove the duplicate (runtime) `ProjectReference` to SourceGeneration; move attributes + `CanBits` to linked source or a tiny annotations project | Drops Roslyn from runtime closure; verify with `dotnet build` |
| QW5 — **DONE 2026-07-18** | Deleted: `VehicleSelector.cs` (+ its commented-out call block in `Program.cs`, replaced with a pointer comment), `MissingTypeStubs.cs` (zero references verified), empty `ObdInsight.Drivers/`, orphaned `ObdInsight.sln.DotSettings`, stray `obdtest-*.log`/`test-output*.log`; IntegrationTests csproj scaffolding leftovers removed; `src/ObdInsight/.claude/` local settings untracked (kept on disk) + gitignored. Screenshots folder turned out untracked local data — left alone. DevTools stub commands (live menu entries with TODOs) kept — that's the port-or-rebuild decision for DevTools features, separate from dead code. | Hygiene sweep, zero behavior risk |

### M0 — Safety net

| Task | Description | Files | Acceptance | Effort | Risk | Deps |
|---|---|---|---|---|---|---|
| M0.1 CanBits characterization tests — **DONE 2026-07-18** | Matrix tests (signed/unsigned × widths incl. 32 × positive/negative) against production `CanBits`, plus regression tests through real generated frame decoders. Written first (red on the defect), green after M1.1 | `tests/ObdInsight.SourceGeneration.Tests/CanBitsTests.cs`, `tests/ObdInsight.Tests/NissanLeaf/AZE0/Unit/GeneratedFrameDecodingTests.cs` | Tests exist and pass ✔ (CI wiring still pending M0.2) | M | None (test-only) | — |
| M0.2 CI workflow — **DONE 2026-07-18** | GitHub Actions on windows-latest: builds all non-MAUI projects (integration tests compile-only), runs unit + source-generator suites via `dotnet run --project` (MTP exes; `dotnet test` unsupported under SDK 10). QW3 resolved: SDK-10 `global.json` committed; setup-dotnet reads it. | `.github/workflows/ci.yml` | Red PR on failing test ✔ (commands verified locally in Release; first real run happens on push) | S/M | Low | QW3 ✔ |
| M0.3 ReplayElmTransport skeleton — **DONE 2026-07-18** | `tests/ObdInsight.Tests.Base/ReplayElmTransport.cs`: scripted exchanges (script wins over lenient auto-OK for AT commands), unsolicited-frame injection for monitoring, blocking reads honoring cancellation, unscripted non-AT commands fail loudly. Tests: `tests/ObdInsight.Tests/Elm327/ElmSessionReplayTests.cs` — init+lock+query, invalid-response retry path, monitoring stream enter/read/exit. | new files as listed | `ElmSession.InitializeAndLockAsync` + `QueryAsync` + monitoring pass against replay ✔ (26/26 Debug and Release) | M | Low | — |

> **New finding (2026-07-18, discovered building M0.3) — FIXED same day: ElmFramer dropped data after a delimiter.** `ReadUntilAsync` (and `SendAndReadFrameAsync`) returned at the first delimiter and discarded any remaining bytes already read into the local buffer — one transport read spanning two monitoring lines (burst BLE notification) silently lost every frame after the first. Reproduced deterministically via replay transport (hung the monitoring test for its full 30-minute timeout). Fix: persistent carry-over buffer in `ElmFramer` — both read paths consume it before touching the transport, delimiter hits stash the remainder, `ClearBuffer` clears it. Regression test: `ElmSessionReplayTests.MonitoringMode_StreamsFrames_AndExitsCleanly` feeds two frames in one chunk and requires both. Hardware note: fix validated against replay only; live-vehicle monitoring re-check recommended.

### M1 — Correctness

| Task | Description | Files | Acceptance | Effort | Risk | Deps |
|---|---|---|---|---|---|---|
| M1.1 Fix generated `ReadSigned` — **DONE 2026-07-18** | Emitted `int` return + cast mirroring hand-written impl; `bitLen==32` handled; all Verify snapshots re-accepted; signed+Factor snapshot case added. Bit-decode matrix tests (M0.1 scope) landed alongside in `CanBitsTests.cs` + `GeneratedFrameDecodingTests.cs` | `CanSignalGenerator.cs`, snapshots | Matrix fully green; new snapshot covers `(double)(signed * factor)` ✔ | M | Medium (all generated decoders change — that's the point) | M0.1 |
| M1.2 Single-source CanBits | Generated code calls one shared implementation (linked source via QW4's annotations move); delete test-project copy (32-bit fix applied to all three copies in the meantime) | `CanBits.cs`, generator, test copy | One `ReadSigned` implementation greps in repo (plus generated ref) | S | Low | M1.1 ✔, QW4 |
| M1.3 Min/Max decision — **DONE 2026-07-18** (docs path) | Attribute docs corrected: Min/MaxValue are documentation-only ("Valid range" XML remarks); no validation emitted. Emitting real validation remains a possible future enhancement | `CanSignalAttribute.cs` | Docs and behavior agree ✔ | S | Low | — |

### M2 — High leverage

| Task | Description | Files | Acceptance | Effort | Risk | Deps |
|---|---|---|---|---|---|---|
| M2.1 Replace parser re-implementations — **DONE 2026-07-18 (unit scope)** | All three unit test files now exercise PRODUCTION code over replay: BMS Group 01 via `LeafAze0CommandSet`→`LeafAze0Bms`→generated `QueryGroup01Async`; VIN via `LeafAze0VehicleIdentification`; ISO-TP via public `IsoTpParser`. Golden data moved to `Tests.Base/LeafGoldenData.cs` (data only). `BmsParsingHelpers` deleted from shared Tests.Base and quarantined into the IntegrationTests project with a divergence note (its 'H'→'4' quirk repair differs from production's "H"→"48"). **Payoff: immediately surfaced a real production bug — see UDS FrameType finding below.** Remaining follow-up: retarget the hardware integration tests at production parsing and delete the quarantined copy (needs a real vehicle to validate). | unit test files, `LeafGoldenData.cs`, `IntegrationTests/LeafBmsParsingHelpers.cs` | Unit suite asserts production path ✔ (23/23 + 41/41, Debug & Release) | L | Medium | M0.3 ✔ |

> **New finding (2026-07-18, surfaced by M2.1) — FIXED same day: UdsGenerator dropped ConsecutiveFrame-sourced fields.** `UdsGenerator.cs` parsed the `FrameType` named argument via `.ToString()` on the boxed enum value Roslyn provides — yielding `"1"`, never `"ConsecutiveFrame"` — so the CF-sourced branch never matched and such fields decoded from payload offset 0 instead. Concrete impact: Leaf BMS `VoltageVolts` (Group 01, CF3 bytes 0-1) decoded as 0 V in production; nothing caught it because no test exercised the generated query path. Fix mirrors the existing `Type`-case int handling; regression locked by `UdsGeneratorTests.GeneratesConsecutiveFrameSourcedField` (snapshot + content assertion) and the production-path test `LeafBmsGroup01ParsingTests.GetStatus_ExtractsVoltage`. Hardware note: validated against golden capture; live-vehicle spot-check recommended.
| M2.2 Extract Nissan wakeup from ElmSession — **DONE 2026-07-18** | New `IEcuWakeupStrategy` (Communication/Elm327); `TryNissanLeafBmsAsync` deleted from `ElmSession` and ported to `LeafBmsWakeupStrategy` next to Leaf code. `ElmSession` ctor takes optional strategy (old ctor call sites still compile); wired at Leaf-specific sites (console `Program.cs`, DevTools compat shim, both BLE fixtures). All Nissan/Leaf tokens (incl. comments) removed from `Communication/` — grep-verified. Replay test `Initialize_LeafWakeupStrategy_LocksProtocolWhenStandardObdSilent` covers the EV path: 0100 silent → strategy probes 2101 → protocol locked, detection loop skipped. Behavioral parity preserved (incl. the pre-existing quirk that wakeup still sends `AT SP 0` after a strategy lock — noted, not changed). | `IEcuWakeupStrategy.cs`, `LeafBmsWakeupStrategy.cs`, `ElmSession.cs`, 4 wiring sites | No `Nissan`/`Leaf` tokens in `Communication/` ✔; replay test ✔ (24/24 Debug & Release) | M | Medium | M0.3 ✔ |
| M2.3 ILogger in Core — **DONE 2026-07-18** | Core's Serilog package replaced with `Microsoft.Extensions.Logging.Abstractions`. `ElmSession`/`ElmFramer` take optional `ILogger<T>` (NullLogger default; old call sites unaffected); `ElmSession`'s `Console.WriteLine` removed (`EnableDebugLogging` retained for API compat, docs updated). `IsoTpParser` debug logging dropped (pure function); capability/strategy classes keep `Debug.WriteLine` only. Console app bridges via `Serilog.Extensions.Logging.SerilogLoggerFactory` into its existing console+file sinks — visibility preserved. Core is now logging-stack-agnostic. | `ObdInsight.Core.csproj`, `ElmSession.cs`, `ElmFramer.cs`, `IsoTpParser.cs`, 4 capability files, `ObdInsight.csproj`, `Program.cs` | No `Console.WriteLine`/`Serilog` in Core ✔ (grep-verified); 24/24 + 41/41 Debug & Release | M | Low | — |
| M2.4 Streaming monitor design doc — **DONE 2026-07-18 (doc; implementation pending review)** | `docs/STREAMING_MONITOR_DESIGN.md`: `CanMonitor` (long-lived monitoring owner, channel fan-out with drop-oldest, latest-frame cache, `MonitoringEndReason` incl. BUFFER FULL auto-restart), generator extension for typed `Subscribe<T>()`/`TryGetLatest<T>()` via `ICanFrame<T>` static abstracts, capability migration plan (broadcast capabilities become views over shared monitor), 3-phase rollout (P1 core / P2 typed+pilots / P3 full migration+query arbitration), replay-based test matrix, hardware checkpoints. Fixes A2 (session churn) + A7 (silent stream death) when implemented. | `docs/STREAMING_MONITOR_DESIGN.md` | Doc ✔; **P1 implemented 2026-07-18**: `CanMonitor` (Core/Communication/Elm327) + `MonitoringEndReason` + `IElmSession.LastMonitoringEndReason` (A7 fixed at source), 5 replay tests (demux, drop-oldest, latest cache, BUFFER FULL restart-then-give-up, stop/mode-restore) green ×5 runs Debug + Release. P2 done 2026-07-18 (typed layer + HVAC/MotorController migrated to shared-monitor cache views; `LeafAze0CommandSet` owns the `CanMonitor`). P3 arbitration done same day: `CanMonitor.SuspendAsync` + `MonitorSuspendingElmSession` decorator — every capability coexists with the running monitor (whole-model replay test green). P3 broadcast migration completed 2026-07-18: ABS, Brake, BodyControl, Charger, VCM (helper split folded into one class) are cache views; legacy enter/exit code deleted. Session-activation + keep-alive hooks landed same day (`CanMonitor` activation-on-cold-start, keep-alive suspend-cycle timer with control-gate serialization, `IElmSession.SendKeepAliveAsync`; replay-tested). Steering intentionally stays on the arbitration decorator: enabling keep-alive on the shared accept-all context would impose a ~2s suspend cycle on all monitoring — decision needs hardware measurement. **Hardware session 2026-07-18 (2017 AZE0, Veepeak BLE, while charging):** decode fixes CONFIRMED live — pack 393.17 V (was 0 V pre-UDS-fix), current −2.8…−4.1 A while charging (C1 signed fix), Hx/AHR stable 5/5, 96 cells 4088–4103 mV; VIN, protocol lock, wakeup strategy, UDS arbitration all worked (13/13 queries). Steering `1081` activation got an EPS response. Found + fixed same day: (1) BUFFER FULL leaves a residual prompt in the stream → off-by-one AT desync on re-enter → `EnterMonitoringModeAsync` now clears buffers first (replay regression test); (2) `PromptDetected` now auto-restarts like BufferFull instead of killing the monitor; (3) integration-test shim leaked running monitors across classes (~20 cascade failures) → per-test `[After(Test)]` disposal + `ThrowsExactly`→`Throws` for OCE subclasses. Accept-all overrun addressed same day with **hardware-filter rotation**: `CanMonitor.FilterRotation` (list of `CanFilterWindow` mask/pattern/dwell) — the loop enters monitoring once per window with that window's AT CM/CF filter, rotating on dwell expiry; cache accumulates across windows (data ≤ one cycle stale). Leaf rotation (`LeafAze0Contexts.SharedBroadcastRotation`): mask 700 × patterns 100/200/300/500/600 @ 600 ms → ~3 s cycle, ~1/8 bus load per window. Capability warm-up timeouts raised to 4 s (cold cache must survive a full cycle); suspend/keep-alive compose with rotation via the existing control gate. Also fixed while testing: typed `Subscribe<T>()` deferred subscriber registration to first MoveNext (frames arriving earlier were dropped) — now registers eagerly like the raw overload. Replay tests: rotation cycles filters + cache accumulation; suite green ×8. **Needs hardware re-test with rebuilt bundle: does mask-700 windowing fit the Veepeak's throughput, and is a 3 s staleness cycle acceptable? Dwell/pattern tuning expected.** **Audit A2 (session churn) resolved** — all broadcast snapshots except Steering are warm-cache I/O-free. | M (doc) + P1 done → P2/P3 pending | — | M2.1-2.3 ✔ |
| M2.5 Integration-test hardware skip — **DONE 2026-07-18** | Hardware tests are now opt-in via `LEAF_BLE_ADDRESS`: new `RequiresLeafHardwareAttribute` (TUnit `SkipAttribute` subclass) on all 6 integration classes skips when unset, and `BleSessionFixture` no-ops its BLE init in that case (fixture failure would otherwise pre-empt skip results). Without hardware: 50/50 skipped in ~0.2s, zero failures, no BLE touched (MTP exits 8 for "zero tests ran" — informational). With `LEAF_BLE_ADDRESS` set, behavior unchanged. Bonus cleanup: dead `tests/ObdInsight.Tests/Fixtures/BleSessionFixture.cs` deleted (unused since the 164c394 project split). | `RequiresLeafHardwareAttribute.cs`, `IntegrationTests/Fixtures/BleSessionFixture.cs`, 6 test classes | Suite runs clean with no adapter ✔ (50 skipped / 0 failed) | S | Low | — |

### M3 — Quality & polish

| Task | Description | Effort |
|---|---|---|
| M3.1 | README rewrite (honest framing: console-first today, MAUI aspiration; current architecture diagram) | S |
| M3.2 | Central package management (`Directory.Packages.props`); align Roslyn versions between generator and its tests | S |
| M3.3 | Fix namespace masquerade + relocate transports (consolidate console/DevTools BLE stacks per Theme 4 design) | L |
| M3.4 | Flatten `Vehicles/Vehicles/`; fix `OdbTestApp.Tests` assembly name; remove csproj scaffolding leftovers | S |
| M3.5 | Scrub personal MAC/VIN defaults into config; move mockups out of root (or to `docs/assets/`) | S |
| M3.6 | Tighten integration assertions; convert assertion-free diagnostics into explicitly-named harness commands | M |
| M3.7 | Split `Program.cs`: extract the 10 diagnostic blocks into a table-driven runner; restore vehicle selection | M/L |

### Top-3 implementation sketches

**1. M1.1 — Fix generated `ReadSigned`.**
Approach: make the generated helper identical in behavior to the hand-written `CanBits.ReadSigned` (`int` return, `(int)(unsigned | mask)` cast), computing the sign-extend mask in `ulong` so `bitLen == 32` works: `var signExtendMask = bitLen == 32 ? 0ul : ~((1ul << bitLen) - 1);` then cast once. Steps: (1) land M0.1 characterization tests red; (2) edit `GenerateCanBitsHelperMethods` (`CanSignalGenerator.cs:501-528`); (3) check `GenerateDecodeExpression` (`:428-448`) still produces correct casts now that the operand is `int` (unscaled paths lose their accidental rescue-cast dependence); (4) run generator tests, review the full snapshot diff line by line, accept; (5) add the missing signed+Factor→double snapshot case. Gotchas: every `*.g.verified.cs` churns — isolate in one commit; the test-project `CanBits.cs` copy will disagree until M1.2 deletes it.

**2. M0.3 — ReplayElmTransport.**
Approach: implement `IElmTransport` over a script of `(expectedWrite, responseBytes)` steps; responses include the `>` prompt for request/response mode and raw frame lines (no prompt) for monitoring mode. Feed from string literals first, then from parsed DevTools `Reports/leaf_session_*.txt` captures. Steps: (1) study `ElmFramer`'s read loop to match its framing expectations exactly; (2) implement strict mode (unexpected write → test failure) and lenient mode (AT commands auto-`OK`) — lenient makes session-level tests terse; (3) port golden BMS lines from `LeafBmsParsingHelpers.cs:17-26` as the first replay fixture. Gotchas: monitoring mode never sends a prompt and `ExitMonitoringModeAsync` expects drains/timeouts — the fake must support timed "no data" reads; `ElmSession` sends recovery commands on failure, so strict scripts need those exchanges or lenient mode.

**3. M0.2 — CI workflow.**
Approach: single job on `windows-latest` (the `net9.0-windows10.0.19041.0` TFMs won't build on Linux): checkout → `actions/setup-dotnet` reading `global.json` → `dotnet build ObdInsight.slnx -c Release` → `dotnet test tests/ObdInsight.Tests -c Release --no-build` and same for `tests/ObdInsight.SourceGeneration.Tests`. Gotchas: resolve QW3 first (as written, CI must install the .NET 10 SDK to build net9 targets); MAUI workloads — either `dotnet workload restore` (slow) or exclude `ObdInsight.Maui` from the CI build via a solution filter (`.slnf`) — recommended; TUnit uses Microsoft.Testing.Platform, and **`dotnet test` fails outright under the .NET 10 SDK** ("Testing with VSTest target is no longer supported…", confirmed 2026-07-18) — invoke the test exe via `dotnet run --project tests/<proj>` or opt in to the new dotnet-test experience (`dotnet.config`).

---

## 6. Open Questions (need a human decision)

1. **`global.json` SDK 10 bump** (uncommitted, `9.0.308` → `10.0.201`): intentional? Commit it or revert — it currently forces every environment (and CI) to carry the .NET 10 SDK with no project targeting net10.
2. **MAUI: still the goal?** Docs claim a mobile-first product; reality is a Windows console app. If mobile is still the plan, the Theme 4 transport consolidation should target a Plugin.BLE-compatible abstraction now. If console-first, say so in README and consider dropping the shell until it's real.
3. **`.cs.broken` DevTools features** — recording/replay and report generation (3,511 lines): port or delete? The recording feature directly feeds Theme 2 (replay fixtures); worth a deliberate call rather than silent deletion.
4. **Public-repo comfort:** personal adapter MAC and real VIN appear in source, launch settings, and committed session captures. Fine for a private repo; scrub before any OSS publication?
5. **Endianness roadmap:** any target vehicle whose signals are documented in Motorola byte order? If yes, `ByteOrder` support moves from "documented limitation" into Theme 1's scope.
6. **Honda CR-V profile** (`HondaCrv.cs:174` throws `NotImplementedException`): active intent or placeholder to remove?

---

## 7. Frame bit-layout audit — executed 2026-07-18 (see docs/FRAME_LAYOUT_AUDIT.md)

Hardware evidence: raw CAR-CAN captures from the 2017 AZE0 (parked in READY, charging,
ambient ~22 °C, pack ~96%). Root cause of every confirmed-broken signal: community DBC
layouts are Motorola bit order; the generator is Intel-only, and start bits were transcribed
verbatim. Motorola fields that cross byte boundaries are **not expressible** as a single
Intel `[CanSignal]`, so fixes declare byte-aligned raw part signals and recombine them in
computed properties on the partial class (capability-facing property names unchanged; the
generator was not modified).

Fixed (each locked with the exact captured bytes in `GeneratedFrameDecodingTests`):

- **0x284 `AbsFrame_284_AZE0`** — `VehicleSpeedFromAbs` (39,16) actually decoded the
  bytes-6/7 free-running message counter (61–496 km/h while parked). Now: wheel speeds =
  big-endian byte pairs 0-1/2-3 (×0.005), vehicle speed = bytes 4-5 (×0.01), bytes 6-7
  exposed as `MessageCounter1/2`. Factors verified only at the zero point (parked capture).
  Duplicate resolved: `VcmFrame_284_AZE0` (unused, conflicting layout) **deleted**;
  `AbsFrame_284_AZE0` is canonical. `AbsFrame_285_AZE0` given the same treatment (identical
  layout family, capture `…35BC`).
- **0x55B `BatteryFrame_55B_AZE0.Soc`** — was Intel (7,10) → decoded 1. True field is
  Motorola 7|10 (byte0 + byte1[7..6]); recombined from raw parts: `E8 00 → 928` = 92.8%
  (pack ~96%, matches expectation ~928–960).
- **0x245 `AbsFrame_245_AZE0`** — all three 12-bit torque fields were Motorola-transcribed
  (decoded 3720/232.5 Nm parked). True fields: byte0+byte1[7..4], byte1[3..0]+byte2,
  byte6+byte7[7..4]; center-offset 0x800 = 0 Nm, est. 0.5 Nm/bit (only the neutral point is
  hardware-verified: capture decodes −1.0/+1.0/−1.0 Nm). `TorqueDownRequestType` moved
  (31,3)→(29,3) (within-byte Motorola→Intel). Byte 4 and byte-7 low nibble confirmed as
  per-frame counters.
- **0x5BC `BatteryFrame_5BC_AZE0`** — GIDS fixed from Intel (7,10) (decoded 384) to the
  Motorola 7|10 recombination → 375. Caveat documented in the class remarks: 375 exceeds the
  typical 30 kWh max (~363) and `MaxGids` was set in the same frame — the field may carry
  full capacity in some mux states; needs a multi-frame capture across mux values.
  `RemainChargeTime` fixed from a 12-bit misread (4091) to the documented 13-bit field
  (byte6[4..0]+byte7) → decodes the 0x1FFF "unavailable" sentinel correctly; new
  `RemainChargeTimeAvailable` helper. SOH (33,7) left as-is (decoded 65% — plausible for an
  aged 30 kWh pack but unconfirmed; overlaps `Mux`/`RemainCapSegmentSwitchFlag` in byte 4 —
  flagged in doc comments).

Regression-locked confirmed-correct decodes (exact captured bytes): 0x292 lead-acid 12.70 V /
brake pressure 0 · 0x510 ambient 22.5 °C / ChargeMode 2 / climate off · 0x5A9 range 179.2 km ·
0x354 speed 0 / ESP enabled · 0x180 motor amps 0 / throttle 0 · 0x60D doors closed / signals
off / VehicleState READY.

**EV-CAN architecture fact documented** (CLAUDE.md gotcha, capability doc comments on
`LeafAze0MotorController`/`LeafAze0Vcm.GetGearPositionAsync`/`LeafAze0Brake`,
`SharedBroadcastRotation` comment): 0x1DB/0x1DC/0x1DA/0x11A/0x1CA/0x55A/0x59E were absent
from passive monitoring all session. Precision (refined 2026-07-18 after review): stock
ELM327 adapters wire OBD pins 6/14 = CAR-CAN; EV-CAN is physically present on OBD pins
12/13 and a rewired/modified adapter can monitor it — the frames are "unreachable" only for
unmodified adapters in passive mode. EV-CAN-sourced data (SOC, pack V/I, cells) remains
reachable on stock adapters via active UDS queries over CAR-CAN (BMS 79B→7BB, proven live
13/13) — this is how LeafSpy-class apps work. Affected broadcast capabilities time out on
data-absence until UDS alternatives exist; SOC cross-checks go through 0x55B (now fixed) or
0x5BC. This also answers Open Question 5: yes — the current vehicle's own DBC sources are
Motorola-order, so generator `ByteOrder` support would eliminate the raw-part/computed-
property workaround and belongs in Theme 1 scope.

Verification: `ObdInsight.Tests` 48/48 green ×3 runs (12 new tests);
`ObdInsight.SourceGeneration.Tests` 42/42 (generator untouched); DevTools + console app
compile. Not done (needs hardware/multi-frame data): 0x5BC mux cycle capture, torque-field
factor confirmation while driving, 0x54A ambient offset (+41?) verification.

**Addendum 2026-07-18 — cross-checked against OVMS `vehicle_nissanleaf.cpp`** (openvehicles
OVMS.V3, taps EV-CAN and CAR-CAN directly): confirms the 55B SOC, 5BC gids, 5BC 13-bit
charge time (0x1FFF sentinel), and 284 bytes-4/5 speed fixes bit-for-bit. **Resolved the
5BC 375-gids caveat**: byte5 bit4 (`MaxGids`) is the gids mux selector — 0 = remaining,
1 = maximum gids/pack capacity (30 kWh+ only); the capture had it set, so 375 = 30.0 kWh
full capacity, exactly right. Sentinels added: gids 0x3FF and 55B SOC 0x3FF = invalid
(`GidsValid` helper added; class docs updated). One conflict: OVMS decodes 5A9 range as
`(d1<<4|d2>>4)/5` → 124 km on our capture; our layout gives 179.2 km matching the dash
ground truth (~179) — kept ours for 2017 AZE0. New follow-up work identified from OVMS
(not yet applied): (1) our EV-CAN frames 1DB (Current/Voltage), 1DC (power limits), 1DA
(torque/RPM), 59E (capacity), 5C0 (pack temp) are also Motorola-mistranscribed — OVMS gives
authoritative layouts; existing 1DB/1DA unit tests are synthetic/self-consistent and lock
in the wrong wire layout; (2) CAR-CAN 0x421 gear map `(d0>>3)&7` (1=P,2=R,3=N,4=D,7=B) —
enables stock-adapter gear position for `LeafAze0Vcm`; (3) CAR-CAN 0x5B3 SOH `d1>>1`
(OVMS trusts 5BC byte-4 SOH only on 24 kWh ZE0 — our 65% read is suspect); (4) 0x355
odometer-units bit, 0x385 TPMS pressures; (5) 284 speed divisor: OVMS uses ~/98
(GPS-calibrated) vs our ×0.01.

**Addendum 2 (2026-07-18) — third-party app log confirms the EV-CAN finding.** Reviewed an
exported log from a commercial OBD app (Veepeak BLE, 2025-12-06 session, "Leaf ZE1" profile)
on the same car: the app sources effectively all data via active UDS polling (ATSH79B/
ATCRA7BB, groups 2101 ×32k / 2102 ×29k / 210E/210F/2104/210C, plus ATSH7DF standard OBD and
a one-shot ~100-header ECU enumeration). It attempted accept-all ATMA exactly once → hit
BUFFER FULL within one burst → never retried. The burst's frame set (002, 174, 176, 1CB,
1CC, 1D5, 1D6, 215, 216, 245, 284, 285, 292, 2DE, 50A, 50D, 510, 551, 5A9, 5B3, 6F6) is
CAR-CAN-only — no 1DB/1DC/1DA/11A/1CA/55A — independently confirming the stock-adapter
EV-CAN-broadcast absence. Bonus samples consistent with our fixes: 284/285 bytes 6-7
counter (…76FC/…76FD, speed bytes 4-5 = 0), 245 neutral 0x7FE/0x802 pattern with byte-4
counter, and a live 0x5B3 (`5084…` → SOH = d1>>1 = 66%) agreeing with our 5BC byte-4 SOH
read of 65% — raising confidence that 5BC SOH is valid on AZE0. Note: the app's ZE1 profile
mis-matches this AZE0 (BMS reply len 0x2B = 30 kWh variant), so its displayed battery values
may use wrong offsets/scales.

**Addendum 3 (2026-07-18) — stock-adapter gear + SOH landed.** Implemented the CAR-CAN
alternatives identified from OVMS: (1) `VcmFrame_421_AZE0` fixed to the real map (byte 0
bits 3-5: 0/1=P, 2=R, 3=N, 4=D, 7=Drive/B) with a static `ShifterPositionFromByte0` raw
decoder — the frame is 1 byte on the wire, so the generated 8-byte `Parse` can't run and
consumers read the monitor's raw cache (which stores any length; only the typed layer
filters to 8 bytes). (2) `LeafAze0Vcm.GetGearPositionAsync` now waits for either 0x11A
(EV-CAN, modified adapters) or 0x421 (CAR-CAN) and falls back to the raw 0x421 decode —
gear works on stock adapters for the first time. (3) New `VcmFrame_5B3_AZE0` with SOH =
byte1>>1 (0 = invalid; `SohValid` helper), locked with the 2025-12-06 third-party-app
capture (`5084FFFB20B5A18A` → 66%). (4) Production gap fixed: `SharedBroadcastRotation`
had no 0x4xx window, so 0x421 could never reach the cache — added mask 700/pattern 400
(cycle now ~3.6 s; needs the usual hardware dwell check). Tests: 421 map unit test, 5B3
decode test, and a capability-level replay test (`LeafAze0VcmGearFallbackTests`) driving
the 1-byte frame through ElmSession/CanMonitor into the fallback. Suite 51/51 green ×3;
DevTools compiles.

**Addendum 4 (2026-07-18) — EV-CAN frame layouts fixed against OVMS.** Completed the
follow-up from addendum 2: `BatteryFrame_1DB_AZE0` Current (byte0+byte1[7..5], 11-bit two's
complement, 0.5 A/bit; wire sign convention flagged unverified — OVMS negates it) and
Voltage (byte2+byte3[7..6], 0.5 V/bit); `BatteryFrame_1DC_AZE0` discharge/charge/charger-max
power limits; `InvMcFrame_1DA_AZE0` torque (byte2[2..0]+byte3, 11-bit signed, 0.5 Nm/bit)
and RPM (byte4[6..0]+byte5, 15-bit signed, /2 — byte4 bit7 undocumented, excluded);
`BatteryFrame_5C0_AZE0` temperatures ((17,7)→(16,8) — the old def halved twice). All use
the raw-part + computed-property pattern; capability-facing names unchanged (`MotorStatus`
untouched). Deliberate deviations from OVMS, documented in code: 1DC charge-limit packing
uses <<4 (10-bit field per DBC; OVMS's <<2 self-overlaps and contradicts its neighboring
fields — judged an OVMS bug), and 1DC MaxPowerForCharger keeps the DBC −10 kW offset that
OVMS omits (unresolved; flagged for hardware verification). 0x59E left unfixed: only weak/
conflicting references (OVMS reads a 12-bit field where the DBC says 9, and ignores it on
30 kWh) — not worth locking a guess into tests. The synthetic 1DB/1DA unit tests that
enshrined the old wrong layouts (C1b pattern) were rewritten with OVMS-derived bytes, and
the two CanMonitor typed-decode tests re-encoded. New 1DC and 5C0 decode tests. Also added
UsableSocValid (0x7F sentinel, per OVMS). Found + fixed during test bring-up: first draft
placed VoltageRawLow at (22,2) (byte2 bits) instead of (30,2) (byte3 bits) — caught by the
rewritten tests failing, which is the coverage working as intended. Suite 53/53 green ×3;
generator suite 42/42 untouched; DevTools compiles. Remaining EV-CAN caveat: everything
here is reference-verified only (no hardware can see these frames on stock adapters);
first modified-adapter or CAN-shield session should spot-check 1DB voltage/current against
the BMS UDS values.
