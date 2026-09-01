using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ObdInsight.Core.Protocols;
using ObdInsight.SourceGeneration;
using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.DevTools.Commands;

/// <summary>
///     Compares the <c>[CanSignal]</c> definitions compiled into Core against the DBC files they were
///     transcribed from, and reports every disagreement.
///     Read-only by design: it changes nothing and asserts nothing, because most differences need a
///     human decision. A signal absent from the DBC may be a community discovery rather than an
///     error, and a differing factor may be a deliberate unit choice.
///     The one category that is almost always a genuine defect is **byte order**. A signal declared
///     Intel where the DBC says <c>@0</c> reads a different set of bits entirely, and it fails
///     silently - the value is plausible, just wrong. Checking a single frame this way (0x55B) found
///     two such signals, which is what prompted sweeping all of them.
/// </summary>
public static class DbcAudit
{
    /// <summary>
    ///     <c>SG_ Name : start|len@order sign (factor,offset) [min|max] "unit" receivers</c>
    ///     Order is 0 for Motorola/big-endian and 1 for Intel; sign is + for unsigned, - for signed.
    /// </summary>
    private static readonly Regex SignalLine = new(
        @"^\s*SG_\s+(?<name>[A-Za-z0-9_]+)\s*:\s*(?<start>\d+)\|(?<len>\d+)@(?<order>[01])(?<sign>[+-])\s*" +
        @"\((?<factor>[^,]+),(?<offset>[^)]+)\)\s*\[(?<min>[^|]*)\|(?<max>[^\]]*)\]",
        RegexOptions.Compiled);

    /// <summary>
    ///     <c>BO_ &lt;decimal id&gt; &lt;name&gt;: &lt;dlc&gt; &lt;transmitter&gt;</c>
    /// </summary>
    private static readonly Regex MessageLine = new(
        @"^BO_\s+(?<id>\d+)\s+(?<name>[A-Za-z0-9_]+)\s*:",
        RegexOptions.Compiled);

    public static int Run(string[] args)
    {
        var dbcPaths = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (dbcPaths.Count == 0)
        {
            Console.Error.WriteLine("usage: ObdInsight.DevTools.exe dbc-audit <file.dbc|directory> [...]");
            return 2;
        }

        var files = new List<string>();
        foreach (var p in dbcPaths)
        {
            if (Directory.Exists(p))
            {
                files.AddRange(Directory.GetFiles(p, "*.dbc", SearchOption.AllDirectories));
            }
            else if (File.Exists(p))
            {
                files.Add(p);
            }
            else
            {
                Console.Error.WriteLine($"error: not found: {p}");
                return 2;
            }
        }

        if (files.Count == 0)
        {
            Console.Error.WriteLine("error: no .dbc files found.");
            return 2;
        }

        // A CAN ID can appear in more than one DBC (CAR-CAN and EV-CAN overlap), so keep every
        // definition and let a signal match any of them rather than picking one arbitrarily.
        var dbc = new List<DbcSignal>();
        foreach (var file in files)
        {
            var before = dbc.Count;
            dbc.AddRange(ParseDbc(file));
            Console.Error.WriteLine($"parsed {Path.GetFileName(file)}: {dbc.Count - before} signals");
        }

        var frames = DiscoverFrames();
        Console.Error.WriteLine(
            $"reflected {frames.Count} frames with {frames.Sum(f => f.Signals.Count)} signals from Core");
        Console.Error.WriteLine();

        return Report(frames, dbc);
    }

    // ------------------------------------------------------------------ parse

