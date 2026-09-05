# Telemetry Session Design (roadmap B1)

**Status:** Draft for review; implementation proceeding in parallel per EVTESTDRIVE_ROADMAP
Phase 0 (this doc is the reviewable artifact — flag objections and the API can still move).
**Date:** 2026-07-19

**Contract update 2026-09-04:** snapshots now preserve `DiagnosticTroubleCodes` as
independent stored/pending outcomes with observed responder coverage. Failure and
partial reads are not empty clean lists. Leaf Group 01 Hx no longer populates SOH;
SOH remains null until a validated source is wired in. See
[Diagnostic evidence](DIAGNOSTIC_EVIDENCE.md) for the current contract and migration.

**Strict-decoding update:** cell vectors now preserve `decimal?` entries at physical
indexes; `CellVoltageData` is immutable and pack-wide statistics require a complete
set. See [diagnostic decoding](DIAGNOSTIC_DECODING.md).

## 1. Problem

EvTestDrive (MAUI consumer app) needs "give me these N signals at these cadences and raise
an event per sample" — today the repo offers per-capability `GetStatusAsync` snapshots and
frame-level `CanMonitor` streams. Every consumer would hand-roll the same scheduler, unit
conversions, plausibility filtering, and UDS-vs-cache split. The consumer contract also
wants `decimal` km / km/h / °C / kW / V with **every field nullable**, while capability
records are `double`-typed, unit-mixed (`PowerWatts` vs `ChargePowerKw`, mV `int[]`), and
inconsistent about absence (BMS throws, cache views return empty records).

## 2. Goals / non-goals

**Goals**

1. One consumer facade: subscribe a signal set across three cadence tiers
   (high 1–2 s, medium 5–10 s, low 30–60 s), get per-tick sample batches via
   `IAsyncEnumerable` and an event.
2. Normalized DTOs: `decimal`, SI-for-automotive units (km, km/h, °C, kW, V, %), all
   nullable, plausibility-validated (out-of-range → null, never a bogus report value).
3. One-shot `GetSnapshotAsync` for pre-/post-check, including VIN.
4. Per-signal availability report so the app degrades gracefully (no exceptions on
   data absence; cancellation still propagates as OCE).
5. Vehicle-agnostic: built over `IVehicleCommandSet` capabilities; Leaf is the first
   provider set, other vehicles plug in the same way.
6. Deterministic replay tests end-to-end (cache + UDS interleaved under a running monitor).

**Non-goals**

- DTC reading (B5), odometer (B13), charge cycles (B14) — the signal enum reserves names;
  providers appear when the underlying data lands.
- Reconnect/resilience (B10) — this session assumes a live `ElmSession`; B10 wraps it.
- Replacing capabilities — providers adapt them, they stay the diagnostic-grade surface.

## 3. Shape

New project `src/ObdInsight.Telemetry` (net9.0, references Core only) — keeps Core lean
and matches the B15 packaging list (Core + Annotations + Telemetry + Simulation + Ble).

```
ITelemetrySession  (TelemetrySession)
  ├─ TelemetrySubscription: TelemetrySignal → CadenceTier (Default = EvTestDrive spec)
  ├─ scheduler loop: per-tier due times, sequential ticks (ElmSession is single-writer)
  ├─ ITelemetryProvider[]  (vehicle adapters; batch-shaped)
  │    ├─ cache-only (speed/HVAC/range): read CanMonitor-backed capability, bounded by
  │    │    options.CacheReadTimeout (default 250 ms) so cold caches can't stall a tier
  │    └─ UDS (BMS status, cells): capability call under existing SuspendAsync arbitration
  └─ TelemetryValidator: static per-signal plausibility ranges → out-of-range = null
```

### Key types

```csharp
public enum TelemetrySignal
{
    StateOfCharge, PackVoltage, PackCurrent, PackPower, PackTemperature, StateOfHealth,
    CellVoltageMin, CellVoltageMax, CellVoltageAverage, CellVoltages,
    VehicleSpeed, RemainingRange, CabinTemperature, HvacActive,
    Odometer, ChargeCycleCount,           // reserved: providers pending B13/B14
}

public enum CadenceTier { High, Medium, Low }

public readonly record struct TelemetryValue(decimal? Scalar = null,
    IReadOnlyList<decimal>? Vector = null, bool? Boolean = null);

public sealed record TelemetrySample(TelemetrySignal Signal, TelemetryValue Value,
    DateTimeOffset TimestampUtc, CadenceTier Tier);

public sealed record TelemetrySampleBatch(CadenceTier Tier, DateTimeOffset TimestampUtc,
    IReadOnlyList<TelemetrySample> Samples);

public enum SignalAvailability { Unknown, Available, Unavailable, Stale }

public interface ITelemetryProvider
{
    IReadOnlyCollection<TelemetrySignal> Signals { get; }
    bool IsCacheOnly { get; }
    ValueTask<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>> ReadAsync(
        IReadOnlySet<TelemetrySignal> requested, CancellationToken ct);
}

public interface ITelemetrySession : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken ct);
    ValueTask StopAsync(CancellationToken ct);
    IReadOnlyDictionary<TelemetrySignal, SignalAvailability> Availability { get; }
    IAsyncEnumerable<TelemetrySampleBatch> Batches(CancellationToken ct);
    event EventHandler<TelemetrySampleBatch>? BatchAvailable;
    ValueTask<TelemetrySnapshot> GetSnapshotAsync(CancellationToken ct);
}
```

