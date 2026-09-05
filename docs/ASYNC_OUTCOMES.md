# Terminal asynchronous outcomes

Implemented after strict diagnostic decoding, before 1.0. This contract supersedes
older documentation describing partial ELM replies as successful responses or all
provider exceptions as unavailable measurements.

## Framing

`ElmFramer.SendAndReadFrameAsync` returns only after the ELM prompt. Its deadline
covers command write, flush, and response read. `ReadUntilAsync` likewise requires
its delimiter. Both preserve bytes received beyond the delimiter for the next read.

| Outcome | Contract |
|---|---|
| Caller cancellation | `OperationCanceledException` with the caller token |
| Internal deadline, including a partial response | `TimeoutException` |
| Nonempty transport read returns zero before delimiter | `EndOfStreamException` |
| Complete response | Return the response without delimiter |

`DataIdleTimeout` is removed. Silence is not a framing delimiter. This is an
intentional pre-1.0 behavior/API break: adapters that omit the prompt now fail
instead of supplying apparently complete diagnostic evidence. Transport reads must
wait for bytes, cancellation, or termination; zero must not mean "try again".
Cancellation is cooperative; the framer does not abandon in-flight I/O or recycle
its buffer while a transport might still be writing into it.

After timeout/cancellation/EOF, partial text is discarded. A delayed reply may still
arrive, so [transaction safety](ELM_TRANSACTION_SAFETY.md) now invalidates an interrupted
command exchange permanently. The owning VehicleConnection ends that generation and
reinitializes a fresh physical graph. Queued cancellation and quiet CAN reads do not
invalidate an otherwise healthy session.

`ObdDtcReader` maps explicit `TimeoutException` to timeout evidence. It no longer
guesses that an unrelated `OperationCanceledException` means timeout. The ELM
suppress-positive-response keep-alive path still requires the adapter prompt: missing
that prompt is framing failure, not proof that the keep-alive succeeded.

## Telemetry run ownership

One reserved worker owns startup probing and scheduling. Probes, ticks, and snapshots
share the same bus gate. Concurrent starts share startup rather than issuing duplicate
probes. The initiating start token cancels the probe; after successful startup it no
longer owns the run. Other start callers cancel only their own wait.

`ITelemetrySession.Completion` identifies the current/most recent run. Before any start
it is already successfully completed. Capture it after invoking/awaiting `StartAsync`;
starting another run replaces it, without changing previously captured tasks.

- Explicit stop, or cancellation during startup, completes run streams normally.
  The startup caller still observes cancellation.
- An I/O error or unexpected provider/scheduler exception faults `Completion` and
  every run stream. An unexpected provider cancellation is a **faulted**, not a
  successfully stopped, completion task.
- A query timeout or bounded cold-cache timeout remains a missing sample. Providers
  return `TelemetryValue.Empty` for expected absence; programming errors and broken
  links must not be disguised as absence by the facade.
- Buffered batches drain before a stream completes or throws. Typed streams inherit
  the same termination. A late subscription observes the completed run's outcome.
  Subscribe again after restarting; subscriptions do not silently cross run boundaries.
- Subscriptions registered before the first start belong to that first run. Stopping
  before the first start completes those subscriptions.
- Stop cancels and joins the worker and its cancellation callbacks. Canceling the
  stop token cancels only the wait, never releases worker ownership. A subsequent
  stop/dispose still joins it. Restart while stopping throws `InvalidOperationException`.
- Stop/dispose do not rethrow the recorded producer failure; observe `Completion`
  or the stream. Exceptions thrown by cancellation callbacks are not producer errors
  and can still fail shutdown; provider cancellation callbacks must not throw.
- Disposal is idempotent, rejects new start/subscription/snapshot work, joins the
  worker and already-active snapshots, and detaches the state event. It does not
  own/dispose the supplied capabilities, providers, monitor, or transport. Snapshot
  cancellation remains the snapshot caller's responsibility; noncooperative providers
  can delay shutdown. The managed bus semaphore stays undisposed so queued snapshot
  callers can safely observe disposal instead of racing a released semaphore.

For TestDrive, stream failure means recording ended unexpectedly, not "the vehicle
has no measurements". Preserve recorded batches and the failure; decide separately
whether to establish a new diagnostic connection/run. This contract does not claim
gap-free recording, new acquisition timestamps, or freshness across reconnect.

The subsequent [owned-recovery migration](RESILIENCE_DESIGN.md) adds
`VehicleConnection`, which independently observes physical loss and terminates the
entire old telemetry generation even if a Leaf capability catches its I/O error.
Direct low-level compositions still only propagate exceptions that reach this facade.
Acquisition freshness within a generation remains separate from connection recovery.

```csharp
await telemetry.StartAsync(ct);
Task runCompletion = telemetry.Completion;
// Run the recorder concurrently with the UI/drive controller.
// The controller calls StopAsync when the drive ends.
await foreach (var batch in telemetry.Batches(ct))
    await recorder.AppendAsync(batch, ct);
await runCompletion; // Also usable by consumers that receive batches through events.
```

## Events

`BatchAvailable` and `ConnectionStateChanged` invoke each handler independently;
exceptions are logged and do not prevent later handlers or terminate production.
Callbacks are synchronous on the notifying thread, without UI dispatch. They must
be short and must not synchronously wait for stop/disposal of the producer invoking
them. A callback already in flight can finish during disposal. Use async streams
for asynchronous processing, and marshal to the UI in the application.

## Validation boundary

Deterministic tests cover framing EOF/partial input/deadlines/caller tokens/write and
flush timeouts, shared startup, cancellation, terminal waiting and late readers,
callback isolation, buffered drain, restart, and dispose-during-snapshot. Existing
replay and simulated-drive suites protect normal acquisition. These do not substitute
for hardware disconnect tests or published Android/iOS testing.