    private static IEnumerable<DbcSignal> ParseDbc(string path)
    {
        var canId = -1;
        var message = "";

        foreach (var line in File.ReadLines(path))
        {
            var m = MessageLine.Match(line);
            if (m.Success)
            {
                // DBC ids are decimal and set bit 31 for extended frames; mask it off so 11-bit
                // ids compare directly against [CanFrame(0x...)].
                canId = (int)(uint.Parse(m.Groups["id"].Value) & 0x1FFF_FFFF);
                message = m.Groups["name"].Value;
                continue;
            }

            if (canId < 0)
            {
                continue;
            }

            var s = SignalLine.Match(line);
            if (!s.Success)
            {
                // A blank line ends the message block; anything else at column 0 does too.
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                {
                    canId = -1;
                }

                continue;
            }

            yield return new DbcSignal(
                message,
                canId,
                s.Groups["name"].Value,
                int.Parse(s.Groups["start"].Value),
                int.Parse(s.Groups["len"].Value),
                s.Groups["order"].Value == "0",
                s.Groups["sign"].Value == "-",
                ParseDouble(s.Groups["factor"].Value),
                ParseDouble(s.Groups["offset"].Value),
                ParseDouble(s.Groups["min"].Value),
                ParseDouble(s.Groups["max"].Value));
        }
    }

    private static double ParseDouble(string raw) =>
        double.TryParse(raw.Trim(), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v)
            ? v
            : double.NaN;

    private static List<CodeFrame> DiscoverFrames()
    {
        return typeof(IsoTpParser).Assembly
            .GetTypes()
            .Select(t => (Type: t, Frame: t.GetCustomAttribute<CanFrameAttribute>()))
            .Where(x => x.Frame is not null)
            .Select(x => new CodeFrame(
                x.Type.Name,
                x.Frame!.CanId,
                x.Type.GetProperties()
                    .Select(p => (Property: p, Attribute: p.GetCustomAttribute<CanSignalAttribute>()))
                    .Where(p => p.Attribute is { IncludeInGeneration: true })
                    .Select(p => new CodeSignal(p.Property.Name, p.Attribute!))
                    .ToList()))
            .Where(f => f.Signals.Count > 0)
            .OrderBy(f => f.CanId)
            .ToList();
    }

    // ---------------------------------------------------------------- compare

