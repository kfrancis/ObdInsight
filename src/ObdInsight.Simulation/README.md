# ObdInsight.Simulation

Hardware-free development and testing for ObdInsight consumers:

- **`SimulatedLeafAze0Transport`** — a fake 30 kWh Nissan Leaf behind a fake ELM327:
  answers the real init/protocol sequence, BMS/VIN/DTC queries with state-accurate
  ISO-TP payloads, and streams CAR-CAN broadcast frames whose values evolve along a
  drive profile (SOC drain, speed cycles, pack warming). `timeScale` compresses a
  30-minute drive into seconds for tests.
- **`LeafDriveProfile`** — the time → vehicle-state curve; swap in your own.
- **`ReplayElmTransport`** — deterministic scripted transport (`Expect`,
  `AutoRespond`, `EnqueueIncoming`) with connection-loss injection
  (`SimulateConnectionLost`) for resilience testing.
- **`LeafGoldenData`** — real captured BMS/VIN responses (synthetic VIN) for
  golden-data tests.

```csharp
// A full pre-check → drive → post-check flow, zero hardware:
var transport = new SimulatedLeafAze0Transport(timeScale: 60);
var session = new ElmSession(new ElmFramer(transport));
await session.InitializeAndLockAsync(ct);
var detection = await VehicleResolver.ResolveAsync(session, ct: ct);
await using var telemetry = TelemetrySession.Create(detection.Commands!);
```

Docs: [MAUI integration](https://github.com/kfrancis/ObdInsight/blob/main/docs/MAUI_INTEGRATION.md) ·
[repository](https://github.com/kfrancis/ObdInsight)
