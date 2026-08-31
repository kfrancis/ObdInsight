using System.Text;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Offline correlator for guided-probe captures: given a recorded session, finds the bits that
/// track each stimulus.
///
/// Deliberately a pure function over a capture directory. Car time is the scarce resource, so
/// the recording happens once and the scoring can be argued with at a desk for as long as it
/// takes - and re-run against the same bytes when the scoring changes.
///
/// Implements the protocol in .local/CAN_TOOLING_PLAN.md section 7.3:
///
///   1. NOISE MASK from the idle baseline. Every bit that moves with no stimulus is a counter,
///      CRC or drifting sensor. Masked out everywhere else. Skipping this is why a naive
///      before/after diff drowns in false positives - 0x284's free-running counter alone would
///      "respond" to every probe ever run.
///   2. WITHIN-WINDOW CONSTANCY. A responding bit holds one value for the whole hold window.
///   3. BETWEEN-STATE SEPARATION. Its value in the on-windows differs from the off-windows.
///   4. ALTERNATION CONSISTENCY. It must do that for EVERY repetition. A bit that flips once by
///      chance has a 1-in-2^(N-1) shot at tracking the full pattern; this is the check that
///      separates signal from coincidence, and it is why the scripts repeat three times.
/// </summary>
public static class ProbeAnalyzer
{
    /// <summary>Discarded after a stimulus is confirmed, matching the runner's settle delay.</summary>
    private static readonly double SettleMs = 1500;

    /// <summary>Length of the measured window after settle, matching the runner's hold.</summary>
    private static readonly double HoldMs = 3000;

    public sealed record Frame(double AtMs, string Id, byte[] Payload);

    public sealed record Marker(double AtMs, string Label);

    public sealed record Session(string Name, IReadOnlyList<Frame> Frames, IReadOnlyList<Marker> Markers);

    /// <summary>A bit that tracked a stimulus.</summary>
    public sealed record Finding(
        string Probe,
        string Id,
        int BitIndex,
        int OnValue,
        int Windows,
        bool Confounded,
        string ConfoundedBy);

    // ------------------------------------------------------------------ parse

    public static Session Load(string captureDir)
    {
        var log = Path.Combine(captureDir, "capture.log");
        if (!File.Exists(log))
        {
            throw new FileNotFoundException($"No capture.log in {captureDir}");
        }

        var frames = new List<Frame>();
        var markers = new List<Marker>();

        foreach (var raw in File.ReadLines(log))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            // <elapsed_ms> <F|E|M> <id-or-kind> <payload-or-text>
            var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || !double.TryParse(parts[0], out var at))
            {
                continue;
            }