    private static int Report(List<CodeFrame> frames, List<DbcSignal> dbc)
    {
        var byteOrder = new StringBuilder();
        var layout = new StringBuilder();
        var scaling = new StringBuilder();
        var unmatched = new StringBuilder();

        var cosmetic = new StringBuilder();
        var noDbcFrame = new List<string>();

        int orderCount = 0, layoutCount = 0, scalingCount = 0, unmatchedCount = 0, cosmeticCount = 0;

        foreach (var frame in frames)
        {
            var candidates = dbc.Where(d => d.CanId == frame.CanId).ToList();
            if (candidates.Count == 0)
            {
                noDbcFrame.Add($"0x{frame.CanId:X3} {frame.TypeName}");
                continue;
            }

            foreach (var signal in frame.Signals)
            {
                // Property names are re-spelled in code (LB_SOC -> Soc), so match on geometry:
                // a DBC signal at the same start bit and length is the same signal. Byte order is
                // deliberately excluded from the match, since a wrong order is what we are hunting.
                var match = candidates.FirstOrDefault(d =>
                    d.StartBit == signal.Attribute.BitStart && d.Length == signal.Attribute.BitLength);

                if (match is null)
                {
                    unmatchedCount++;
                    unmatched.AppendLine(
                        $"  0x{frame.CanId:X3} {frame.TypeName}.{signal.Property} " +
                        $"(bit {signal.Attribute.BitStart}, len {signal.Attribute.BitLength}) " +
                        "has no DBC signal at that position");
                    continue;
                }

                var codeIsBe = signal.Attribute.ByteOrder == CanByteOrder.Motorola;
                if (codeIsBe != match.IsBigEndian)
                {
                    var line =
                        $"  0x{frame.CanId:X3} {frame.TypeName}.{signal.Property,-28} " +
                        $"code={(codeIsBe ? "Motorola" : "Intel"),-8} dbc={(match.IsBigEndian ? "Motorola" : "Intel"),-8} " +
                        $"({match.Name} {match.StartBit}|{match.Length}@{(match.IsBigEndian ? 0 : 1)})";

                    // A 1-bit signal addresses the same physical bit under either order: the
                    // Motorola index 8b+(7-i) maps back to byte b, bit i. Such a mismatch is a
                    // documentation inconsistency, not a decode difference, and lumping it in
                    // with the real ones inflates the count and buries what matters.
                    if (signal.Attribute.BitLength == 1)
                    {
                        cosmeticCount++;
                        cosmetic.AppendLine(line);
                    }
                    else
                    {
                        orderCount++;
                        byteOrder.AppendLine(line);
                    }
                }

                if (signal.Attribute.IsSigned != match.IsSigned)
                {
                    layoutCount++;
                    layout.AppendLine(
                        $"  0x{frame.CanId:X3} {frame.TypeName}.{signal.Property,-28} " +
                        $"signed: code={signal.Attribute.IsSigned} dbc={match.IsSigned}");
                }

                if (!NearlyEqual(signal.Attribute.Factor, match.Factor) ||
                    !NearlyEqual(signal.Attribute.Offset, match.Offset))
                {
                    scalingCount++;
                    scaling.AppendLine(
                        $"  0x{frame.CanId:X3} {frame.TypeName}.{signal.Property,-28} " +
                        $"code=({signal.Attribute.Factor},{signal.Attribute.Offset}) " +
                        $"dbc=({match.Factor},{match.Offset})");
                }
            }
        }

        Section("BYTE ORDER MISMATCH - reads different bits, fails silently", byteOrder, orderCount);
        Section("BYTE ORDER, LENGTH 1 - cosmetic: both orders address the same bit", cosmetic, cosmeticCount);

        Section("SIGNEDNESS MISMATCH", layout, layoutCount);
        Section("FACTOR/OFFSET MISMATCH - may be a deliberate unit choice", scaling, scalingCount);
        Section("NO DBC SIGNAL AT THAT POSITION - community discovery, or a wrong position", unmatched, unmatchedCount);

        if (noDbcFrame.Count > 0)
        {
            Console.Out.WriteLine($"FRAMES ABSENT FROM EVERY DBC ({noDbcFrame.Count}) - not checked:");
            Console.Out.WriteLine("  " + string.Join(", ", noDbcFrame));
            Console.Out.WriteLine();
        }

        Console.Out.WriteLine(
            $"SUMMARY: {orderCount} byte-order (+{cosmeticCount} cosmetic 1-bit), {layoutCount} signedness, {scalingCount} scaling, " +
            $"{unmatchedCount} unmatched, {noDbcFrame.Count} frames not in any DBC.");

        // Byte-order mismatches are the actionable category; a non-zero exit makes this usable
        // as a gate once they are all resolved.
        return orderCount > 0 ? 1 : 0;
    }

    private static void Section(string title, StringBuilder body, int count)
    {
        if (count == 0)
        {
            return;
        }

        Console.Out.WriteLine($"{title}  ({count})");
        Console.Out.Write(body.ToString());
        Console.Out.WriteLine();
    }

    private static bool NearlyEqual(double a, double b) =>
        (double.IsNaN(a) && double.IsNaN(b)) || Math.Abs(a - b) < 1e-9;

    // ------------------------------------------------------- capture evidence

    /// <summary>
    ///     Decides byte-order disputes using captured frames instead of argument.
    ///     For each disputed signal both interpretations are decoded across every payload observed
    ///     for that CAN ID, scaled, and checked against the range the DBC declares. A reading that
    ///     leaves the declared range is reporting something the ECU does not emit, which is how
    ///     0x55B SleepEnabled was settled: Intel returned 0, documented as Reserved, while Motorola
    ///     returned RefuseToSleep on a vehicle that was plainly awake.
    ///     Where both interpretations stay in range the evidence is genuinely inconclusive and the
    ///     report says so rather than guessing - several of these signals read 0 throughout a
    ///     stationary capture, and 0 is 0 under either order.
    /// </summary>
    public static int CrossReference(string[] args)
    {
        var paths = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (paths.Count < 2)
        {
            Console.Error.WriteLine("usage: ObdInsight.DevTools.exe dbc-crosscheck <dbc-dir> <captures-dir>");
            return 2;
        }

        var dbc = Directory.GetFiles(paths[0], "*.dbc", SearchOption.AllDirectories)
            .SelectMany(ParseDbc)
            .ToList();

        // Distinct payloads per CAN ID across every capture: repeats add no information.
        var observed = new Dictionary<int, HashSet<string>>();
        var logs = Directory.GetFiles(paths[1], "capture.log", SearchOption.AllDirectories);
        foreach (var log in logs)
        {
            foreach (var line in File.ReadLines(log))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4 || parts[1] != "F")
                {
                    continue;
                }

                if (!int.TryParse(parts[2], NumberStyles.HexNumber, null, out var id))
                {
                    continue;
                }

                if (!observed.TryGetValue(id, out var set))
                {
                    observed[id] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.Add(parts[3]);
            }
        }

