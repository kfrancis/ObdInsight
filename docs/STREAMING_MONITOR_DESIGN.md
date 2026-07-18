# Streaming Monitor Design

**Status:** P1 implemented (`CanMonitor`, `MonitoringEndReason`, `IElmSession.LastMonitoringEndReason` — tests in `tests/ObdInsight.Tests/Elm327/CanMonitorTests.cs`). P2/P3 pending.
**Date:** 2026-07-18

## 1. Problem

Broadcast CAN data is push-shaped — Leaf frames arrive every 10–100 ms whether anyone asked or
not — but the current consumer API is pull-shaped and pays a heavy toll per pull:

- **Session churn (audit A2).** Every capability call enters and exits monitoring mode:
  `LeafAze0Hvac.GetStatusAsync` runs `EnterMonitoringModeAsync` (~10 AT commands + delays),
  collects ~400 ms of frames, then `ExitMonitoringModeAsync` (CR, buffer drains, 5 more AT
  commands). A dashboard polling HVAC + ABS + VCM at 1 Hz spends most of its time thrashing
  adapter state instead of reading data.
- **Exclusive filters.** Each capability owns its own `EcuContext` filter, so two capabilities
  can never share one monitoring pass even though their frames arrive interleaved on the same bus.
- **Pull-only surface.** `GetStatusAsync` snapshots fit request/response UDS queries but fight
  broadcast data. There is no way to say "give me every battery frame as it arrives."
- **Silent stream death (audit A7).** `MonitorFramesAsync` ends with a bare `yield break` on
  `BUFFER FULL` or prompt detection — callers cannot distinguish "you cancelled" from
  "the adapter died mid-stream."

## 2. Goals / Non-goals

**Goals**

1. One long-lived monitoring pass shared by all broadcast consumers.
2. Push API: `IAsyncEnumerable<RawCanFrame>` per subscriber, plus typed decoded streams.
3. Instant snapshots from a latest-frame cache (no adapter round-trip for `GetStatusAsync`).
4. Explicit end-reason reporting.
5. Testable end-to-end against `ReplayElmTransport` (no hardware).

**Non-goals (this design)**

- 29-bit CAN and Motorola byte order (documented limitations; separate work).
- Replacing the UDS query path — request/response capabilities (BMS Mode 21, VIN) keep
  `QueryAsync`.
- Multi-adapter or multi-bus topologies.

## 3. Design

### 3.1 `CanMonitor` (new, `ObdInsight.Core.Communication.Elm327`)

A long-lived owner of the adapter's monitoring mode with channel-based fan-out:

```
ElmSession (mode arbitration, enter/exit once)
    └── CanMonitor
          ├── single MonitorFramesAsync read loop
          ├── latest-frame cache: Dictionary<int /*canId*/, RawCanFrame>
          └── subscribers: bounded Channel<RawCanFrame> per subscription, demuxed by CAN ID
```

```csharp
public sealed class CanMonitor : IAsyncDisposable
{
    public CanMonitor(IElmSession session, EcuContext monitoringContext, ILogger<CanMonitor>? logger = null);

    /// Starts monitoring (enters monitoring mode, spawns the read loop). Idempotent.
    ValueTask StartAsync(CancellationToken ct);

    /// Stops monitoring (cancels loop, exits monitoring mode). Safe to restart.
    ValueTask StopAsync(CancellationToken ct);

    /// Why the last run ended. Running => None.
    MonitoringEndReason EndReason { get; }

    /// Latest frame seen for a CAN ID, if any — O(1), no I/O.
    bool TryGetLatest(int canId, out RawCanFrame frame);

    /// Raw stream of frames whose ID is in canIds (empty => all). Each subscriber gets an
    /// independent bounded channel; slow consumers drop OLDEST frames (broadcast data —
    /// the newest value is the valuable one).
    IAsyncEnumerable<RawCanFrame> Subscribe(ReadOnlyMemory<int> canIds, CancellationToken ct);
}

public enum MonitoringEndReason
{
    None,           // still running / never started
    Stopped,        // caller-initiated StopAsync or cancellation
    BufferFull,     // ELM327 reported BUFFER FULL and exited monitoring itself
    PromptDetected, // adapter dropped to command prompt unexpectedly
    TransportError, // I/O failure
}
```

