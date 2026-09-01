using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ObdInsight.SourceGeneration.Attributes;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Compares the <c>[CanSignal]</c> definitions compiled into Core against the DBC files they were
/// transcribed from, and reports every disagreement.
///
/// Read-only by design: it changes nothing and asserts nothing, because most differences need a
/// human decision. A signal absent from the DBC may be a community discovery rather than an
/// error, and a differing factor may be a deliberate unit choice.
///
/// The one category that is almost always a genuine defect is **byte order**. A signal declared
/// Intel where the DBC says <c>@0</c> reads a different set of bits entirely, and it fails
/// silently - the value is plausible, just wrong. Checking a single frame this way (0x55B) found
/// two such signals, which is what prompted sweeping all of them.
/// </summary>
public static class DbcAudit
{
    private sealed record DbcSignal(
        string Message,
        int CanId,
        string Name,
        int StartBit,
        int Length,
        bool IsBigEndian,
        bool IsSigned,
        double Factor,
        double Offset);

    /// <summary>
    /// <c>SG_ Name : start|len@order sign (factor,offset) [min|max] "unit" receivers</c>
    /// Order is 0 for Motorola/big-endian and 1 for Intel; sign is + for unsigned, - for signed.
    /// </summary>
    private static readonly Regex SignalLine = new(
        @"^\s*SG_\s+(?<name>[A-Za-z0-9_]+)\s*:\s*(?<start>\d+)\|(?<len>\d+)@(?<order>[01])(?<sign>[+-])\s*\((?<factor>[^,]+),(?<offset>[^)]+)\)",
        RegexOptions.Compiled);

    /// <summary><c>BO_ &lt;decimal id&gt; &lt;name&gt;: &lt;dlc&gt; &lt;transmitter&gt;</c></summary>
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
        Console.Error.WriteLine($"reflected {frames.Count} frames with {frames.Sum(f => f.Signals.Count)} signals from Core");
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
                ParseDouble(s.Groups["offset"].Value));
        }
    }

    private static double ParseDouble(string raw) =>
        double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : double.NaN;

    // ------------------------------------------------------------- reflection

    private sealed record CodeSignal(string Property, CanSignalAttribute Attribute);

    private sealed record CodeFrame(string TypeName, int CanId, List<CodeSignal> Signals);

    private static List<CodeFrame> DiscoverFrames()
    {
        return typeof(ObdInsight.Core.Protocols.IsoTpParser).Assembly
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
}
