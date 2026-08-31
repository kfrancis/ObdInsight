# ObdInsight.Telemetry

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

Docs: [MAUI integration](https://github.com/kfrancis/ObdInsight/blob/main/docs/MAUI_INTEGRATION.md) ·
[design](https://github.com/kfrancis/ObdInsight/blob/main/docs/TELEMETRY_SESSION_DESIGN.md) ·
[repository](https://github.com/kfrancis/ObdInsight)