            switch (parts[1])
            {
                case "F":
                    frames.Add(new Frame(at, parts[2], ParseHex(parts[3].Trim())));
                    break;
                case "M":
                    markers.Add(new Marker(at, parts[3].Trim()));
                    break;
            }
        }

        return new Session(Path.GetFileName(captureDir), frames, markers);
    }

    // ---------------------------------------------------------------- analyse

    public static IReadOnlyList<Finding> Analyze(Session session, out string report)
    {
        var sb = new StringBuilder();
        var ids = session.Frames.Select(f => f.Id).Distinct().OrderBy(i => i, StringComparer.Ordinal).ToList();

        // --- 1. noise mask from the idle baseline -----------------------------
        var baselineStart = session.Markers.FirstOrDefault(m => m.Label.EndsWith("-start", StringComparison.Ordinal)
                                                                && m.Label.StartsWith("idle-baseline", StringComparison.Ordinal));
        var baselineEnd = session.Markers.FirstOrDefault(m => m.Label.EndsWith("-end", StringComparison.Ordinal)
                                                              && m.Label.StartsWith("idle-baseline", StringComparison.Ordinal));

        var noise = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (baselineStart is not null && baselineEnd is not null)
        {
            foreach (var id in ids)
            {
                noise[id] = ChangedMask(FramesIn(session, id, baselineStart.AtMs, baselineEnd.AtMs));
            }

            var noisy = noise.Count(kv => kv.Value.Any(b => b != 0));
            sb.AppendLine($"Noise mask from idle baseline ({baselineEnd.AtMs - baselineStart.AtMs:F0} ms): " +
                          $"{noisy}/{ids.Count} IDs have self-moving bits (counters, CRCs, drift).");
        }
        else
        {
            sb.AppendLine("WARNING: no idle baseline found - every counter and CRC will look like a response.");
            foreach (var id in ids)
            {
                noise[id] = new byte[8];
            }
        }

        // --- 2. group markers into probes -------------------------------------
        // Labels are "<probe>-on" / "<probe>-off"; idle markers are excluded.
        var probes = session.Markers
            .Where(m => m.Label.EndsWith("-on", StringComparison.Ordinal) || m.Label.EndsWith("-off", StringComparison.Ordinal))
            .Where(m => !m.Label.StartsWith("idle-", StringComparison.Ordinal))
            .GroupBy(m => m.Label[..m.Label.LastIndexOf('-')])
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        sb.AppendLine($"{probes.Count} probes, {session.Frames.Count} frames, {ids.Count} CAN IDs.");
        sb.AppendLine();

        var findings = new List<Finding>();

        foreach (var (probe, marks) in probes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var onWindows = marks.Where(m => m.Label.EndsWith("-on", StringComparison.Ordinal)).ToList();
            var offWindows = marks.Where(m => m.Label.EndsWith("-off", StringComparison.Ordinal)).ToList();

            if (onWindows.Count == 0 || offWindows.Count == 0)
            {
                continue;
            }

            foreach (var id in ids)
            {
                var mask = noise.TryGetValue(id, out var m2) ? m2 : new byte[8];

                // Modal payload per window; null when the ID produced no frames in that window.
                var onModes = onWindows.Select(w => Modal(session, id, w.AtMs)).ToList();
                var offModes = offWindows.Select(w => Modal(session, id, w.AtMs)).ToList();

                if (onModes.Any(x => x is null) || offModes.Any(x => x is null))
                {
                    continue;   // incomplete coverage - cannot claim consistency
                }

                var width = onModes.Concat(offModes).Min(x => x!.Length) * 8;

                for (var bit = 0; bit < width; bit++)
                {
                    if (IsMasked(mask, bit))
                    {
                        continue;
                    }

                    var onBits = onModes.Select(x => GetBit(x!, bit)).ToList();
                    var offBits = offModes.Select(x => GetBit(x!, bit)).ToList();

                    // Alternation consistency: identical within each state, different between.
                    if (onBits.Distinct().Count() != 1 || offBits.Distinct().Count() != 1)
                    {
                        continue;
                    }

                    if (onBits[0] == offBits[0])
                    {
                        continue;
                    }

                    findings.Add(new Finding(probe, id, bit, onBits[0], onWindows.Count + offWindows.Count, false, ""));
                }
            }
        }

        // --- 3. confounder marking --------------------------------------------
        // A bit that answers to more than one stimulus is not specific to either. left-signal vs
        // right-signal is the designed case: a shared hit is an AnySignalActive-style bit.
        var byBit = findings.GroupBy(f => (f.Id, f.BitIndex));
        var marked = new List<Finding>();
        foreach (var group in byBit)
        {
            var probesHit = group.Select(f => f.Probe).Distinct().ToList();
            foreach (var f in group)
            {
                var others = probesHit.Where(p => p != f.Probe).ToList();
                marked.Add(f with { Confounded = others.Count > 0, ConfoundedBy = string.Join(",", others) });
            }
        }

        report = sb.ToString();
        return marked;
    }

    public static string Format(Session session, IReadOnlyList<Finding> findings, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {session.Name} ===");
        sb.Append(header);

        // Iterate the probes the SESSION contains, not just those that produced hits. A probe
        // that found nothing is a result - it means the stimulus is not visible on this bus, or
        // the protocol did not suit it - and silently omitting it reads as "not run".
        var allProbes = session.Markers
            .Where(m => m.Label.EndsWith("-on", StringComparison.Ordinal) || m.Label.EndsWith("-off", StringComparison.Ordinal))
            .Where(m => !m.Label.StartsWith("idle-", StringComparison.Ordinal))
            .Select(m => m.Label[..m.Label.LastIndexOf('-')])
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var probe in allProbes)
        {
            var hits = findings.Where(f => f.Probe == probe).OrderBy(f => f.Id, StringComparer.Ordinal).ThenBy(f => f.BitIndex).ToList();
            var clean = hits.Where(h => !h.Confounded).ToList();

            sb.AppendLine($"--- {probe} ({hits.Count} bit(s), {clean.Count} specific) ---");

            foreach (var h in hits)
            {
                var byteIndex = h.BitIndex / 8;
                var bitInByte = h.BitIndex % 8;
                var tag = h.Confounded ? $"  [also responds to {h.ConfoundedBy}]" : "  [specific]";
                sb.AppendLine($"    0x{h.Id}  bit {h.BitIndex,2} (byte {byteIndex}, bit {bitInByte})  on={h.OnValue}  {h.Windows} windows{tag}");
            }

            if (hits.Count == 0)
            {
                sb.AppendLine("    (nothing tracked this stimulus)");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ utils

    private static IEnumerable<Frame> FramesIn(Session s, string id, double from, double to) =>
        s.Frames.Where(f => f.Id == id && f.AtMs >= from && f.AtMs <= to);

    /// <summary>
    /// Most common payload in the hold window following a marker. Modal rather than last, so a
    /// single mid-window glitch frame does not decide the result.
    /// </summary>
    private static byte[]? Modal(Session s, string id, double markerAt)
    {
        var window = FramesIn(s, id, markerAt + SettleMs, markerAt + SettleMs + HoldMs).ToList();
        if (window.Count == 0)
        {
            return null;
        }

        return window
            .GroupBy(f => Convert.ToHexString(f.Payload), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .First()
            .First().Payload;
    }

    private static byte[] ChangedMask(IEnumerable<Frame> frames)
    {
        var mask = new byte[8];
        byte[]? first = null;

        foreach (var f in frames)
        {
            first ??= f.Payload;
            var n = Math.Min(f.Payload.Length, Math.Min(first.Length, mask.Length));
            for (var i = 0; i < n; i++)
            {
                mask[i] |= (byte)(f.Payload[i] ^ first[i]);
            }
        }

        return mask;
    }

    private static bool IsMasked(byte[] mask, int bit)
    {
        var byteIndex = bit / 8;
        return byteIndex < mask.Length && (mask[byteIndex] & (1 << (bit % 8))) != 0;
    }

    private static int GetBit(byte[] payload, int bit)
    {
        var byteIndex = bit / 8;
        return byteIndex >= payload.Length ? 0 : (payload[byteIndex] >> (bit % 8)) & 1;
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
}