        Console.Error.WriteLine(
            $"{logs.Length} captures, {observed.Count} CAN IDs, " +
            $"{observed.Sum(kv => kv.Value.Count)} distinct payloads");
        Console.Error.WriteLine();

        var frames = DiscoverFrames();
        int decided = 0, inconclusive = 0, nodata = 0;

        foreach (var frame in frames)
        {
            var candidates = dbc.Where(d => d.CanId == frame.CanId).ToList();

            foreach (var signal in frame.Signals)
            {
                var match = candidates.FirstOrDefault(d =>
                    d.StartBit == signal.Attribute.BitStart && d.Length == signal.Attribute.BitLength);

                if (match is null || signal.Attribute.BitLength == 1)
                {
                    continue;
                }

                var codeIsBe = signal.Attribute.ByteOrder == CanByteOrder.Motorola;
                if (codeIsBe == match.IsBigEndian)
                {
                    continue;
                }

                if (!observed.TryGetValue(frame.CanId, out var payloads) || payloads.Count == 0)
                {
                    nodata++;
                    continue;
                }

                var (intelOk, intelLo, intelHi) = Evaluate(payloads, match, false);
                var (motoOk, motoLo, motoHi) = Evaluate(payloads, match, true);

                string verdict;
                if (intelOk && !motoOk)
                {
                    verdict = "KEEP INTEL   (Motorola leaves the DBC range)";
                    decided++;
                }
                else if (motoOk && !intelOk)
                {
                    verdict = "USE MOTOROLA (Intel leaves the DBC range)";
                    decided++;
                }
                else if (intelLo == intelHi && motoLo == motoHi && intelLo == motoLo)
                {
                    verdict = "INCONCLUSIVE (both constant and equal - captures do not discriminate)";
                    inconclusive++;
                }
                else
                {
                    verdict = "INCONCLUSIVE (both within range)";
                    inconclusive++;
                }

                Console.Out.WriteLine(
                    $"0x{frame.CanId:X3} {frame.TypeName}.{signal.Property}");
                Console.Out.WriteLine(
                    $"    dbc {match.Name} {match.StartBit}|{match.Length}@{(match.IsBigEndian ? 0 : 1)} " +
                    $"range [{match.Min}..{match.Max}]  over {payloads.Count} payloads");
                Console.Out.WriteLine(
                    $"    Intel    {intelLo,12:G6} .. {intelHi,-12:G6} {(intelOk ? "in range" : "OUT OF RANGE")}");
                Console.Out.WriteLine(
                    $"    Motorola {motoLo,12:G6} .. {motoHi,-12:G6} {(motoOk ? "in range" : "OUT OF RANGE")}");
                Console.Out.WriteLine($"    => {verdict}");
                Console.Out.WriteLine();
            }
        }

