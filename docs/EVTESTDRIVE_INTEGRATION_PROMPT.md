# Prompt: integrate ObdInsight into EvTestDrive

Copy everything below the line into a Claude Code session in the EvTestDrive repo.

---

Integrate the ObdInsight NuGet packages as this app's OBD communication layer, and
adjust the app to consume them. Audit the existing code first — replace any hand-rolled
OBD/BLE/parsing code with the library rather than keeping parallel implementations.

## Packages (nuget.org, prerelease — use the latest `0.1.0*` version)

- `ObdInsight.Core` — session, protocols, vehicle capabilities, VIN-driven resolution,
  resilience decorators
- `ObdInsight.Telemetry` — `ITelemetrySession`, the ONLY surface app code should touch
  during a drive
- `ObdInsight.Transports.Ble` — Plugin.BLE transport for Android/iOS (Vgate iCar Pro
  auto-detected)
- `ObdInsight.Simulation` — simulated Leaf + scripted replay transport; use for all
  development and unit tests, no hardware needed

Do NOT reference `ObdInsight.Annotations`/`ObdInsight.SourceGeneration` — those are
only for defining new CAN frames, which this app doesn't do.

## Connection wiring (one object graph per adapter connection)

```csharp
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Simulation;
using ObdInsight.Telemetry;
using ObdInsight.Transports.Ble;

// Resilient transport owns a FACTORY: a BLE drop mid-drive reconnects with backoff
// while the whole object graph above stays alive (samples pause, then resume).
var transport = new ReconnectingElmTransport(
    () => new PluginBleElmTransport(CrossBluetoothLE.Current.Adapter, bleDeviceId));
// Development flavor (no hardware): swap the factory for
//   () => new SimulatedLeafAze0Transport(timeScale: 1)
await transport.OpenAsync(ct);

var session = new ElmSession(new ElmFramer(transport), new LeafBmsWakeupStrategy());
await session.InitializeAndLockAsync(ct);

var retrying = new RetryingElmSession(session); // per-query retry ≤3 on IOException

var detection = await VehicleResolver.ResolveAsync(retrying, ct: ct);
// detection.Status: Detected | VinUnreadable | UnsupportedVehicle | VariantUnsupported
// Never throws — surface non-Detected statuses in the UI with detection.Vin.

await using var telemetry = TelemetrySession.Create(
    detection.Commands!, connectionState: transport);
```

**Lifetime rules (important):** `ElmSession`, the command set, and `TelemetrySession`
are single-consumer and NOT thread-safe. Register the graph scoped to a connection
(a factory/service created on connect, disposed when the drive ends) — never as
app-lifetime DI singletons. `TelemetrySession` internally serializes its scheduler
against snapshot calls, so the app may call `GetSnapshotAsync` while streaming.

## Drive flow

```csharp
var preCheck  = await telemetry.GetSnapshotAsync(ct);   // pre-check
await telemetry.StartAsync(ct);                          // probes availability, starts cadence loop
await foreach (var batch in telemetry.Batches(ct)) { }   // or the BatchAvailable event
await telemetry.StopAsync(ct);
var postCheck = await telemetry.GetSnapshotAsync(ct);    // post-check
```

- `TelemetrySnapshot` (pre/post-check): `Vin`, `DiagnosticTroubleCodes` with independent
  stored/pending outcomes and responding-ECU evidence. Only `Succeeded` has aggregate
  codes; an empty list covers observed responders, not whole-vehicle health. See
  `docs/DIAGNOSTIC_EVIDENCE.md`. Leaf Group 01 Hx is not SOH; SOH is currently null.
  Other measurements include `SocPercent`, `PackVoltageV`,
  `PackCurrentA` (+ discharge / − regen), `PackPowerKw`, `PackTemperatureC`,
  `StateOfHealthPercent`, `CapacityAh`, `CellVoltagesV` (full set, volts) +
  min/max/average, `VehicleSpeedKmh`, `RemainingRangeKm`, `CabinTemperatureC`,
  `HvacActive`, `OdometerKm`, `ChargeCycleCount`.
- Measurement fields are nullable `decimal` (or list/bool); render unavailable values
  as "—". DTC outcomes require status/coverage handling, and caller cancellation or
  programming/lifecycle exceptions still need normal application handling.
- Units are fixed: km, km/h, °C, kW, V, %. No conversion in the app.
- Streaming: `TelemetrySampleBatch` per cadence tick; each `TelemetrySample` has
  `Signal` (`TelemetrySignal` enum), `Value` (`TelemetryValue` — one of
  `Scalar`/`Vector`/`Boolean`, `IsEmpty` when unavailable), `TimestampUtc`, `Tier`.
- Cadence: `TelemetrySubscription.Default` matches the app spec, EXCEPT consider
  moving `CellVoltages` to `CadenceTier.Low` — each UDS read costs a monitor
  suspend/resume cycle over BLE, and the 96-cell read is the heavy one.
- `telemetry.Availability` (live per-signal map: Available/Unknown/Unavailable) drives
  graceful degradation in the UI. `Odometer`/`ChargeCycleCount` are currently always
  Unavailable (no provider yet) — design the UI to tolerate that.
- `telemetry.ConnectionState` + `ConnectionStateChanged`
  (`Connecting/Connected/Reconnecting/Lost`) bind straight to a connection indicator.
  On `Lost` (reconnect exhausted) the drive is over — offer manual reconnect.

## Platform setup

- Android: request `BLUETOOTH_SCAN` + `BLUETOOTH_CONNECT` at runtime (API 31+) before
  any Plugin.BLE call.
- iOS: `NSBluetoothAlwaysUsageDescription` in Info.plist. Plugin.BLE device IDs on iOS
  are per-install UUIDs, not MACs — persist the ID from the user's scan/pick, never
  hardcode.
- The Vgate iCar Pro resolves automatically (FFE0/FFE1 single-characteristic profile);
  `PluginBleElmTransport.ActiveProfile` reports what was picked — log it.

## Testing

- Unit/integration tests and local dev run against `SimulatedLeafAze0Transport`
  (constructor `timeScale` compresses a 30-min drive into seconds; `SimulatedVin` is
  its constant VIN) — drive the FULL pre-check → drive → post-check flow in tests.
- `ReplayElmTransport` (same package) scripts exact adapter exchanges
  (`Expect`/`AutoRespond`/`EnqueueIncoming`) and injects link death
  (`SimulateConnectionLost()`) for resilience tests.

## Current library limitations to design around

- Only the Nissan Leaf AZE0-2 (2016–2017, 30 kWh) resolves to `Detected`; other Leaf
  years return `VariantUnsupported`, other makes `UnsupportedVehicle`. The UI must
  present these as first-class outcomes, not errors.
- Odometer and charge-cycle counts: always null for now.
- DTCs are generic OBD Mode 03/07 (EV-specific UDS 0x19 codes come later).
- Some values are pending hardware verification (SOC vs dash, speed scale while
  driving) — keep report wording appropriately hedged for now.

Reference docs in the ObdInsight repo if fetched: `docs/MAUI_INTEGRATION.md`,
`docs/TELEMETRY_SESSION_DESIGN.md`, `docs/RELEASING.md`.

Plan the integration first (project structure, DI shape, what existing code gets
replaced), then implement with a simulator-backed end-to-end test proving
pre-check → compressed drive → post-check works before touching any UI polish.
