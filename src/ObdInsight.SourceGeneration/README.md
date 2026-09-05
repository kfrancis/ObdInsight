# ObdInsight.SourceGeneration

Roslyn incremental generators for ObdInsight (analyzer-only package):

- **CAN signal decoders** — `[CanFrame]`/`[CanSignal]` partial classes get generated
  `Parse(ReadOnlySpan<byte>)` methods, a per-namespace bit helper, and a
  `CanFrameRouter`; frames implement `ICanFrame<TSelf>` for typed
  `CanMonitor.Subscribe<T>()` streams.
- **UDS query methods** — `[UdsService]`/`[UdsPid]`/`[UdsField]` definitions generate
  `Query{Name}Async` methods with ISO-TP reassembly and response-variant field
  selection.

UDS authoring also requires `ObdInsight.Core`: emitted queries use its strict ISO-TP
parser and the exact `_context.RxFilter`. Response lengths exclude the two-byte
SID/PID header; variants require exact lengths. Malformed input never becomes a
partial decoded response. Nullable arrays preserve invalid elements as null slots;
nonnullable invalid elements fail the response. `OBDUDS001` diagnoses unsupported
or inconsistent schemas. Custom parsing helper methods are no longer required.

Reference alongside **`ObdInsight.Annotations`** (the attribute types) when defining
your own frames:

```xml
<PackageReference Include="ObdInsight.Annotations" Version="..." />
<PackageReference Include="ObdInsight.SourceGeneration" Version="..." PrivateAssets="all" />
```

Limitations: 11-bit CAN IDs, Intel (little-endian) bit order (bit 0 = LSB of byte 0).

Docs: [repository](https://github.com/kfrancis/ObdInsight)
