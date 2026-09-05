# MAUI Integration Guide (EvTestDrive)

**Date:** 2026-07-19 (roadmap B12/B15). How to consume ObdInsight from a .NET MAUI app
(Android + iOS), including the lifetime rules that matter.

**Current lifecycle contract:** see [terminal asynchronous outcomes](ASYNC_OUTCOMES.md).
Each telemetry run has `Completion`; unexpected producer failures fault it and its
streams. Canceling a stop does not abandon the producer. Await stop before restart
and use new subscriptions. `VehicleConnection` owns reinitialization and fresh
generations after loss; recording explicitly starts a new segment, never an
uninterrupted subscription across physical connections. Interrupted command framing
also ends the generation; see [transaction safety](ELM_TRANSACTION_SAFETY.md). Never
retry an uncertain diagnostic command merely because BLE still reports connected.

**Recording evidence:** [observation semantics](OBSERVATION_SEMANTICS.md) separates
publication from acquisition time. Persist your drive ID and ConnectionGeneration
alongside each value's Observation/Freshness/Age. Use batches to retain missing-data
outcomes. Snapshot convenience fields now omit stale/unknown-age readings; inspect
Measurements before drawing pre/post health conclusions.

## Packages to reference

NuGet packages (see `docs/RELEASING.md`; project references work identically):

| Package | Why |
|---|---|
| `ObdInsight.Core` | Session, protocols, capabilities, `VehicleResolver`, resilience decorators |
| `ObdInsight.Telemetry` | `ITelemetrySession` — the only surface app code should touch during a drive |
| `ObdInsight.Transports.Ble` | `PluginBleElmTransport` (Plugin.BLE) for Android/iOS |
| `ObdInsight.Simulation` | `SimulatedLeafAze0Transport` for development/tests without hardware |

All are trim-annotated (`IsTrimmable` + trim analyzers, zero IL warnings). Vehicle
profiles are explicitly registered — no reflection scans — so iOS full-AOT/trimming is
safe. (A device-based AOT publish smoke test remains pending; it needs a Mac build host.)

## Lifetime rules (important)

- **`ElmSession` is single-consumer and NOT thread-safe.** One session per adapter
  connection; never a DI singleton shared across features. The same goes for
  `CanMonitor`, the command set, and `TelemetrySession` — they form one object graph
  per connection.
- Register the graph **scoped to a connection**, not to the app. The natural shape is
  a factory/service that owns the whole chain and is created when the user connects
  and disposed when the drive ends.
- `TelemetrySession` serializes its own scheduler ticks and snapshots internally — the
  app may call `GetSnapshotAsync` while the cadence loop runs, but only through that
  one instance.

## Wiring sketch

```csharp
await using var connection = new VehicleConnection(
    () => new PluginBleElmTransport(CrossBluetoothLE.Current.Adapter, bleDeviceId),
    [new NissanLeaf()],
    wakeupStrategy: new LeafBmsWakeupStrategy());
// Development: use () => new SimulatedLeafAze0Transport(timeScale: 1).
var generation = await connection.OpenAsync(ct);
var telemetry = generation.Telemetry;
var detection = generation.Detection;
```

The connection owns the graph. On loss, finish the old recording segment and await
`connection.WaitForReadyAsync(generation.Number, ct)`; explicitly start its telemetry
and create new subscriptions. Do not redirect old subscriptions or snapshots to a
replacement vehicle/session. See [owned recovery](RESILIENCE_DESIGN.md).

Drive flow: `GetSnapshotAsync` (pre-check) → `StartAsync` + bind `Batches()` /
`BatchAvailable` + `Availability` → `StopAsync` → `GetSnapshotAsync` (post-check).
Every DTO field is nullable `decimal` in km / km/h / °C / kW / V — null always means
"unavailable", never an error.

## Platform notes

- **Android:** request `BLUETOOTH_SCAN`/`BLUETOOTH_CONNECT` (API 31+) at runtime
  before touching Plugin.BLE.
- **iOS:** add `NSBluetoothAlwaysUsageDescription` to Info.plist. Plugin.BLE device
  IDs on iOS are per-install UUIDs, not MAC addresses — persist the ID you get from a
  scan, don't hardcode.
- The Vgate iCar Pro resolves to the FFE0/FFE1 single-characteristic profile
  automatically; `PluginBleElmTransport.ActiveProfile` says what was picked.
- UDS reads suspend/resume the shared broadcast monitor. Keep heavy signals
  (96-cell voltages) at the Low cadence tier — see `TelemetrySubscription.Default`
  and the drive-test guidance in `SimulatedDriveTests`.
