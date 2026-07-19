# EvTestDrive Readiness Roadmap

**Date:** 2026-07-19. Derived from a full-repo audit against the requirements of
**ObdInsight.EvTestDrive** — a .NET MAUI (Android + iOS) consumer app that assesses used-EV
health during a pre-sale test drive: BLE connect (Vgate iCar Pro reference adapter) →
pre-check → 20–30 min live telemetry → post-check → report.

**How to use this file:** work items in phase order. Each item has acceptance criteria.
When an item lands, change its `[ ]` to `[x]` and append a dated note (AUDIT.md style).
Do not start Phase 4 (vehicle breadth) until Phases 0–3 are done — one vehicle end-to-end
beats four half-working.

**Working rules for every item (do not skip):**

1. Follow CLAUDE.md exactly: test invocation is `dotnet run --project tests/ObdInsight.Tests`
   (never `dotnet test`), TUnit conventions, Verify snapshot accept workflow.
2. Tests exercise **production code** through `ReplayElmTransport` — never re-implement
   parsers test-side. New telemetry paths need replay tests before merge.
3. Layering rules are enforced: no vehicle names in `Communication/`, no Console/Serilog
   in Core (ILogger only), vehicle quirks go through strategies (`IEcuWakeupStrategy` pattern).
4. Anything requiring live-vehicle or adapter verification: implement + replay-test, then
   flag "needs hardware check" in the item note — never fake a hardware validation.
5. Large items (B1, B9, B10) get a short design doc in `docs/` (pattern:
   `STREAMING_MONITOR_DESIGN.md`) reviewed before implementation.
6. Run both suites (`ObdInsight.Tests`, `ObdInsight.SourceGeneration.Tests`) green before
   declaring an item done.

---

## Context: what the audit found (2026-07-19)

- Decode layer trustworthy (C1 signed-decode fixes hardware-verified). `CanMonitor`
  P1–P3 done: shared monitor, typed `Subscribe<T>`, latest-frame cache,
  `SuspendAsync`/`MonitorSuspendingElmSession` UDS arbitration, filter rotation. Core is
  plain net9.0, one dependency, DI-clean — loads on Android/iOS.
- EV-CAN broadcast (0x1DB/1DC/1DA/11A/1CA/55A/59E) is unreachable on stock adapters;
  battery data comes via UDS 79B→7BB over CAR-CAN. CAR-CAN broadcast (0x54x HVAC, 0x284
  speed, 0x421 gear, 0x5A9 range, 0x5B3 SOH, 0x5C5 odometer candidate) is reachable.
- **Signal gaps:** SOC% broken for the supported 30 kWh AZE0 (`Group01Response.SocPercent`
  is `AppliesTo="40kWh_ZE1"` only); DTC reading absent entirely (no Mode 03/07, no UDS
  0x19); odometer absent; charge-cycle counts absent; range frame 0x5A9 exists +
  capture-locked but unwired to any capability.
- **Structural gaps:** no cadence scheduler / polling session (broadcast streams are
  bus-cadence; UDS is one-shot pull); no reconnect-with-continuity; no connection-state
  events (`IElmTransport` exposes only `bool IsOpen`); retry is recover-then-retry-once,
  not per-request ≤3; capability absence semantics inconsistent (BMS throws, VIN nulls,
  HVAC returns partial record); all three BLE stacks are WinRT-only and FFF0-hardcoded
  (Vgate iCar Pro commonly uses FFE0/FFE1 — no FFE0 profile exists in repo); VIN read
  works but never drives profile selection (hardcoded literal VIN in `Program.cs`);
  duplicate source-gen runtime ref ships Roslyn into the runtime closure (audit QW4);
  `ReplayElmTransport` is test-assembly-only and test-strict; `VehicleProfileRegistry`
  reflection scan is iOS trim/AOT-hostile.

Milestones: **M-A** pre-check · **M-B** live drive · **M-C** post-check/report ·
**M-D** multi-vehicle.

---

## Phase 0 — contract + dev unblock

