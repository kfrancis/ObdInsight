# Resilience Layer Design (roadmap B10)

**Status:** Draft for review; implementation proceeding per Phase 2.
**Date:** 2026-07-19

## 1. Problem

Bluetooth in a moving car drops. Today a dead link surfaces as timed-out reads →
`IOException` after one recover-retry; there is no reconnect (recovery re-runs AT
commands over the same dead transport), no connection-state signal for the UI, and
per-request retry is fixed at recover-then-retry-once. The caller's only option is to
rebuild the entire object graph (what the console `SessionRetryService` does — app-side
only).

## 2. Shape — three composable pieces, no rebuild

```
ReconnectingElmTransport   IElmTransport decorator, owns a transport FACTORY:
  states: Connecting → Connected → Reconnecting → (Connected | Lost)
  - reacts to IConnectionAwareTransport.ConnectionLost AND to read/write failures
  - reconnect loop: dispose dead inner, factory() → OpenAsync, backoff, ≤ MaxAttempts
  - reads/writes during an outage BLOCK until reconnected (bounded), then resume —
    the session/monitor objects above never get torn down
  - implements IConnectionStateSource (event + current state) for UI binding

RetryingElmSession         IElmSession decorator: per-request retry ≤ MaxAttempts with
  delay, wrapping the session's existing recover-then-retry-once. Composes INSIDE
  MonitorSuspendingElmSession (one suspension, N attempts).

Monitor continuity        NO CanMonitor change needed: reads/writes block during an
  outage, and the production Leaf monitor runs hardware-filter rotation — it re-enters
  monitoring every dwell window (~600 ms) by design, which re-establishes ATMA on the
  fresh adapter right after reconnect. If the adapter also lost protocol lock, the next
  UDS tick's failure walks the existing L0-L3 recovery ladder (baseline re-init +
  protocol reapply). Continuity emerges from composition — no new session machinery.
  (A silence watchdog for non-rotating monitors was considered and deferred — nothing
  in the shipped path needs it.)
```

### Connection-state surface

```csharp
public enum ConnectionState { Connecting, Connected, Reconnecting, Lost }

public interface IConnectionStateSource
{
    ConnectionState State { get; }
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
}
```

`TelemetrySession` accepts an optional `IConnectionStateSource` and re-exposes it
(`ConnectionState` + event) so the app binds one object. `Degraded` from the roadmap
sketch is folded into `Reconnecting` (distinct UI treatment wasn't justified; can be
added later without breaking).

### Wiring (consumer)

```csharp
var transport = new ReconnectingElmTransport(
    () => new PluginBleElmTransport(adapter, deviceId), options);
var session = new ElmSession(new ElmFramer(transport), new LeafBmsWakeupStrategy());
var retrying = new RetryingElmSession(session, retryOptions);
var detection = await VehicleResolver.ResolveAsync(retrying, ct);
var telemetry = TelemetrySession.Create(detection.Commands!, connectionState: transport);
```

## 3. Semantics

- **Reconnect triggers:** `ConnectionLost` event (proactive, BLE stack told us) or an
  exception from inner read/write/open (reactive). First trigger wins; concurrent
  callers wait on the same reconnect attempt.
- **Backoff:** `InitialDelay × 2^attempt`, capped (defaults 500 ms → 8 s, 6 attempts).
  Exhausted → state `Lost`; pending and subsequent I/O throws `IOException`; a later
  explicit `OpenAsync` may start over.
- **State event ordering guarantee:** events fire in transition order from a single
  supervisor loop; no concurrent duplicate transitions.
- **Retry policy:** retries only `QueryAsync` (both overloads) on `IOException`;
  cancellation and other exceptions propagate untouched. Defaults: 3 attempts, 250 ms
  between.
- **Watchdog:** `SilenceRestartTimeout` default null (off) — opt-in, because scripted
  replay tests legitimately go silent. The MAUI wiring recommendation is ~5 s.

## 4. Test plan (replay, no hardware)

- `ReplayElmTransport` gains failure injection: `SimulateConnectionLost()` (raises the
  event + makes I/O throw until "repaired") — shipped in Simulation, useful to
  EvTestDrive's own tests.
- Reconnect: scripted transport #1 dies mid-session → factory returns scripted
  transport #2 → decorator reconnects, state events observed in order
  (Connected → Reconnecting → Connected); a query issued during the outage completes
  after reconnect.
- Give-up: factory keeps failing → Lost after MaxAttempts; I/O throws IOException.
- Retry policy: unit — first N-1 attempts throw IOException, Nth succeeds; OCE never
  retried; attempts capped.
- Watchdog: monitor running on a transport that goes silent → restart observed
  (re-entered monitoring), frames resume after the transport "recovers".
- Telemetry continuity: TelemetrySession over the composed stack; kill + repair the
  transport mid-stream; batches pause and resume without resubscribing;
  `ConnectionState` transitions surface through the session.