`TelemetrySnapshot` is a flat record of nullable normalized fields (SOC %, pack V/A/kW,
pack °C, SoH %, cell stats + indexed `IReadOnlyList<decimal?>` in V, speed, range km, cabin °C,
HVAC bool, odometer km, cycle counts, `Vin`, `DiagnosticTroubleCodes` — the last three null
until B13/B14/B5).

### Units and signs

| Signal | Unit | Source conversion |
|---|---|---|
| StateOfCharge / StateOfHealth | % | `BatteryStatus` doubles → decimal |
| PackVoltage | V | as-is |
| PackCurrent | A | as-is; **sign: + discharge / − charge (regen)** — matches hardware capture (−2.8 A while charging) |
| PackPower | kW | `V × A / 1000`, same sign convention |
| PackTemperature | °C | as-is |
| Cell voltages | V | mV `int` → `decimal / 1000` |
| VehicleSpeed | km/h | as-is |
| RemainingRange | km | 0x5A9 via `VcmStatus.RangeKm` (B8, folded in); 0xFFF charging sentinel → null |
| CabinTemperature | °C | `HvacStatus.InteriorIntakeTempC` |
| HvacActive | bool | `ClimateControlOn || AcOn` |

### Scheduling semantics

- Single background loop; computes the next due tier from configurable periods
  (`High=1.5 s, Medium=7.5 s, Low=45 s` defaults; tests shrink them).
- Ticks are sequential (session is single-writer). If a tick overruns its period, the
  next occurrence is scheduled from *completion* time — no backlog, no burst.
- A tier's batch always contains one sample per subscribed signal of that tier; unknown
  values are null-valued samples (UI binds "—"), never omissions. Unexpected provider
  errors terminate the run; see [terminal outcomes and lifecycle](ASYNC_OUTCOMES.md).
- Cache-only provider reads are bounded by `CacheReadTimeout` via a linked CTS: an
  internal timeout maps to null values; caller cancellation rethrows OCE.
- UDS providers ride the existing `MonitorSuspendingElmSession` arbitration — the monitor
  suspends around the query batch and resumes, same as capabilities do today.

### Availability

Probed during `StartAsync`, then reassessed at publication. Fresh usable observations
are `Available`; usable old observations are `Stale`; missing/invalid/timeout/unknown-age
readings are `Unknown`. Only absent providers or explicit unsupported evidence produce
`Unavailable`. A failed UDS probe is not proof of unsupported vehicle hardware.

### Observation evidence

See [observation semantics](OBSERVATION_SEMANTICS.md) for the implemented contract.
Publication follows provider reads and is distinct from acquisition. Snapshots retain a
per-signal `Measurements` evidence map; convenience fields contain only fresh readings.
`MaxObservationAge` defaults to 30 seconds. Batches, snapshots and typed streams carry
connection-generation identity when created by `VehicleConnection`. Typed streams retain
stale metadata but still skip absent values; use batches to record missing-data outcomes.

### Validation

Static plausibility table (SOC 0–100, pack 100–500 V, current ±500 A, temps −40..85 °C,
speed 0–200 km/h, cell 1.5–5 V, range 0–500 km...). Out-of-range → null + debug log.
This is deliberately in the facade, not Core: `[CanSignal] MinValue/MaxValue` metadata is
documentation-only by decision (audit M1.3) and not reachable at runtime without
reflection (iOS AOT hostile).

## 4. Test plan (replay, no hardware)

1. **Three-tier end-to-end:** Leaf command set over `ReplayElmTransport`; broadcast 0x284
   (speed), 0x54x (HVAC), 0x5A9 (range) enqueued continuously; scripted 2101/2102/2104
   golden responses. Assert: high batches carry SOC (UDS) + speed (cache) interleaved
   while the monitor keeps running; medium carries cabin temp + range; low carries cells;
   consumer code never touches `ElmSession`/`CanMonitor`.
2. **Snapshot:** pre-check shape — all BMS fields + VIN populated, decimal units correct
   (361.78 V, 41.92 %, cells in V).
3. **Degradation:** no 0x5A9 in replay → range sample null, availability not `Available`,
   no throw; expected query timeout maps to null, while I/O/programming failures fault
   run completion and streams.
4. **Validation:** injected implausible value → null sample.

## 5. Consequences

- EvTestDrive binds one interface; Core API churn (B7 absence semantics, B10 resilience)
  lands behind it without app changes.
- B2's simulator exercises this same contract (`ITelemetrySession` over a simulated
  transport), so app development starts before any hardware session.
- Capability records stay `double`/mixed — normalization is one layer, in one place, by
  design; Core is not churned (roadmap API-flag #4).
