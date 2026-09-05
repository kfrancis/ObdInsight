# ELM transaction and interruption contract

Implemented pre-1.0 after observation evidence. This supersedes the old automatic
query recovery ladder and timeout-as-success monitoring cleanup.

## Ownership and atomicity

`ElmSession.QueryAsync(command, context, ct)` holds one gate across adapter reset,
ECU configuration, command write/flush and response receipt. No other session operation
can change the ECU between configuration and that query. Context settings are immutable;
activation/keep-alive context reuse compares the context instance, not just its display name.

`SetEcuContextAsync` followed by contextless `QueryAsync` is still two operations. It is
an expert convenience, not a context reservation. Use the context-bearing overload when
multiple callers share a session. Queries accept one nonempty command, not injected
CR/LF-separated command sequences. Configuration/settings must not be mutated concurrently.

The monitor's frame enumeration holds the session gate for its lifetime, including
between yielded frames. Cancel and join/dispose that enumeration before exiting monitoring.
Exit waits for ownership: it never forces a request/response mode flag after a lock timeout.
Use `CanMonitor` and the Leaf command set's existing suspension decorator for normal
mixed monitoring/query work. The framer also rejects overlapping raw operations rather
than letting a second reader consume another command's bytes. Once supplied to a session,
its framer and physical transport are exclusively used through that session.

## Single-attempt queries; explicit limited retries

Normal queries execute once. A complete, prompt-terminated reply rejected by validation
(including NO DATA) throws `ElmQueryRejectedException : IOException`. This describes
**local response validation**, not proof that the ECU did not execute the command.
Framing remains usable. Strict UDS schema decoding continues to return Invalid observation
evidence for malformed complete replies; those are not automatically retried either.

The old recovery ladder and `MaxConsecutiveFailures` property are removed. The expert
`RetryingElmSession` remains, but retries only `ElmQueryRejectedException` for commands
explicitly listed in `QueryRetryOptions.RetrySafeCommands`. This list is copied on
construction and is empty by default. The caller must know the command is safe to repeat.
Generic IOException, timeout, cancellation and invalidated-session failures are never
retried, even for allowlisted commands. The consumer owner does not install this decorator.

```csharp
IElmSession expert = new RetryingElmSession(session, new QueryRetryOptions
{
    RetrySafeCommands = ["2101"], // explicit application/vehicle-specific assertion
    MaxAttempts = 2
});
var reply = await expert.QueryResponseAsync("2101", context, ct);
```

## Lost response boundary means a new graph

A command write/flush/read failure after write admission has uncertain delivery. The
initial call retains its TimeoutException, caller-token OperationCanceledException,
or original I/O failure. Before returning, the framer permanently records
`ElmSessionInvalidatedException` in `Failure` and raises `Invalidated` synchronously once.
Subsequent framer I/O, buffer clear, and session queries/initialization reject that graph.
Clearing currently buffered bytes cannot prove a delayed response will not arrive later.
Partial ECU configuration also invalidates the session; it is not a valid cached context.

Cancellation before admission or while queued for the session gate does not write or
invalidate it. Once an adapter-state transition is admitted, cancellation can invalidate
the graph even between commands. An interrupted raw prompt read likewise invalidates it.
Quiet CAN-line read deadlines and normal reader cancellation do not invalidate framing.

This implementation deliberately chooses graph invalidation over in-place resynchronization.
There is no reset/clear-failure escape hatch. Dispose the old connection and open/initialize
a fresh transport/framer/session graph. Factory implementations must honor fresh physical
connection ownership; merely constructing another framer over the old byte stream is not
recovery. Hardware validation must verify adapter behavior across that physical reopen.

`VehicleConnection` listens for framing invalidation as well as physical loss. Its handler
immediately closes physical I/O admission and invalidates telemetry, then its existing
supervisor joins teardown and builds the replacement. Swallowed capability timeout results
cannot become a successful old-generation snapshot. A canceled snapshot is never replayed.
Recording explicitly starts new subscriptions on the newer ready generation, as before.
Invalidated event handlers must be short/nonblocking; handler exceptions are isolated.

## Monitoring transitions

Leaving monitoring requires the actual ELM prompt. BUFFER FULL's trailing prompt is
consumed, not cleared optimistically. A missing stop prompt invalidates the graph; no
subsequent diagnostic request is admitted. An already observed stop prompt does not
cause an extra bare CR that could repeat the adapter's last command.

Internal rotation/suspension cancellation joins an in-flight, command-deadline-bounded
configuration before stopping its reader. It does not cancel AT configuration halfway
through and then resume on a partially configured adapter. Physical owner invalidation
still cancels the underlying I/O; no task or buffer is abandoned to speed up shutdown.
An invalidated suspended monitor does not resume, clears its cache, ends subscriptions
with TransportError and preserves the original query's cancellation/timeout outcome.

Suppressing an ECU's positive TesterPresent response does not suppress the ELM prompt.
A prompt-terminated empty reply is acceptable; a missing prompt is not keep-alive success.

## Consumer implications and limits

Expect more explicit failures and fresh-generation recovery where the old code silently
retried or continued. This is preferable to attributing an old reply to a new query, but
adapter-specific deadlines must be checked on hardware. CacheReadTimeout still covers
lazy capability/monitor startup as well as cold-cache waiting; do not configure it shorter
than the hardware setup you ask the capability to perform.

No byte API can guarantee bounded shutdown against a transport that never completes
canceled I/O. No guarantee is made about proprietary adapters ignoring prompts or
retaining old replies across physical reopen. No SLCAN UDS/reconnection or hardware
smoke runner was added. The next checkpoint is the consumer-path BLE and passive-SLCAN
hardware smoke runner, followed by actual stationary adapter tests.
