# ObdInsight.Core

Core OBD-II / CAN communication for EVs over ELM327-family adapters:

- **`ElmSession`** — adapter init, protocol detect/lock, query vs monitoring state
  machine, four-level failure recovery for cheap clone adapters.
- **`CanMonitor`** — one long-lived monitoring pass shared by all consumers: typed
  decoded streams, latest-frame cache, hardware-filter rotation for overflow-prone
  adapters, UDS arbitration (queries transparently suspend/resume monitoring).
- **Vehicle capabilities** — `IBatteryManagementSystem`, `IHvac`,
  `IDiagnosticTroubleCodes` (OBD Mode 03/07), `IVehicleIdentification`, and more.
  DTC reads preserve independent mode outcomes and responding-ECU evidence; failed
  reads never become clean lists. Caller cancellation and programming errors propagate.
  Battery `StateOfHealthPercent` is not a proxy for Nissan Hx; Leaf currently leaves it null.
- **`VehicleResolver`** — VIN-driven vehicle/variant detection and command-set
  construction (Nissan Leaf AZE0 fully wired; profiles are pluggable).
- **Resilience** — `ReconnectingElmTransport` (transport-factory reconnect with
  backoff and connection-state events) and `RetryingElmSession` (per-query retry).

Bring a transport: `ObdInsight.Transports.Ble` (Android/iOS), your own
`IElmTransport`, or `ObdInsight.Simulation` for hardware-free development. Most apps
should consume telemetry through `ObdInsight.Telemetry` rather than this package's
lower-level surface.

Diagnostic decoding uses one strict ISO-TP parser; incomplete, mixed-responder, or
malformed input cannot become a trusted single payload. Cell voltages preserve
physical indexes as `IReadOnlyList<int?>`; statistics require a complete set.

```csharp
var session = new ElmSession(new ElmFramer(transport), new LeafBmsWakeupStrategy());
await session.InitializeAndLockAsync(ct);
var detection = await VehicleResolver.ResolveAsync(session, ct: ct);
// detection.Commands is the vehicle's capability set when Status == Detected
```

Docs: [MAUI integration](https://github.com/kfrancis/ObdInsight/blob/main/docs/MAUI_INTEGRATION.md) ·
[repository](https://github.com/kfrancis/ObdInsight)
