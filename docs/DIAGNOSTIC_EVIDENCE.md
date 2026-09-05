# Diagnostic evidence contract

Implemented 2026-09-04, before 1.0. This replaces the earlier B5 empty-list degradation
contract. It is one diagnostic-truth tranche, not a new general result framework.

## DTC outcomes and coverage

`IDiagnosticTroubleCodes.GetDtcsAsync` returns `DtcReadResult.Stored` and `.Pending`,
independent `DtcModeResult` values. Each mode preserves observed responders by CAN ID.
Collections are defensively copied and exposed read-only.

| Status | Meaning | Aggregate `Codes` |
| --- | --- | --- |
| Succeeded | At least one responder; all observed responses validated | Present, possibly empty |
| Partial | Some valid responders plus invalid/unattributed data | Null; use individual responder evidence |
| NoData | No response evidence (including a direct NO DATA reply) | Null |
| InvalidResponse | Observed replies could not be validated | Null |
| QueryFailed | Session/I/O failure without trustworthy response evidence | Null |
| Timeout | Operation deadline, not caller cancellation | Null |

An individual responder's null `Codes` means an invalid, incomplete, or negative reply.
Negative replies are not yet decoded into service-specific rejection categories.
The reader does not infer "unsupported" from silence. The current `ElmSession` can
collapse NO DATA and adapter failures into `IOException` after recovery; those are
honestly `QueryFailed`, not a cause guessed from exception text.

`Succeeded` with zero codes means **no codes reported by the observed responders**.
Functional addressing does not establish a census of installed ECUs. Silent ECUs,
adapter filtering, non-OBD manufacturer diagnostics, and unqueried services limit
coverage. Never translate an empty result into "vehicle is healthy".

```csharp
var snapshot = await telemetry.GetSnapshotAsync(cancellationToken);
var stored = snapshot.DiagnosticTroubleCodes?.Stored;
if (stored?.Status == DtcReadStatus.Succeeded)
{
    // Store codes AND observed CAN IDs as report evidence.
    var codes = stored.Codes!;
    var responders = stored.Responders;
}
// Missing capability, failure, and partial coverage are not clean results.
```

Snapshots keep the full result in `DiagnosticTroubleCodes`; null means no capability.
An operational exception from an alternative capability is retained as failure for
both modes because the snapshot cannot recover its intermediate evidence. Programming
and lifecycle errors propagate instead of being relabeled as missing capability.

Caller cancellation is checked before each mode and after queries, and stops the
second query. Internal deadline cancellation from current ELM framing maps to Timeout.
No new retry, lock, background task, ownership rule, or physical-query concurrency is
introduced. The existing arbitrated session remains the Leaf execution path.

## DTC parsing boundary

Supported input is the current stack's header-bearing classical 11-bit CAN line
format with ISO-TP PCI bytes. Each responder is assembled independently. Declared
length, first-frame size, consecutive-frame sequence (including wrap), completeness,
response SID, code count, and zero padding are validated before codes are trusted.
Odd hex, invalid bytes, orphan/duplicate/out-of-order frames, and unknown text cannot
silently disappear into a clean result. This does not add 29-bit, headerless, or
adapter-numbered payload support by heuristic inference.

The subsequent [strict-decoding tranche](DIAGNOSTIC_DECODING.md) consolidates this
validator with the other runtime ISO-TP paths. DTC-specific SID/count/padding checks
and the mode/responder outcome contract remain separate from transport reassembly.

## Nissan Hx is not state of health

`BatteryStatus.HealthPercent` was misleading and is replaced by
`StateOfHealthPercent`. Leaf Group 01's field is now explicitly `HxPercent` in the
internal generated diagnostic schema and is **not** mapped to generic SOH. The
golden AZE0 capture still decodes Hx as 35.44, but Leaf status, telemetry, and snapshots
leave SOH null. No new public OEM capability is added solely to preserve a misnamed
field. Hx remains an internal diagnostic metric in this tranche.

Evidence: checked-in `vehicle_nissanleaf.cpp`, `PollReply_Battery` around lines 688–731,
extracts Hx separately from capacity-derived SOH; instrument SOH is handled separately
around lines 1837–1847. `CAR-can_AZE0.dbc` also describes 0x5B3 SOH. A future SOH
provider must deliberately select/validate its source, vehicle applicability, and
freshness. Neither Hx nor an assumed nominal battery capacity is a safe fallback.
No new hardware validation is claimed by this change.

## Pre-1.0 migration

- Replace `result.StoredCodes` / `PendingCodes` with the corresponding mode's outcome;
  inspect status and retain responder evidence before interpreting `Codes`.
- Replace snapshot `StoredDtcCodes` / `PendingDtcCodes` with `DiagnosticTroubleCodes`.
- Rename generic battery `HealthPercent` to `StateOfHealthPercent`; Leaf's former
  numeric value was Hx and must not be migrated as an SOH observation in stored data.
- Existing persisted reports that treated missing DTCs as clean or Hx as SOH require
  application-specific invalidation/relabeling; recompiling cannot repair them.