Design decisions:

- **`System.Threading.Channels`**, one bounded channel (default capacity ~64) per subscription,
  `BoundedChannelFullMode.DropOldest`. No subscriber can stall the read loop; no unbounded
  buffers (engineering constraint).
- **Wide hardware filter, software demux.** The ELM327 has a single CM/CF mask pair, which
  cannot express an arbitrary ID set. `CanMonitor` uses the union-friendly context supplied by
  the caller (or accept-all) and demuxes in software — the pattern the capabilities already use
  individually, now paid once.
- **End reason is set before the loop completes**, so `await foreach` termination + `EndReason`
  gives callers the full story. `BufferFull` additionally triggers an automatic restart attempt
  (bounded retry, e.g. 3 attempts with backoff) before surfacing — BUFFER FULL is routine on
  busy buses with accept-all filters.
- `TryGetLatest` reads the cache the loop maintains for **every** frame, including IDs nobody
  subscribed to. This is what makes snapshot capabilities instant.

### 3.2 Typed decoded streams (generator extension)

`CanSignalGenerator` already emits `CanFrameRouter.TryParse<Frame>(canId, data, out frame)` per
frame type. Two small additions make typed subscriptions generic-friendly:

1. Emit `public const int FrameCanId = 0x...;` on each generated frame class.
2. Emit a static router map `CanFrameRouter.CanIdOf<T>()` / `TryParse<T>(...)`.

Then an extension layer (hand-written, `ObdInsight.Core`):

```csharp
public static class CanMonitorExtensions
{
    // Decoded push stream: subscribe to the frame's CAN ID, parse, yield typed instances.
    public static IAsyncEnumerable<T> Subscribe<T>(this CanMonitor monitor, CancellationToken ct)
        where T : ICanFrame<T>;   // interface implemented by generated frames

    // Decoded snapshot from the latest-frame cache.
    public static bool TryGetLatest<T>(this CanMonitor monitor, out T frame)
        where T : ICanFrame<T>;
}
```

`ICanFrame<T>` (static abstract members: `CanId`, `Parse`) is emitted by the generator on each
frame class — net9 supports static abstract interface members.

Consumer experience — the whole point of this design:

```csharp
await using var monitor = new CanMonitor(session, EcuContext.NissanLeafHvbatMonitor);
await monitor.StartAsync(ct);

await foreach (var battery in monitor.Subscribe<BatteryFrame_1DB_AZE0>(ct))
    Console.WriteLine($"{battery.Voltage:F1} V  {battery.Current:F1} A");
```

### 3.3 Capability migration

Broadcast-backed capabilities (`LeafAze0Hvac`, `LeafAze0Abs`, `LeafAze0Brake`,
`LeafAze0BodyControl`, `LeafAze0Steering`, `LeafAze0MotorController`, `LeafAze0Vcm`,
`LeafAze0Charger`) change from *owning* monitoring to *viewing* a shared `CanMonitor`:

- Constructor takes `CanMonitor` instead of `(IElmSession, EcuContext)`.
- `GetStatusAsync` becomes: read `TryGetLatest<...>` for each needed frame; if a required frame
  is missing (monitor cold), await its first arrival with a short timeout. No enter/exit.
- New streaming member on the capability interfaces where it earns its keep, e.g.
  `IAsyncEnumerable<HvacStatus> StreamStatusAsync(CancellationToken ct)` composed from the
  underlying typed streams.
- `LeafAze0CommandSet` constructs one `CanMonitor` (accept-all EV-CAN context) and hands it to
  all broadcast capabilities; UDS capabilities (BMS, VIN) keep the session.

**Mode arbitration:** UDS queries cannot run while monitoring. `ElmSession` already enforces
this; the coordinator is `CanMonitor`: `PauseAsync()`/`ResumeAsync()` (or an
`IDisposable`-scoped `SuspendScope`) lets the command set run a query batch between monitor
windows. Phase 3; until then callers stop the monitor explicitly before UDS work.

