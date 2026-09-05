# Owned diagnostic recovery

This design supersedes the earlier B10 byte-reconnection design. Transparent
`ReconnectingElmTransport` and `ReconnectOptions` were removed before 1.0:
replacing the byte stream cannot preserve a partially executed diagnostic transaction.

## Boundary and dependency direction

`VehicleConnection` lives in Telemetry because it owns the consumer graph, including
telemetry. Core does not depend on Telemetry or a platform transport. Its command-set
contract now includes async disposal; the Leaf command set owns its monitor.

```
TestDrive → VehicleConnection (Telemetry)
              ├─ factory → platform IElmTransport
              └─ each ready generation
                   ├─ private non-replacing transport guard
                   ├─ fresh ElmFramer → initialized ElmSession
                   ├─ VehicleResolver + explicit profiles → owned command set/monitor
                   └─ fresh TelemetrySession
Telemetry → Core ← platform transports
```

No reflection discovery, DI container, generic plugin system, or new package is
needed. Expert applications may still compose ElmSession, raw CAN, command sets,
and monitors themselves. Disposing a command set does not dispose its supplied
session/transport; the connection owner disposes the entire graph in order.

## Consumer contract

```csharp
await using var connection = new VehicleConnection(
    () => new PluginBleElmTransport(adapter, deviceId),
    [new NissanLeaf()],
    wakeupStrategy: new LeafBmsWakeupStrategy());

var generation = await connection.OpenAsync(ct);
var pre = await generation.Telemetry.GetSnapshotAsync(ct);
await generation.Telemetry.StartAsync(ct);
// Record batches concurrently with the drive controller.
// Tag persisted batches with generation.Number.
```

A generation is borrowed, not independently owned. Its telemetry/capabilities must
not be retained across `Ended`. The owner does not automatically start recording:
after loss, finish/drain the old reader, preserve its error and buffered evidence,
then explicitly acquire a newer ready generation:

```csharp
Exception? loss = await generation.Ended.WaitAsync(ct);
var next = await connection.WaitForReadyAsync(generation.Number, ct);
await next.Telemetry.StartAsync(ct);
// Create a NEW stream and record a NEW segment tagged with next.Number.
```

`Ended` signals invalidation (error on loss; null on owner shutdown), not completion
of teardown. A replacement is not published until teardown has joined the old graph
and a new graph has initialized and identified the same VIN. The first VIN pins the
owner to that vehicle; a different VIN is fatal, requiring an explicit new owner.

`OpenAsync` is single-flight and starts one supervisor. Its token cancels only the
caller's wait; dispose the owner to cancel opening/recovery. `WaitForReadyAsync`
does not start supervision. It throws on a disposed/exhausted owner. `Completion`
faults on exhausted/fatal recovery or teardown failure and completes normally on
intentional shutdown. Disposing joins it without rethrowing its recorded fault.
An exhausted owner is not reusable; create another owner explicitly.

State events are ordered by the single supervisor. `Connected` means ELM initialized
and vehicle detected, not just GATT connected. `Reconnecting` means the previous
generation has ended; its I/O is never redirected. `Lost` is terminal, including
disposal. Handlers are synchronous, individually exception-isolated, and must not
block on owner disposal/completion; marshal UI notifications in the application.

## Recovery and ownership invariants

- Every factory result is fresh and exclusively transferred to the owner. Do not
  reuse a transport instance or share it with another consumer.
- Physical ConnectionLost, I/O failure (including flush), or nonempty read returning
  EOF ends that physical generation. No read/write/flush is retried on a replacement.
- Invalidation stops admission and cancels pending physical operations. A result
  arriving after invalidation is rejected. The guard joins outstanding operations
  before disposing the transport; no abandoned task can reuse a returned read buffer.
- Teardown invalidates transport I/O, terminates/disposes telemetry (including active
  snapshots), disposes the command set/monitor, then disposes the transport.
  Leaf monitor disposal joins keep-alive work and removes cached frames.
- Telemetry is terminated even when a vehicle capability catches an I/O error and
  returns missing data. The owner observes physical loss independently.
- Every candidate is disposed on failed open, initialization, detection, cancellation,
  or VIN mismatch. Late open success cannot be adopted after shutdown.
- Recovery uses a fixed configurable delay and an initialization deadline. Defaults:
  six retries after the initial attempt, 500 ms delay, 60 s initialization deadline.
  A successful ready generation resets the retry budget.
- Fresh framing/context/monitor caches are constructed every time. A stale callback
  is tied only to its old guard; it cannot adopt or invalidate a replacement.

Cancellation remains cooperative. A transport/profile ignoring cancellation can delay
shutdown; the owner joins it rather than pretending it is gone. Expert users must
not concurrently operate a borrowed command set during owner teardown. Use the
generation's telemetry facade for the supported snapshot/recording workflow.

## Deliberate limits

This is not gap-free recording: buffered batches still belong to the old generation.
Batches and snapshots now carry their owner-local ConnectionGeneration. Publication
timestamps remain separate from acquisition; [observation semantics](OBSERVATION_SEMANTICS.md)
defines within-generation quality, age and stale-value handling.

Quiet passive monitoring is not evidence of disconnection. An interrupted command
exchange is different: [transaction safety](ELM_TRANSACTION_SAFETY.md) now permanently
invalidates framing and ends the generation even without a physical-loss event.
The owner rebuilds the graph without replaying the interrupted operation. Normal
queries no longer retry; the expert retry decorator requires an explicit safe-command
allowlist and only retries rejected, complete responses.

Failed detection currently surfaces as an IOException with its detection status in the
message (available as the inner failure of a failed open); the owner publishes only
fully detected supported generations. Specialist unsupported-vehicle diagnostics
can still use the lower-level resolver directly.

## Validation

Deterministic owner tests cover fresh-generation recovery, pending streams, failed
open/init disposal, late success during shutdown, single-flight open, canceled waits,
backoff shutdown, uncertain-write non-replay, EOF, stale callbacks, and VIN mismatch.
BLE proxy tests verify pending readers terminate on loss/disposal. These are not
physical Android/iOS disconnect or suspend/resume tests.