        Console.Out.WriteLine(
            $"SUMMARY: {decided} decided by captured data, {inconclusive} inconclusive, " +
            $"{nodata} with no captured frames for that ID.");
        return 0;
    }

    /// <summary>
    ///     Decodes every declared signal across every captured payload and reports the ones whose
    ///     own definition the data contradicts.
    /// </summary>
    /// <remarks>
    ///     The oracle here is the code itself: a signal that leaves the MinValue/MaxValue its own
    ///     attribute declares is either mis-positioned or mis-scaled, and no external reference is
    ///     needed to say so. Two weaker signals are reported alongside it, since both have already
    ///     produced real findings in this project:
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 Counter-like fields, which advance by a constant step every frame. 0x284's
    ///                 "vehicle speed" was one, decoding 61-496 km/h on a stationary car.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 Signals constant across every frame ever captured, which may be reading
    ///                 reserved padding rather than the field they are named for - though on a parked
    ///                 car many legitimately do not vary, so this is a hint and not a verdict.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    public static int SignalSanity(string[] args)
    {
        var paths = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (paths.Count < 1)
        {
            Console.Error.WriteLine("usage: ObdInsight.DevTools.exe signal-sanity <captures-dir>");
            return 2;
        }

        // Payloads in arrival order per ID: the counter check needs successive frames.
        var observed = new Dictionary<int, List<byte[]>>();
        var logs = Directory.GetFiles(paths[0], "capture.log", SearchOption.AllDirectories);
        foreach (var log in logs)
        {
            foreach (var line in File.ReadLines(log))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4 || parts[1] != "F" ||
                    !int.TryParse(parts[2], NumberStyles.HexNumber, null, out var id))
                {
                    continue;
                }

                if (!observed.TryGetValue(id, out var list))
                {
                    observed[id] = list = [];
                }

                var bytes = ParseHex(parts[3]);
                if (bytes.Length < 8)
                {
                    Array.Resize(ref bytes, 8);
                }

                list.Add(bytes);
            }
        }

        Console.Error.WriteLine(
            $"{logs.Length} captures, {observed.Count} CAN IDs, {observed.Sum(kv => kv.Value.Count)} frames");
        Console.Error.WriteLine();

        var outOfRange = new StringBuilder();
        var counters = new StringBuilder();
        var constants = new StringBuilder();
        int rangeCount = 0, counterCount = 0, constantCount = 0, checkedCount = 0, noData = 0;

        foreach (var frame in DiscoverFrames())
        {
            if (!observed.TryGetValue(frame.CanId, out var payloads) || payloads.Count < 2)
            {
                noData++;
                continue;
            }

            var multiplexor = frame.Signals.FirstOrDefault(s => s.Attribute.IsMultiplexor);

            foreach (var signal in frame.Signals)
            {
                var a = signal.Attribute;
                var values = new List<double>(payloads.Count);

                foreach (var payload in payloads)
                {
                    // A multiplexed signal is only meaningful in frames selecting its variant.
                    if (a.MuxValue != CanSignalAttribute.NotMultiplexed && multiplexor is not null)
                    {
                        var selector = Read(payload, multiplexor.Attribute);
                        if ((int)selector != a.MuxValue)
                        {
                            continue;
                        }
                    }

                    values.Add(Read(payload, a) * a.Factor + a.Offset);
                }

                if (values.Count < 2)
                {
                    continue;
                }

                checkedCount++;
                var lo = values.Min();
                var hi = values.Max();

                var bounded = !double.IsNaN(a.MinValue) && !double.IsNaN(a.MaxValue) && a.MaxValue > a.MinValue;
                if (bounded && (lo < a.MinValue || hi > a.MaxValue))
                {
                    rangeCount++;
                    outOfRange.AppendLine(
                        $"  0x{frame.CanId:X3} {frame.TypeName}.{signal.Property,-32} " +
                        $"observed {lo:G6}..{hi:G6}  declared [{a.MinValue:G6}..{a.MaxValue:G6}]  ({values.Count} frames)");
                }

                if (LooksLikeCounter(values))
                {
                    counterCount++;
                    counters.AppendLine(
                        $"  0x{frame.CanId:X3} {frame.TypeName}.{signal.Property,-32} " +
                        $"advances by a constant step over {values.Count} frames ({lo:G6}..{hi:G6})");
                }
                else if (Math.Abs(hi - lo) < 1e-9)
                {
                    constantCount++;
                    constants.AppendLine(
                        $"  0x{frame.CanId:X3} {frame.TypeName}.{signal.Property,-32} " +
                        $"constant {lo:G6} over {values.Count} frames");
                }
            }
        }

        Section("OUT OF ITS OWN DECLARED RANGE - mis-positioned or mis-scaled", outOfRange, rangeCount);
        Section("COUNTER-LIKE - advances by a constant step, so probably not the named signal", counters, counterCount);
        Section("CONSTANT across every captured frame - may be reading reserved bits", constants, constantCount);

        Console.Out.WriteLine(
            $"SUMMARY: {checkedCount} signals checked against captured data, {rangeCount} out of range, " +
            $"{counterCount} counter-like, {constantCount} constant, {noData} frames with no captures.");

        return rangeCount > 0 ? 1 : 0;
    }

    private static double Read(byte[] payload, CanSignalAttribute a)
    {
        var raw = a.ByteOrder == CanByteOrder.Motorola
            ? CanBits.ReadUnsignedBe(payload, a.BitStart, a.BitLength)
            : CanBits.ReadUnsigned(payload, a.BitStart, a.BitLength);

        return a.IsSigned ? SignExtend(raw, a.BitLength) : raw;
    }

    /// <summary>
    ///     True when successive values advance by one repeated non-zero step, allowing for
    ///     wraparound. Requires the step to dominate rather than merely appear, so a signal that
    ///     happens to change smoothly for a while is not mistaken for a counter.
    /// </summary>
    private static bool LooksLikeCounter(List<double> values)
    {
        if (values.Count < 8)
        {
            return false;
        }

        var steps = new Dictionary<double, int>();
        for (var i = 1; i < values.Count; i++)
        {
            var delta = values[i] - values[i - 1];
            if (delta <= 0)
            {
                continue; // ignore wraparound and idle repeats
            }

            steps[delta] = steps.GetValueOrDefault(delta) + 1;
        }

        if (steps.Count == 0)
        {
            return false;
        }

        var dominant = steps.OrderByDescending(kv => kv.Value).First();
        return dominant.Value >= (values.Count - 1) * 0.8;
    }

    private static (bool InRange, double Lo, double Hi) Evaluate(
        HashSet<string> payloads, DbcSignal dbc, bool bigEndian)
    {
        var lo = double.MaxValue;
        var hi = double.MinValue;

        foreach (var hex in payloads)
        {
            var bytes = ParseHex(hex);
            if (bytes.Length < 8)
            {
                // Zero-extend, matching the generated reader.
                Array.Resize(ref bytes, 8);
            }

            uint raw;
            try
            {
                raw = bigEndian
                    ? CanBits.ReadUnsignedBe(bytes, dbc.StartBit, dbc.Length)
                    : CanBits.ReadUnsigned(bytes, dbc.StartBit, dbc.Length);
            }
            catch (ArgumentOutOfRangeException)
            {
                // A Motorola signal that runs past the payload cannot be the right reading.
                return (false, double.NaN, double.NaN);
            }

            var value = dbc.IsSigned
                ? SignExtend(raw, dbc.Length) * dbc.Factor + dbc.Offset
                : raw * dbc.Factor + dbc.Offset;

            lo = Math.Min(lo, value);
            hi = Math.Max(hi, value);
        }

        // A DBC range of [0|0] means "unspecified", not "must be zero".
        var bounded = !double.IsNaN(dbc.Min) && !double.IsNaN(dbc.Max) && dbc.Max > dbc.Min;
        var inRange = !bounded || (lo >= dbc.Min && hi <= dbc.Max);

        return (inRange, lo, hi);
    }

    private static int SignExtend(uint value, int bitLength)
    {
        var signBit = 1u << (bitLength - 1);
        return (value & signBit) == 0
            ? (int)value
            : (int)(value | (bitLength == 32 ? 0u : ~((1u << bitLength) - 1)));
    }

    private static byte[] ParseHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    private sealed record DbcSignal(
        string Message,
        int CanId,
        string Name,
        int StartBit,
        int Length,
        bool IsBigEndian,
        bool IsSigned,
        double Factor,
        double Offset,
        double Min,
        double Max);

    // ------------------------------------------------------------- reflection

    private sealed record CodeSignal(string Property, CanSignalAttribute Attribute);

    private sealed record CodeFrame(string TypeName, int CanId, List<CodeSignal> Signals);
}