### 3.4 What this deletes

- Per-capability `EnterMonitoringModeAsync`/`ExitMonitoringModeAsync` calls and their ~400 ms
  collection windows.
- The 8 near-identical "collect frames until timeout, break when all present" loops in the
  capability classes.
- The `CommunicationMode` gymnastics for `ActiveMonitoring` contexts can fold into
  `CanMonitor.StartAsync` (session activation + keep-alive hooks from `EcuContext` are honored
  by the monitor's loop — keep-alive slots into the same arbitration point as queries).

## 4. Phasing

| Phase | Scope | Effort | Proves |
|---|---|---|---|
| P1 — **DONE 2026-07-18** | `CanMonitor` with raw `Subscribe`, latest cache, `MonitoringEndReason`, BUFFER FULL auto-restart. Replay tests: multi-subscriber demux, drop-oldest, end reasons. Also landed: `IElmSession.LastMonitoringEndReason` — `MonitorFramesAsync` now records why it ended at every exit site (fixes audit A7 at the source). Hardware checkpoint (real-adapter BUFFER FULL restart) pending. | M/L | Core mechanics, no consumer changes ✔ |
| P2 — **DONE 2026-07-18** | Typed layer: generator conditionally implements `ICanFrame<TSelf>` + `FrameCanId` (interface-free compilations byte-identical — snapshot-verified); `Subscribe<T>()`/`TryGetLatest<T>()` extensions. Pilots migrated: `LeafAze0Hvac` + `LeafAze0MotorController` are now cache views over the shared monitor (warm cache = instant snapshot, cold = short warm-up wait); `LeafAze0CommandSet` owns one `CanMonitor` (`SharedBroadcastMonitor` accept-all context) and exposes it for direct typed streaming. | L | Consumer API + migration pattern ✔ |
| P3 — **arbitration DONE 2026-07-18; remainder pending** | Done: `CanMonitor.SuspendAsync` (subscribers/cache survive, no end reason recorded, scope-dispose resumes) + `MonitorSuspendingElmSession` decorator — UDS queries and legacy enter/exit capabilities transparently pause/resume the monitor, so ALL capabilities coexist with it today (whole-model replay test: HVAC stream → BMS UDS query → HVAC warm-cache read, monitor running throughout). Pending: migrate remaining broadcast capabilities off the legacy enter/exit pattern (they work correctly via the decorator meanwhile), keep-alive integration, hardware validation. | L | Whole-system model ✔ (via decorator); native migration pending |

Each phase lands green against `ReplayElmTransport`; hardware validation checkpoints after P1
(does BUFFER FULL restart behave on the real adapter?) and P2 (real-bus throughput with
accept-all filter — if the ELM327 chokes, tighten the context's CM/CF mask).

## 5. Risks

- **ELM327 throughput.** ATMA with accept-all on EV-CAN can overrun cheap clones (BUFFER FULL).
  Mitigations: auto-restart, hardware mask when subscriber set allows it, and the drop-oldest
  channels mean the app degrades to "slower updates," never "stale forever."
- **BLE single connection.** Nothing new — same transport, same session; the monitor just owns
  the mode for longer stretches.
- **Query starvation under continuous monitoring** until P3 arbitration lands — documented
  limitation of P1/P2.
- **11-bit only** — inherited from `TryParseMonitoringFrame` (audit A3); unchanged here.

## 6. Test plan

`ReplayElmTransport` already supports scripted monitoring (silent `ATMA`, `EnqueueIncoming`
frame injection, timed no-data reads). P1 test matrix:

- Two subscribers with disjoint ID sets each receive only their frames.
- Slow subscriber: channel drops oldest, newest survives; fast subscriber unaffected.
- `TryGetLatest` returns newest frame after burst; cold cache returns false.
- `BUFFER FULL` line → auto-restart re-enters monitoring (scripted `ATMA` twice) → after retries
  exhausted, `EndReason == BufferFull` and all subscriber streams complete.
- `StopAsync` → `EndReason == Stopped`, adapter back in request/response mode (prompt drained).