- [x] **B1 — `ITelemetrySession` API + normalized DTOs.** (L) — **DONE 2026-07-19**
  (design doc `docs/TELEMETRY_SESSION_DESIGN.md` — flagged for review; API can still move).
  New `src/ObdInsight.Telemetry` (net9.0, refs Core only): `ITelemetrySession` /
  `TelemetrySession` with three cadence tiers (`TelemetrySubscription.Default` = the
  EvTestDrive spec), batch-shaped `ITelemetryProvider` adapters over capabilities
  (one UDS exchange serves SOC+V+A+kW+SoH), cache-only reads bounded by
  `CacheReadTimeout` (cold cache can't stall a tier), per-batch event +
  `IAsyncEnumerable`, live `Availability` map (UDS-miss = Unavailable, cold broadcast =
  Unknown until data appears), `GetSnapshotAsync` incl. VIN, decimal DTOs (km/km-h/°C/
  kW/V, all nullable), static plausibility validation (out-of-range → null; in the
  facade because `[CanSignal]` Min/Max is doc-only + reflection is iOS-AOT-hostile).
  Provider-less signals (odometer, cycles, DTCs) degrade to null. Tests:
  `Telemetry/TelemetrySessionTests` — 3-tier replay end-to-end (UDS + cache interleaved,
  monitor running throughout), snapshot shape, absent-broadcast degradation; green ×6
  Debug + Release. *Unblocked:* M-A, M-B app-side work.

- [x] **B8 — Range capability (0x5A9), pulled forward from Phase 1.** — **DONE 2026-07-19.**
  `VcmStatus.RangeKm` filled from `VcmFrame_5A9_AZE0.RangeInstrumentCluster`
  independently of 0x510 presence; 0xFFF charging sentinel → null. Rotation already
  covers 0x5xx. Replay tests: capture value 179.2 km, sentinel → null, frame-absent →
  null (`LeafAze0VcmRangeTests`).

- [x] **B2 — Simulated transport package.** (M) — **DONE 2026-07-19.**
  New `src/ObdInsight.Simulation` (net9.0, refs Core, zero test-framework deps):
  `ReplayElmTransport` + `LeafGoldenData` moved in from the former
  `tests/ObdInsight.Tests.Base` (project deleted; tests now reference Simulation), plus
  `SimulatedLeafAze0Transport` + `LeafDriveProfile` — a time-driven fake 30 kWh Leaf
  behind a fake ELM327: answers the real init/protocol sequence, BMS/VIN UDS with
  state-accurate ISO-TP payloads (SOC at the B3 offset, 96 cells, Group04 thermistors,
  shunts), streams CAR-CAN broadcast (0x284/0x54x/0x510/0x5A9/0x421/0x5B3) with evolving
  values (SOC drain, speed cycles, pack warming), `TimeScale` compression for tests.
  Deliberate limits documented: AT CM/CF filters ignored; EV-CAN broadcast absent (like
  a stock adapter). *Acceptance met:* `SimulatedDriveTests` runs pre-check → 20 s
  compressed drive → post-check purely through `ITelemetrySession` (SOC drained, pack
  warmed, range shrank, VIN read); `SimulatedLeafTransportTests` covers init, cold/
  running-monitor UDS, cells, VIN, scheduler, stop-then-snapshot. Suite 71/71 ×4 + 42/42.
  Found while testing: sim clock now starts on first adapter traffic (session stack
  never calls `OpenAsync`).
  *Unblocked:* EvTestDrive development day 1.

- [x] **B3 — SOC for the 30 kWh AZE0.** (S/M) — **DONE 2026-07-19 (hardware check pending).**
  `Group01Response.SocPercent` gained a 24/30 kWh field: payload offset 29, UInt24BE,
  0.0001 %/bit, `ValidRange 0..100`, `AppliesTo="24kWh,30kWh"`. Offset derived from the
  consistent ZE1 = AZE0 + 2 shift of this response (Hx 26→28, AHR 33→35, documented ZE1
  SOC at 31) and validated against the 2026-01-18 golden capture: `06 65 8A` → 41.92 %
  at pack 361.78 V (≈3.77 V/cell — consistent). Replay test
  `GetStatus_ExtractsSoc_For30kWhLeaf` replaces the old SOC-is-null test; 57/57 green.
  **Needs hardware check:** compare against dash SOC next live session. GIDS-derived kWh
  skipped — GIDS lives on EV-CAN 0x5BC, unreachable on stock adapters; revisit if a UDS
  source is identified (B14 research).
  *Unblocks:* M-A, M-B flagship signal.

- [x] **B4 — Annotations split (audit QW4).** (S) — **DONE 2026-07-19.**
  New `src/ObdInsight.Annotations` (net9.0, dependency-free) owns the attribute sources +
  hand-written `CanBits` (namespaces unchanged — generator matches by full name). The
  generator compiles them as linked source (analyzer stays self-contained; a
  ProjectReference would not load in the analyzer context). Duplicate runtime refs removed
  from Core and the console app; Core now references Annotations. Verified: Core bin =
  Core + Annotations only (no Roslyn); 57/57 + 42/42 green, snapshots untouched; DevTools
  compiles. Follow-up unlocked: M1.2 single-source CanBits (test-project copy deletable).

## Phase 1 — pre-check complete (M-A)

- [ ] **B5 — DTC reading.** (M)
  Generic OBD-II Mode 03 (stored) + Mode 07 (pending) over functional 7DF, multi-ECU
  responses; decode to standard `P0xxx`-style codes. UDS 0x19 per-ECU later (separate
  item if needed). New `IDiagnosticTroubleCodes` capability, registered for Leaf.
  *Acceptance:* replay tests from synthetic + captured responses; graceful empty result
  when no codes; no throw on unsupported ECU.
  *Unblocks:* M-A, M-C. *Deps:* none.

- [ ] **B6 — VIN-driven vehicle selection.** (S/M)
  Wire `IVehicleIdentification.GetVinAsync` → `VehicleProfileRegistry` /
  `DetectVariantFromVin` → command-set construction. Delete the hardcoded literal VIN
  path in `Program.cs`; finish the `DistinguishVariantByVds` stub for Leaf variants.
  *Acceptance:* replay test: session connects, reads VIN, resolves AZE0-2 command set
  with no hardcoded vehicle; unknown VIN → clear "unsupported vehicle" result, not a
  crash.
  *Unblocks:* M-A; prerequisite for M-D. *Deps:* none.

- [ ] **B7 — Unified degradation contract.** (S/M)
  All capabilities: data absence → nullable fields / null result, never throw
  (`LeafAze0Bms.GetStatusAsync` currently throws on missing Group01). Cancellation
  still propagates as OCE. Document the contract on the capability interfaces.
  *Acceptance:* replay tests for each capability with absent data return
  null/partial-with-nulls; grep finds no `InvalidOperationException` on data absence
  in capability implementations.
  *Unblocks:* M-A graceful degradation. *Deps:* B1 (availability report consumes this).

- [x] **B8 — Range capability (0x5A9).** (S) — **DONE 2026-07-19, pulled into Phase 0**
  (see the entry under B1 above).

## Phase 2 — live drive on phones (M-B)

- [ ] **B9 — Cross-platform BLE transport.** (M/L; design doc first)
  New `src/ObdInsight.Transports.Ble` on Plugin.BLE (or Shiny.BluetoothLE) implementing
  `IElmTransport`. GATT profile table: **FFE0/FFE1 single-characteristic (Vgate iCar
  Pro) + FFF0/FFF1/FFF2 (Veepeak) + Nordic UART**, with auto-probe on connect. Port
  profile knowledge from DevTools `BleDeviceProfile.cs`; fold in the namespace-masquerade
  cleanup (audit A5/M3.3) so WinRT transports stop claiming `ObdInsight.Core.*`.
  *Acceptance:* compiles for net9.0-android + net9.0-ios; profile auto-probe unit-tested
  against fake GATT layers; hardware check with a real iCar Pro flagged pending.
  *Unblocks:* M-B on Android, most of iOS. *Deps:* B4 recommended first.

- [ ] **B10 — Resilience layer.** (L; design doc first)
  (a) Connection-state event stream (`Connecting/Connected/Degraded/Reconnecting/Lost`)
  surfaced from transport through B1's session for UI binding. (b) Reconnect with
  continuity: supervisor re-opens transport, re-runs init + protocol lock (existing
  recovery ladder covers the adapter side), restarts `CanMonitor`, keeps B1 subscriber
  streams alive across the gap (samples pause, resume; no resubscribe required).
  (c) Injectable per-request retry policy (≤3 attempts + timeout) replacing the
  recover-then-retry-once behavior for consumer-facing queries.
  *Acceptance:* replay test simulating transport death mid-drive → session reconnects,
  telemetry stream resumes, state events fired in order; retry policy unit-tested.
  *Unblocks:* M-B in a moving car. *Deps:* B1, B9.

- [ ] **B11 — Speed factor verification.** (S; hardware)
  One driving capture; confirm 0x284 speed factor ×0.01 vs OVMS ~/98; lock with captured
  bytes in `GeneratedFrameDecodingTests`.
  *Unblocks:* M-B data trust. *Deps:* hardware session.

- [ ] **B12 — iOS hygiene.** (M)
  Replace `VehicleProfileRegistry` reflection scan with explicit registration; trim/AOT
  test pass of Core + generated code on net9.0-ios; document scoped-per-connection
  lifetime guidance (ElmSession not thread-safe) for MauiProgram registration.
  *Acceptance:* Core + Telemetry + Ble transport build with trimming enabled for iOS
  without warnings from our assemblies.
  *Unblocks:* M-B on iOS. *Deps:* B9.

## Phase 3 — post-check / report (M-C)

- [ ] **B13 — Odometer.** (S/M)
  CAR-CAN 0x5C5 frame (OVMS layout) + capability + units bit (0x355). Hardware verify
  against dash.
  *Unblocks:* M-C. *Deps:* hardware check.

- [ ] **B14 — Charge-cycle counts (QC / L1L2).** (M; research-heavy)
  Identify which BMS UDS group carries the counters (LeafSpy exposes them → reachable);
  decode + add to `BatteryStatus`. Golden-capture test once located.
  *Unblocks:* M-C. *Deps:* B3 learnings.

- [ ] **B15 — Packaging + scrub.** (S/M)
  NuGet pack for Core + Annotations + Telemetry + Simulation + Ble transport (analyzer
  packs to `analyzers/dotnet/cs`), versioning, CI pack step — or documented project-ref
  consumption. Scrub personal adapter MAC + real VIN from fixtures/launchSettings
  (audit M3.5) before anything ships.
  *Unblocks:* EvTestDrive CI. *Deps:* B4.

## Phase 4 — vehicle breadth (M-D; only after Phases 0–3)

- [ ] **B16 — Hyundai Kona.** (L) Best next target; UDS-over-OBD well documented.
- [ ] **B17 — Chevy Bolt.** (L) GM UDS PIDs community-documented; needs hardware access.
- [ ] **B18 — Tesla Model 3.** (L + feasibility risk) No standard OBD-II port (harness
  adapter behind rear console); ELM327-over-BLE path marginal. Run a feasibility spike
  before committing; consider de-scoping.
- [ ] **B19 — Honda CR-V decision.** Delete or ignore the all-null stub (audit Q6).

---

## API design flags (fix before EvTestDrive integrates; folded into items above)

1. No streaming members on capability interfaces — apps must reach into
   `LeafAze0CommandSet.Monitor`. B1 becomes the only consumer surface.
2. Inconsistent absence semantics (throw vs null vs partial) — B7.
3. `IElmTransport` has no connection-state events — add during B9/B10 while all
   implementations are in-repo.
4. `double?` vs `decimal`, W vs kW, mV `int[]` — normalize once in B1 DTOs; don't churn Core.
5. `ElmSession` recover-retry-once + non-thread-safety — B1 owns single-writer
   discipline; B10 owns retry policy.
6. `ReplayElmTransport` lives in a test assembly — B2 ships a proper package.
7. `VehicleProfileRegistry` reflection — B12.
8. Duplicate source-gen runtime ref (Roslyn in closure) — B4.
9. `MinValue`/`MaxValue` doc-only, no runtime validation — B1 facade range-checks.
10. Hardcoded personal MAC + real VIN — B15 scrub.
