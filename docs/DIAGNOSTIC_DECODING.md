# Strict diagnostic decoding boundary

Implemented after the diagnostic-evidence tranche, based on commit `8193a49`.
This is a pre-1.0 contract change. It does not change session ownership, reconnection,
or the timestamp/freshness model.

## One ISO-TP validation path

`IsoTpParser.ParseResponses` groups evidence by CAN responder and validates classical
11-bit CAN ISO-TP: PCI, declared length, first-frame geometry, consecutive sequence
including wraparound, and completeness. Each `IsoTpResponse` retains `CanId`,
`ExpectedLength`, and `Error`. Failed responses expose no partial payload. Unknown
text is retained as batch-level invalid evidence. NO DATA alone means no responders.

The payload memory is owned and unpooled; callers must not mutate its backing storage.
Response collections are read-only. Parsing creates no background work and has no
session lifetime or concurrency requirement beyond the input not changing mid-call.

`TryReadPayload` succeeds only for exactly one valid responder, with no other invalid
evidence. An optional expected responder must be an exact three-digit hex ID. A
wildcard is not evidence of which ECU answered. Generated queries pass the current
ECU's `RxFilter` and their request echo. Functional DTC reading instead consumes
the batch, preserving independent responder outcomes and partial-read semantics.

Supported text formats are header-bearing lines with or without spaces and
concatenations of full 19-character frames (three hex ID characters plus eight
data bytes). Short final frames are accepted only when they contain all remaining
declared payload bytes. There is no scanning for CAN-looking payload bytes, repair
of short first frames, odd-nibble truncation, raw-hex fallback, or headerless/29-bit
format inference. Adapter-specific unsupported formats fail closed.

`ParseIsoTpResponse` remains a convenience returning an empty list on invalid or
ambiguous data. `ParseHexString` now returns no bytes for malformed hex, rather than
a parsed prefix. Use structured outcomes where the failure distinction matters.

The duplicate Leaf and DTC reassemblers have been removed. DTC mode results and
responder evidence from the prior tranche are preserved. Leaf VIN parsing also
rejects invalid characters rather than filtering them into a different identity.

## Generated UDS contracts

Generated queries use the shared parser in **ObdInsight.Core**. UDS schema authors
must reference Core in addition to Annotations and the analyzer package; ordinary
application consumers still do not need the generator. The generator itself does
not depend on Core at runtime. No caller-supplied `ParseIsoTpFrames` or
`ReassembleIsoTpPayload` convention is required anymore. `_session` and `_context`
remain the existing authoring convention.

- Bounds and variant lengths count **data bytes after the two-byte SID/PID header**.
- `MinLength` is enforced; `MaxLength = 0` means no schema upper bound. Field geometry
  is always enforced. The transport's classical ISO-TP size limit still applies.
- Variants require exact declared lengths. There is no nearest-length fallback.
- Every field and array must fit inside the validated declared payload, never padding.
- Schema-range violations fail nonnullable measurements and leave nullable
  measurements null, including when a response class has an initializer. Numeric
  representability failures fail the response rather than overflow or wrap.
- Array indexes are never compressed. Nullable element arrays preserve null slots;
  invalid elements of nonnullable arrays fail the whole response.
- Numeric conversions reject nonfinite/out-of-range values and checked-conversion
  overflow. Scale literals explicitly use double arithmetic and invariant formatting.
- Frame-sourced fields address the validated classical payload: first-frame offsets
  include the SID/PID; CF sequence selects its first occurrence (1..15, then 0).
  Multiple fields from one frame have independent scopes. Missing frames/bytes fail.
- Caller cancellation is checked before and after the query. Existing protocol
  failure semantics remain: invalid/unsupported payloads return null.

`OBDUDS001` reports invalid bounds/variant ambiguity, unsupported numeric or wire
types, invalid geometry, invalid ranges/scales, unknown variants, and nonnullable
variant-optional fields. This does not claim that every possible malformed C# schema
has a custom diagnostic; compiler errors still protect unsupported authoring shapes.
Signed 8/16/24-bit types are rejected rather than silently generating zero.

Generator tests now compile generated UDS output against the runtime parser. Tests
also execute generated queries for bounds and narrowing/overflow, and compare output
under different cultures. Reflection used to load compiled tests is confined to test
infrastructure; no reflection/activation was added to generated or runtime code.

## Indexed cells through TestDrive telemetry

`CellVoltageData` now defensively copies `IEnumerable<int?>` values and optional
balancing flags. `CellVoltagesMv` is `IReadOnlyList<int?>`: count and position describe
physical cells, not how many readings happened to validate. `ValidCellCount` and
`IsComplete` make that distinction explicit. Balancing flags must have the same
physical count and remain aligned even when a voltage is missing.

Min/max/average/delta are computed from the owned values and are null unless the
whole nonempty set is valid. A partial set is not allowed to report pack-wide
statistics computed from a healthy-looking subset.

Telemetry vectors, typed cell streams, and snapshot `CellVoltagesV` now use
`IReadOnlyList<decimal?>`. Invalid entries stay null at the same index during unit
conversion and plausibility validation. The generic vector may be present even when
some or all entries are missing; applications must inspect slots, not infer complete
availability from a non-null vector. This tranche does not redesign the broader
availability enum or observation quality model.

```csharp
var cells = await bms.GetCellVoltagesAsync(cancellationToken);
if (cells is not null)
{
    for (var index = 0; index < cells.CellCount; index++)
    {
        int? millivolts = cells.CellVoltagesMv[index];
        // Persist the index even when millivolts is null.
    }
    // Only a complete cell set has pack-wide statistics.
    int? spread = cells.DeltaVoltageMv;
}
```

## Leaf applicability and fixtures

Group 01 keeps its exact declared 39/41/49-byte data layouts. Group 04 is limited to
the implemented AZE0 14-byte data layout; the differently laid out 29-byte ZE1
response is no longer decoded with AZE0 offsets. Group 06 is exactly 24 data bytes;
Group 02 accepts 192–196 bytes (96 cell pairs plus the known optional trailer).
Golden captured BMS payloads remain unchanged and pass. These declarations are not
new hardware support claims for other variants.

The **synthetic** VIN fixture's last CAN frame had an odd number of hex digits. It
was corrected to eight data bytes; its declared payload and VIN remain unchanged.
There is no special parser exception for this fixture.

## Migration and limits

- Replace `CellVoltageData` object initializers with its constructor; statistics are
  derived, not caller-supplied. Use `Count` instead of array `Length` on public lists.
- Change typed cell consumers to `IReadOnlyList<decimal?>`; preserve missing indexes
  in persistence, analytics, and reporting. Never filter nulls and renumber cells.
- Third-party UDS authoring must reference Core and supply an exact `RxFilter`.
- Previously accepted corrupted/ambiguous payloads now fail. Explicitly add and test
  genuinely supported layouts; do not restore fuzzy detection to regain acceptance.
- Reconnect generations, stale cache invalidation, transport EOF/deadlines, producer
  completion, and physical-device validation remain separate work.
