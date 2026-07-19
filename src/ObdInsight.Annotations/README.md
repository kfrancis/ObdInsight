# ObdInsight.Annotations

Attribute types for declarative, DBC-shaped CAN frame and UDS response definitions —
`[CanFrame]`, `[CanSignal]`, `[UdsService]`, `[UdsPid]`, `[UdsField]` and friends —
plus the `CanBits` bit-extraction helper. Dependency-free.

Pair with the **`ObdInsight.SourceGeneration`** analyzer package, which turns these
definitions into zero-reflection `Parse(ReadOnlySpan<byte>)` decoders and typed UDS
query methods:

```csharp
[CanFrame(0x1DB, Description = "Battery status (10ms)")]
public partial class BatteryFrame
{
    [CanSignal(13, 11, IsSigned = true, Factor = 0.5, Unit = "A")]
    public partial double Current { get; init; }
}
```

You only need this package directly when defining your own frames; consumers of the
built-in vehicle support get it transitively via `ObdInsight.Core`.

Docs: [repository](https://github.com/kfrancis/ObdInsight)
