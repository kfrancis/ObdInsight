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

- [ ] **B1 — `ITelemetrySession` API + normalized DTOs.** (L; design doc first)
  New consumer facade (new project `src/ObdInsight.Telemetry` or Core namespace):
  caller registers signal set with cadence tier (high 1–2 s / medium 5–10 s / low
  30–60 s) → scheduler serves broadcast signals from the `CanMonitor` latest-frame cache
  (no I/O) and batches UDS queries through existing `CanMonitor.SuspendAsync`
  arbitration → per-sample event or `IAsyncEnumerable<TelemetrySample>`. DTOs use
  `decimal`, km / km/h / °C / kW / kWh / V, every field nullable (null = unavailable),
  range-validated against `[CanSignal]` Min/Max (out-of-range → null, never a bogus
  report value). Include one-shot `GetSnapshotAsync` (pre/post-check) and a per-signal
  availability report probed at connect.
  *Acceptance:* design doc in `docs/`; replay test drives a 3-tier session over scripted
  Leaf data end-to-end (cache signals + UDS signals interleaved, monitor running
  throughout); consumer never touches `ElmSession`/`CanMonitor` directly.
  *Unblocks:* M-A, M-B. *Deps:* none (composes on P3 arbitration).

- [ ] **B2 — Simulated transport package.** (M)
  Promote the `ReplayElmTransport` pattern into a shipping `src/ObdInsight.Simulation`:
  lenient auto-respond mode, time-driven scripted "drive profiles" (reuse
  `LeafGoldenData` + DevTools `Reports/leaf_session_*.txt` captures), a simulated Leaf
  AZE0 that answers BMS/VIN UDS and streams CAR-CAN broadcast frames continuously for
  30+ min with evolving values (SOC drain, temp rise, speed curve).
  *Acceptance:* a console or test harness runs a full simulated pre-check → drive →
  post-check through the B1 API with zero hardware; EvTestDrive can reference the
  package without touching test assemblies.
  *Unblocks:* app development day 1. *Deps:* B1 API sketch (sim should exercise the real contract).

- [ ] **B3 — SOC for the 30 kWh AZE0.** (S/M)
  Decode SOC from BMS Group 01 at AZE0 offsets (LeafSpy proves the field exists in this
  response; research LeafSpy/OVMS layouts). Also expose GIDS-derived energy (kWh) where
  available. Add `AppliesTo` coverage so the supported variant stops returning null.
  *Acceptance:* replay test with golden Group01 capture yields plausible SOC%;
  hardware-verify against dash on next live session (flag pending).
  *Unblocks:* M-A, M-B flagship signal. *Deps:* none.

- [ ] **B4 — Annotations split (audit QW4).** (S)
  Remove the duplicate runtime `ProjectReference` to `ObdInsight.SourceGeneration` from
  Core and the console app; move attribute types + `CanBits` to a tiny annotations
  assembly or linked source. Generator stays analyzer-only for consumers.
  *Acceptance:* no Roslyn/`Microsoft.CodeAnalysis` in Core's runtime closure
  (`dotnet build` + inspect); both test suites green; snapshots unchanged (or accepted
  deliberately).
  *Unblocks:* clean MAUI/AOT consumption. *Deps:* none.

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

- [ ] **B8 — Range capability (0x5A9).** (S)
  Wire `VcmFrame_5A9_AZE0.RangeInstrumentCluster` (CAR-CAN, capture-locked 179.2 km)
  into a capability field (likely `IVcm.VcmStatus` or new). Add the 0x5xx window to the
  filter rotation if not already covered.
  *Acceptance:* replay test through monitor cache; field null when frame absent.
  *Unblocks:* M-B medium tier. *Deps:* none.

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
