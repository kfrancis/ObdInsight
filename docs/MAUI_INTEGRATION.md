# MAUI Integration Guide (EvTestDrive)

**Date:** 2026-07-19 (roadmap B12/B15). How to consume ObdInsight from a .NET MAUI app
(Android + iOS), including the lifetime rules that matter.

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
// MauiProgram.cs
builder.Services.AddSingleton<IConnectionFactory, ObdConnectionFactory>();

public sealed class ObdConnection : IAsyncDisposable
{
    public ITelemetrySession Telemetry { get; }
    public VehicleDetectionResult Detection { get; }
    // owns: transport → ElmFramer → ElmSession → command set (incl. CanMonitor)
}

public sealed class ObdConnectionFactory : IConnectionFactory
{
    public async Task<ObdConnection> ConnectAsync(Guid bleDeviceId, CancellationToken ct)
    {
        // Resilient composition (docs/RESILIENCE_DESIGN.md): the reconnecting decorator
        // owns a transport FACTORY — a BLE drop in a moving car is a data gap, not a
        // teardown. For development without hardware swap the factory for
        // () => new SimulatedLeafAze0Transport(timeScale: 1).
        var transport = new ReconnectingElmTransport(
            () => new PluginBleElmTransport(CrossBluetoothLE.Current.Adapter, bleDeviceId));
        await transport.OpenAsync(ct);

        var session = new ElmSession(new ElmFramer(transport), new LeafBmsWakeupStrategy());
        await session.InitializeAndLockAsync(ct);

        // Per-request retry (≤3 attempts); composes inside the monitor arbitration.
        var retrying = new RetryingElmSession(session);

        var detection = await VehicleResolver.ResolveAsync(retrying, ct: ct);
        if (detection.Status != VehicleDetectionStatus.Detected)
        {
            // Surface detection.Status / detection.Vin to the UI — never throws.
        }

        // connectionState wires ITelemetrySession.ConnectionState / ConnectionStateChanged
        // (Connecting/Connected/Reconnecting/Lost) for direct UI binding.
        var telemetry = TelemetrySession.Create(detection.Commands!, connectionState: transport);
        return new ObdConnection(transport, session, detection, telemetry);
    }
}
```

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
