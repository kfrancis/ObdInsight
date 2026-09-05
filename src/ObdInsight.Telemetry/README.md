# ObdInsight.Telemetry

Diagnostic snapshots retain per-mode DTC outcomes and responding-ECU evidence in
`DiagnosticTroubleCodes`. Check status before interpreting codes: failed/partial
reads are not clean results, and success covers only observed responders. Leaf
Group 01 Hx is not SOH; `StateOfHealthPercent` is currently null for that provider.

Cell vectors and typed cell streams use `IReadOnlyList<decimal?>`: missing readings
retain their physical indexes. Pack-wide cell statistics are null for incomplete
sets; do not filter missing cells out and renumber the remaining entries.

The consumer telemetry facade for ObdInsight — the only surface an app needs during a
drive:

- **Cadence-tiered polling** — subscribe signals at High (1–2 s), Medium (5–10 s), and
  Low (30–60 s) tiers; broadcast-backed signals come from the shared monitor cache
  (no adapter round-trip), UDS-backed signals ride the built-in monitor arbitration.
- **Normalized DTOs** — nullable `decimal` in km, km/h, °C, kW, V; null always means
  "unavailable", never an error; implausible values are filtered to null.
- **Snapshots** — one-shot `GetSnapshotAsync` for pre/post-drive checks: VIN, DTCs,
  SOC, pack V/A/temp, SoH, full cell-voltage set.
- **Availability + connection state** — a live per-signal availability map and
  re-exposed `Connecting/Connected/Reconnecting/Lost` transitions for UI binding.

```csharp
await using var telemetry = TelemetrySession.Create(detection.Commands!,
    connectionState: reconnectingTransport);

var preCheck = await telemetry.GetSnapshotAsync(ct);
await telemetry.StartAsync(ct);
await foreach (var batch in telemetry.Batches(ct))
{
    // one batch per cadence tick; samples carry signal, value, timestamp, tier
}

// Or one signal at its own type — no enum switch, no TelemetryValue unpacking:
await foreach (var sample in telemetry.Stream(Signals.StateOfCharge, ct))
{
    decimal soc = sample.Value;   // TelemetrySample<decimal>
}
```

Develop without hardware using `ObdInsight.Simulation`.

Use `VehicleConnection` to own the complete adapter/vehicle graph and its recovery.
Pass a fresh-transport factory and explicit vehicle profiles, then await `OpenAsync`.
Each ready generation supplies `Detection`, `Telemetry`, `Number`, and `Ended`.
Loss ends the old generation; await a newer one and explicitly start a new recording
segment. No interrupted operation is replayed and no cached data crosses generations.
See [owned recovery](https://github.com/kfrancis/ObdInsight/blob/main/docs/RESILIENCE_DESIGN.md).

Each run exposes `Completion`. I/O and unexpected producer failures fault this task
and all batch/typed streams; expected query timeouts remain missing samples. Stop
joins the producer, and canceling a stop only cancels the wait. Await stop again before
restarting and create new subscriptions. Disposal joins active work but does not
dispose supplied capabilities or the connection graph. Event handlers run synchronously;
their exceptions are isolated and logged.

See [terminal outcomes](https://github.com/kfrancis/ObdInsight/blob/main/docs/ASYNC_OUTCOMES.md)
for startup cancellation, failure, restart, and ownership contracts.

Docs: [MAUI integration](https://github.com/kfrancis/ObdInsight/blob/main/docs/MAUI_INTEGRATION.md) ·
[design](https://github.com/kfrancis/ObdInsight/blob/main/docs/TELEMETRY_SESSION_DESIGN.md) ·
[repository](https://github.com/kfrancis/ObdInsight)
