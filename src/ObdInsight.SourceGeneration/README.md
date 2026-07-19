# ObdInsight.SourceGeneration

Roslyn incremental generators for ObdInsight (analyzer-only package):

- **CAN signal decoders** — `[CanFrame]`/`[CanSignal]` partial classes get generated
  `Parse(ReadOnlySpan<byte>)` methods, a per-namespace bit helper, and a
  `CanFrameRouter`; frames implement `ICanFrame<TSelf>` for typed
  `CanMonitor.Subscribe<T>()` streams.
- **UDS query methods** — `[UdsService]`/`[UdsPid]`/`[UdsField]` definitions generate
  `Query{Name}Async` methods with ISO-TP reassembly and response-variant field
  selection.

Reference alongside **`ObdInsight.Annotations`** (the attribute types) when defining
your own frames:

```xml
<PackageReference Include="ObdInsight.Annotations" Version="..." />
<PackageReference Include="ObdInsight.SourceGeneration" Version="..." PrivateAssets="all" />
```

Limitations: 11-bit CAN IDs, Intel (little-endian) bit order (bit 0 = LSB of byte 0).

Docs: [repository](https://github.com/kfrancis/ObdInsight)
