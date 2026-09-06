# Streaming Monitor Design

**2026-09-05 hardware correction:** Leaf ABS, HVAC, and VCM `GetStatusAsync` now
start the shared monitor and immediately project its available cache, including partial
or empty evidence. They no longer wait for unrelated absent frames on each poll.
Use streams or explicit `WaitForCacheAsync` when waiting for acquisition is intended.
This pre-1.0 behavior change preserves timestamps/quality and method signatures;
it supersedes cold-cache wait descriptions for these three status methods below.

**Status:** P1–P4 implemented. P1–P3: shared `CanMonitor`, typed layer, capability migration,
`SuspendAsync` arbitration, filter rotation. P4: streaming members on the capability interfaces,
typed per-signal telemetry streams, short-frame decoding (see the P4 row and §7).
**Date:** 2026-07-18 (P4: 2026-08-31)

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

**Implemented mode arbitration:** `CanMonitor.SuspendAsync` returns an async-disposable
scope. The command-set decorator uses it around diagnostic work. The session reader is
joined before the stop prompt is consumed; internal suspension joins in-flight window
configuration rather than canceling it halfway through. An invalidated session is never
resumed. See [transaction safety](ELM_TRANSACTION_SAFETY.md) for the current contract.

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
| P3 — **DONE 2026-07-18** | Arbitration: `CanMonitor.SuspendAsync` + `MonitorSuspendingElmSession` decorator (whole-model replay test: HVAC stream → BMS UDS query → HVAC warm-cache read, monitor running throughout). Broadcast capabilities migrated to cache views: ABS, Brake, BodyControl, Charger, VCM (helper split folded) — legacy per-capability enter/exit code deleted. Session-activation + keep-alive hooks built: `StartAsync` activates when `RequiresSessionActivation` (cold start only); a keep-alive timer runs brief suspend→TesterPresent→resume cycles (`IElmSession.SendKeepAliveAsync`, tolerant of suppress-positive silence), serialized with query arbitration via a control gate; `StopAsync` serializes with cycles so a mid-cycle resume cannot revive a stopped monitor. Replay-tested (activation ordering incl. EPS header; keep-alive cycle with subscriber survival). **Steering wiring decision deferred to hardware data:** putting activation/keep-alive on the shared accept-all context imposes a ~2s suspend cycle on ALL monitoring; alternative is an on-demand steering monitor session. Steering stays on the decorator until measured. | L | Whole-system model ✔ |
| P4 — **DONE 2026-08-31** | Consumer streaming surface: `StreamStatusAsync` on the broadcast capability interfaces (charger: `StreamChargingStatusAsync`) over a new `CanMonitor.StreamSnapshots` coalescing helper; `ITelemetrySession.Stream<T>(TelemetrySignal<T>)` with typed handles in `Signals`; `TelemetrySession.Batches` registration made eager; generated frames carry `MinimumLength` so sub-8-byte frames (0x421, 0x176) decode through the typed path instead of being silently skipped. Replay-tested: coalescing across contributing frames, eager registration, throttle, survival across a UDS suspension, no-resurrect on an ended monitor, typed signal values, first-batch-not-missed. | M | Consumer API complete ✔ |

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

## 7. P4 — the consumer streaming contract

P2 gave `CanMonitor.Subscribe<T>()`, but nothing on the public surface handed it out: every
capability interface was pull-only, so an app holding `IVehicleSession` had to downcast to
`LeafAze0CommandSet` to reach `Monitor` (roadmap API design flag #1). P4 closes that at two
levels — capability status streams, and typed per-signal telemetry streams.

### 7.1 Capability streams

```csharp
IAsyncEnumerable<HvacStatus> StreamStatusAsync(TimeSpan minInterval = default, CancellationToken ct = default);
```

Implemented by `CanMonitor.StreamSnapshots(canIds, snapshot, minInterval, ct)`: subscribe to the
IDs that feed the DTO, and rebuild the DTO whenever any of them arrives. Each capability's
projection now lives in a private `BuildStatus()` shared by the pull and stream paths, so the
two cannot drift.

Decisions, and why:

- **Coalesce on any contributing frame.** A status DTO spans frames with different cadences
  (`HvacStatus` = 0x54A/54B/54C/54F). Waiting for all of them would emit at the slowest rate and
  stall entirely if one ID never appears — which is the normal case on stock adapters. The
  newest frame triggers the emission; the rest comes from the cache.
- **Emissions are built when the consumer pulls,** not when the frame arrives. A slow consumer
  therefore gets fresher data rather than a queue of stale records — the same reasoning as the
  monitor's drop-oldest channels. (Tests must step the enumerator between frames to observe
  coalescing; draining at the end only ever shows the final cache state.)
- **`minInterval` skips, never queues.** For 10 ms broadcast data a backlog is worthless; the
  next frame after the window carries the newest state.
- **Cold start emits partial records.** Fields whose IDs have not been seen are null/default,
  matching the pull API's degradation contract (absence is null, never an exception).
- **Registration is eager.** `StreamStatusAsync` is not an async iterator: it registers with the
  monitor synchronously, then returns the iterator. Deferring to the first `MoveNext` would drop
  every frame arriving between creation and iteration.
- **A stream never resurrects an ended monitor.** It starts one that has not run yet, but a
  monitor that has been stopped stays stopped and the stream completes (`EndReason` says why).

### 7.2 Typed telemetry streams

`ITelemetrySession.Batches` yields `TelemetryValue` — a union-by-convention of
`decimal? / IReadOnlyList<decimal>? / bool?` that every consumer had to switch on. `Stream<T>`
takes a phantom-typed handle instead:

```csharp
await foreach (var sample in session.Stream(Signals.StateOfCharge, ct))  // TelemetrySample<decimal>
```

Handles come only from `Signals`, so a handle cannot claim a type its signal does not produce.
Ticks where the signal has no value are skipped, so every emission carries a real value; the
`Availability` map still says whether a quiet signal is cold or unsupported. `Batches` itself was
an async iterator and so registered its channel on first `MoveNext` — a consumer that stored the
enumerable and iterated later silently missed ticks. It now registers when called, and `Stream<T>`
is a projection over that same subscription.

### 7.3 Short frames

The generator emitted a `Parse` that threw unless the payload was exactly 8 bytes, and the typed
monitor extensions silently skipped anything shorter — so 0x421 (1 byte), 0x176 (7) and 0x260 (4)
had typed streams that never yielded. Frames now carry `MinimumLength` (the highest byte any
signal touches) and decode from any payload at least that long; `CanBits` zero-extends a short
payload. `ICanFrame<TSelf>` exposes `MinimumLength` so generic consumers filter on it.

No LINQ-over-`IAsyncEnumerable` dependency was added: these target net10.0, where
`System.Linq.AsyncEnumerable` is in-box, so consumers get `Where`/`Select`/`Take` for free.
