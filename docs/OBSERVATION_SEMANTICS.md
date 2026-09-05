# Observation evidence and freshness

Implemented contract for Core and Telemetry. This complements connection generations
in [resilience](RESILIENCE_DESIGN.md); it does not introduce a recorder or report engine.

## Three distinct times and identities

- `ObservationMetadata.ObservedAtUtc` is host receipt (parsed CAN frame) or completed
  diagnostic query time. It is not the ECU's hardware clock, BLE notification time,
  or the time the vehicle physically sampled a sensor.
- `TelemetrySample.TimestampUtc`, batch time, and snapshot time denote publication /
  snapshot assembly **after** provider reads. Repeated cache reads never recapture
  acquisition time. A multi-query snapshot is not a simultaneous physical measurement.
- `ConnectionGeneration` tags batches, typed samples, and snapshots produced by
  `VehicleConnection`. Standalone telemetry leaves it null unless explicitly supplied.
  Numbers are owner-local, not globally unique: a recorder supplies its own drive ID.

`ObservationMetadata.Capture` retains a private monotonic timestamp. With the same
`TimeProvider`, age uses elapsed time even if UTC moves backwards. External/persisted
metadata uses UTC subtraction; negative ages and absent acquisition times are Unknown.
Clock bookkeeping is not serialized and does not participate in evidence equality.
Use the same clock for expert session/telemetry composition; the owner does this for you.

## Quality is not freshness or support

`ObservationQuality` distinguishes Valid, Partial, Missing, Invalid, Unsupported,
TimedOut and Unknown. Source distinguishes CAN broadcast, diagnostic query and Unknown;
CAN ID / query text are optional provenance. They identify acquisition, not a complete
raw diagnostic audit trail. Invalid decoding retains receipt metadata but no measurement.
Timeouts have no fabricated receipt time. Missing does not mean zero or unsupported.

`TelemetrySessionOptions.MaxObservationAge` defaults to 30 seconds. At publication,
known nonnegative age up to this limit is Fresh, above it Stale; unprovable age is Unknown.
This is a consumer policy, not a safety guarantee or automatic disconnection detector.
Freshness describes evidence age independently of quality: a recently received invalid
reply can have Fresh evidence while still containing no usable measurement.

`Availability` is the latest assessment, not permanent hardware capability discovery:
fresh usable data is Available; old usable data is Stale; missing, invalid, timeout or
unknown-age data is Unknown. Only absent providers or explicit unsupported outcomes
produce Unavailable. Polling continues, and later observations can change the assessment.

## Consumer rules

Snapshots contain `Measurements`, a read-only map with an entry for every telemetry
signal. Stale values and their original timestamps remain here. Numeric/vector/boolean
convenience fields include **only Fresh** readings. Null conveniences are not a clean
health result: inspect Measurements and the existing diagnostic outcomes before reporting.
Session-produced vectors are copied/read-only even with range validation disabled.

Batch streams retain missing/invalid/timeout samples. Typed streams still skip absent
values, but carry Observation, Age, Freshness and ConnectionGeneration for emitted
values, including stale and unknown-age values. Use Batches when recording gaps matters.
Buffers remain bounded drop-oldest, not durable/lossless recording. This tranche does
not add sequence/drop counters, persistence, report judgments, or upload policies.

```csharp
var snapshot = await generation.Telemetry.GetSnapshotAsync(ct);
var speed = snapshot.Measurements[TelemetrySignal.VehicleSpeed];
if (speed.Freshness == ObservationFreshness.Fresh &&
    speed.Observation.Quality == ObservationQuality.Valid)
{
    // speed.Scalar is report-eligible under the configured freshness policy.
}
// Persist evidence, snapshot.TimestampUtc, your drive ID and ConnectionGeneration.
```

## Provider and generator migration (pre-1.0)

Existing scalar capability properties remain; metadata companions correspond to each
current telemetry signal. Leaf speed uses frame 284, cabin temperature 54F, climate
state 54C, range 5A9. Electrical values use query 2101; temperatures 2104; cell voltages
2102, not the later balancing query 2106. Derived power uses its oldest input evidence.
The companion on CellVoltageData describes voltages, not balancing flags.

Generated `Query{Name}Async` now returns `Task<Observed<Response?>>`: inspect `Value`
for decoded data and `Observation` for evidence. Invalid framing/schema/bounds yield
null Value with Invalid evidence; cancellation and I/O exceptions still propagate.
The generator calls `IElmSession.QueryResponseAsync`; the monitor-suspending decorator
captures the inner result before resuming monitoring. Legacy QueryAsync stays available.
Regenerate third-party schemas with matching Core/generator packages.

Custom telemetry providers attach evidence with `TelemetryValue.WithObservation`.
Do not stamp cache lookup time as acquisition. Existing providers without evidence
still compile, but values have Unknown freshness and null snapshot conveniences.
Range validation retains metadata, drops invalid scalars and preserves null vector slots.
Leaf BMS converts expected I/O absence to its existing empty results and retains explicit
timeout/invalid evidence; programming errors and cancellation are no longer swallowed.

Low-level `TryGetLatest<T>(out value, out observation)` preserves the exact cached frame's
evidence. The value-only overload and typed CAN streams remain expert conveniences.
Custom raw frame sources must supply metadata to claim freshness. ELM and SLCAN sources
stamp received frames; parsing an arbitrary byte span alone does not prove acquisition.

Scope: all currently implemented telemetry signals, not every legacy expert-only DTO
field. VIN and DTC results retain their separate identity/diagnostic contracts, not entries
in Measurements. No reflection, scanning, DI requirement, package split, or platform
dependency was added. Trim analysis does not replace Android/iOS device validation.
