using System.Text;

namespace ObdInsight.DevTools.Commands;

/// <summary>
///     Offline correlator for guided-probe captures: given a recorded session, finds the bits that
///     track each stimulus.
///     Deliberately a pure function over a capture directory. Car time is the scarce resource, so
///     the recording happens once and the scoring can be argued with at a desk for as long as it
///     takes - and re-run against the same bytes when the scoring changes.
///     Implements the protocol in .local/CAN_TOOLING_PLAN.md section 7.3:
///     1. NOISE MASK from the idle baseline. Every bit that moves with no stimulus is a counter,
///     CRC or drifting sensor. Masked out everywhere else. Skipping this is why a naive
///     before/after diff drowns in false positives - 0x284's free-running counter alone would
///     "respond" to every probe ever run.
///     2. WITHIN-WINDOW CONSTANCY. A responding bit holds one value for the whole hold window.
///     3. BETWEEN-STATE SEPARATION. Its value in the on-windows differs from the off-windows.
///     4. ALTERNATION CONSISTENCY. It must do that for EVERY repetition. A bit that flips once by
///     chance has a 1-in-2^(N-1) shot at tracking the full pattern; this is the check that
///     separates signal from coincidence, and it is why the scripts repeat three times.
/// </summary>
public static class ProbeAnalyzer
{
    /// <summary>How a bit responded to the stimulus.</summary>
    public enum ResponseKind
    {
        /// <summary>Held one value while the stimulus was applied, a different one while it was not.</summary>
        Static,

        /// <summary>
        ///     Oscillated while the stimulus was applied and was still while it was not. Indicators
        ///     and hazards behave this way, and a modal-value comparison cannot see them: the bit is
        ///     not constant within the window, so the constancy test rejects it. That is exactly why
        ///     the `hazards` probe returned nothing on 2026-08-31.
        /// </summary>
        Blink
    }

    /// <summary>Discarded after a stimulus is confirmed, matching the runner's settle delay.</summary>
    private static readonly double SettleMs = 1500;

    /// <summary>Length of the measured window after settle, matching the runner's hold.</summary>
    private static readonly double HoldMs = 3000;

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
                                                                && m.Label.StartsWith("idle-baseline",
                                                                    StringComparison.Ordinal));
        var baselineEnd = session.Markers.FirstOrDefault(m => m.Label.EndsWith("-end", StringComparison.Ordinal)
                                                              && m.Label.StartsWith("idle-baseline",
                                                                  StringComparison.Ordinal));

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
            .Where(m => m.Label.EndsWith("-on", StringComparison.Ordinal) ||
                        m.Label.EndsWith("-off", StringComparison.Ordinal))
            .Where(m => !m.Label.StartsWith("idle-", StringComparison.Ordinal))
            .GroupBy(m => m.Label[..m.Label.LastIndexOf('-')])
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        sb.AppendLine($"{probes.Count} probes, {session.Frames.Count} frames, {ids.Count} CAN IDs.");
        sb.AppendLine();

        var findings = new List<Finding>();


        var coverage = new Dictionary<string, (int Scored, int Skipped, int Partial)>(StringComparer.Ordinal);

        foreach (var (probe, marks) in probes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var unanalysable = new HashSet<string>(StringComparer.Ordinal);

            var partial = new HashSet<string>(StringComparer.Ordinal);


            var onWindows = marks.Where(m => m.Label.EndsWith("-on", StringComparison.Ordinal)).ToList();
            var offWindows = marks.Where(m => m.Label.EndsWith("-off", StringComparison.Ordinal)).ToList();

            if (onWindows.Count == 0 || offWindows.Count == 0)
            {
                continue;
            }

            foreach (var id in ids)
            {
                var mask = noise.TryGetValue(id, out var m2) ? m2 : new byte[8];

                // Per-bit behaviour per window; null when the ID produced no frames in a window.
                var onStats = onWindows.Select(w => WindowBits(session, id, w.AtMs)).ToList();
                var offStats = offWindows.Select(w => WindowBits(session, id, w.AtMs)).ToList();
                var onSamples = onWindows.Select(w =>
                    (double)FramesIn(session, id, w.AtMs + SettleMs, w.AtMs + SettleMs + HoldMs).Count()).ToList();
                var offSamples = offWindows.Select(w =>
                    (double)FramesIn(session, id, w.AtMs + SettleMs, w.AtMs + SettleMs + HoldMs).Count()).ToList();

                // Drop only the windows that have no frames, not the whole ID.
                //
                // Requiring every window to be populated discarded 16 of 56 IDs during the
                // parking-brake probe on 2026-08-31 - 29% of the bus, silently. On an adapter
                // losing frames to BUFFER FULL that is the normal case for slow IDs, and it
                // turns "not found" into something indistinguishable from "not looked at".
                // Two windows per state still gives an alternation to check; fewer does not.
                var onPresent = onStats.Where(x => x is not null).ToList();
                var offPresent = offStats.Where(x => x is not null).ToList();

                if (onPresent.Count < 2 || offPresent.Count < 2)
                {
                    unanalysable.Add(id);
                    continue;
                }

                if (onPresent.Count < onStats.Count || offPresent.Count < offStats.Count)
                {
                    partial.Add(id);
                }

                onStats = onPresent;
                offStats = offPresent;
                onSamples = onSamples.Where(s => s > 0).ToList();
                offSamples = offSamples.Where(s => s > 0).ToList();

                var width = onStats.Concat(offStats).Min(x => x!.Length);
                var windows = onStats.Count + offStats.Count;

                for (var bit = 0; bit < width; bit++)
                {
                    if (IsMasked(mask, bit))
                    {
                        continue;
                    }

                    var on = onStats.Select(x => x![bit]).ToList();
                    var off = offStats.Select(x => x![bit]).ToList();

                    // --- static response: constant in every window, differing between states ---
                    if (on.All(s => s.Constant) && off.All(s => s.Constant)
                                                && on.Select(s => s.Modal).Distinct().Count() == 1
                                                && off.Select(s => s.Modal).Distinct().Count() == 1
                                                && on[0].Modal != off[0].Modal)
                    {
                        findings.Add(new Finding(probe, id, bit, on[0].Modal, windows, false, ""));
                        continue;
                    }

                    // --- blink response: oscillating in one state, still in the other ---
                    //
                    // Scored on MEANS rather than per-window thresholds. Measured on 2026-08-31,
                    // 0x60D arrived at only 3-5 frames per 3 s window - roughly 1 Hz, against a
                    // native ~10 Hz, the rest lost to adapter BUFFER FULL. Sampling a ~1.5 Hz
                    // indicator at ~1 Hz is below Nyquist, so the transition count aliases badly:
                    // the same physical hazard flash produced 3,3,4 in one run and 2,1,2 in the
                    // next. A unanimity test over per-window thresholds turns that into a
                    // reproducibility failure; comparing means against a near-silent other state
                    // survives it.
                    const double MinMeanOn = 1.5;
                    const double MaxMeanOff = 0.5;

                    var meanOn = on.Average(s => s.Transitions);
                    var meanOff = off.Average(s => s.Transitions);

                    var onBlinks = meanOn >= MinMeanOn && meanOff <= MaxMeanOff;
                    var offBlinks = meanOff >= MinMeanOn && meanOn <= MaxMeanOff;

                    if (onBlinks || offBlinks)
                    {
                        var active = onBlinks ? on : off;
                        findings.Add(new Finding(probe, id, bit, onBlinks ? 1 : 0, windows, false, "")
                        {
                            Kind = ResponseKind.Blink,
                            BlinkRate = active.Average(s => s.Transitions),
                            SamplesPerWindow = onSamples.Concat(offSamples).Average()
                        });
                    }
                }
            }

            // Coverage is reported, never silent. A probe that examined 39 of 56 IDs has not
            // shown that a signal is absent - it has shown that 17 IDs were never looked at.
            if (unanalysable.Count > 0 || partial.Count > 0)
            {
                coverage[probe] = (ids.Count - unanalysable.Count, unanalysable.Count, partial.Count);
            }
        }

        if (coverage.Count > 0)
        {
            sb.AppendLine("COVERAGE (frame loss means not every ID could be scored):");
            foreach (var (probe, c) in coverage.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {probe,-18} scored {c.Scored}/{ids.Count} IDs" +
                              (c.Skipped > 0 ? $", {c.Skipped} SKIPPED (too few populated windows)" : "") +
                              (c.Partial > 0 ? $", {c.Partial} scored on partial windows" : ""));
            }

            sb.AppendLine("  A probe with skipped IDs cannot support a claim that a signal is absent.");
            sb.AppendLine();
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

        AppendProbeCollisionWarnings(sb, marked);

        report = sb.ToString();
        return marked;
    }

    /// <summary>
    ///     Flags probe pairs that look like the same physical action rather than two different ones.
    ///     Two signatures matter, and both are operator errors rather than vehicle behaviour:
    ///     INVERTED - the probes share bits and every shared bit has the opposite on-value. That
    ///     means one probe was performed on the wrong phase: pressed where the script said
    ///     release. Observed for real on 2026-08-31, where `parking-brake` was actually the
    ///     brake pedal worked in reverse, and the shared bits were wrongly written off as a
    ///     confound when they were in fact the same finding confirmed twice.
    ///     DUPLICATE - the probes share bits and every shared bit has the SAME on-value, i.e. the
    ///     same action was performed for both.
    ///     Either way the pair cannot be treated as independent evidence, and the run should be
    ///     repeated with the control identified explicitly.
    /// </summary>
    private static void AppendProbeCollisionWarnings(StringBuilder sb, IReadOnlyList<Finding> findings)
    {
        const int MinShared = 3;

        var probes = findings.Select(f => f.Probe).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
        var warnings = new List<string>();

        for (var i = 0; i < probes.Count; i++)
        {
            for (var j = i + 1; j < probes.Count; j++)
            {
                // Static findings only. A blink finding's OnValue records WHICH state oscillated,
                // not a level, so comparing it against a static level is meaningless - and one
                // such bit mixed into the set is enough to defeat a unanimity test.
                var a = findings.Where(f => f.Probe == probes[i] && f.Kind == ResponseKind.Static)
                    .ToDictionary(f => (f.Id, f.BitIndex), f => f.OnValue);
                var b = findings.Where(f => f.Probe == probes[j] && f.Kind == ResponseKind.Static)
                    .ToDictionary(f => (f.Id, f.BitIndex), f => f.OnValue);

                var shared = a.Keys.Intersect(b.Keys).ToList();
                if (shared.Count < MinShared)
                {
                    continue;
                }

                // A strong majority rather than unanimity: on real data one stray bit should not
                // suppress a warning about eight that agree.
                var opposite = shared.Count(k => a[k] != b[k]);
                // Dominance is measured against each probe's TOTAL findings, blink included -
                // only the polarity test needs static bits. `gear-b` and `gear-drive` share all
                // three of their static bits, but B carries five further blink bits that D does
                // not (B is D plus regen). Judging dominance on static bits alone called that a
                // duplicate; judging it on everything found correctly calls it a nested state.
                var totalA = findings.Count(f => f.Probe == probes[i]);
                var totalB = findings.Count(f => f.Probe == probes[j]);

                // Reported with its numbers rather than judged by a threshold. Tuning a cutoff
                // against the handful of pairs seen so far kept flipping which cases it caught:
                // a bar strict enough to reject `ac`/`fan-max` (3 shared of 26 and 14, an
                // ordinary HVAC-byte overlap) also rejected `brake`/`parking-brake`, which was a
                // genuine phase inversion. How much of each probe the overlap accounts for is the
                // information a reader needs; the verdict is theirs.
                var shareOfA = shared.Count / (double)Math.Max(totalA, 1);
                var shareOfB = shared.Count / (double)Math.Max(totalB, 1);

                var mostlyOpposite = opposite >= shared.Count * 0.8;
                var mostlySame = shared.Count - opposite >= shared.Count * 0.8;

                if (mostlyOpposite || mostlySame)
                {
                    var pattern = mostlyOpposite ? "INVERTED" : "SAME-PHASE";
                    warnings.Add(
                        $"  {pattern,-10} '{probes[i]}' vs '{probes[j]}': {shared.Count} shared static bits " +
                        $"({opposite} inverted), = {shareOfA:P0} of '{probes[i]}' and {shareOfB:P0} of '{probes[j]}'.");
                }
            }
        }

        if (warnings.Count > 0)
        {
            sb.AppendLine("PROBE OVERLAPS (two probes moving the same bits):");
            foreach (var w in warnings.OrderByDescending(w => w))
            {
                sb.AppendLine(w);
            }

            sb.AppendLine(
                "  A high percentage of BOTH probes suggests the same action was performed twice - INVERTED " +
                "meaning on opposite phases. A low percentage is ordinary: related controls share status bytes.");
            sb.AppendLine();
        }
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
            .Where(m => m.Label.EndsWith("-on", StringComparison.Ordinal) ||
                        m.Label.EndsWith("-off", StringComparison.Ordinal))
            .Where(m => !m.Label.StartsWith("idle-", StringComparison.Ordinal))
            .Select(m => m.Label[..m.Label.LastIndexOf('-')])
            .Distinct()
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var probe in allProbes)
        {
            var hits = findings.Where(f => f.Probe == probe).OrderBy(f => f.Id, StringComparer.Ordinal)
                .ThenBy(f => f.BitIndex).ToList();
            var clean = hits.Where(h => !h.Confounded).ToList();

            sb.AppendLine($"--- {probe} ({hits.Count} bit(s), {clean.Count} specific) ---");

            foreach (var h in hits)
            {
                var byteIndex = h.BitIndex / 8;
                var bitInByte = h.BitIndex % 8;
                var tag = h.Confounded ? $"  [also responds to {h.ConfoundedBy}]" : "  [specific]";
                var kind = h.Kind == ResponseKind.Blink
                    ? $"BLINKS ({h.BlinkRate:F1} transitions/window" + (h.Undersampled
                        ? $", UNDERSAMPLED at {h.SamplesPerWindow:F1} frames/window - rate is a lower bound"
                        : "") + ")"
                    : $"on={h.OnValue}";
                sb.AppendLine(
                    $"    0x{h.Id}  bit {h.BitIndex,2} (byte {byteIndex}, bit {bitInByte})  {kind}  {h.Windows} windows{tag}");
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
    ///     Per-bit behaviour across the hold window following a marker: the modal value (majority,
    ///     so one glitch frame cannot decide the result), how many times the bit changed, and
    ///     whether it held still throughout.
    ///     Transition counting is what makes blinking signals visible. A modal value alone reports
    ///     an indicator as "mostly 1" or "mostly 0" depending on where the window happened to fall,
    ///     which is noise; the transition count reports it as oscillating, which is the signal.
    ///     Returns null when the ID produced no frames in the window.
    /// </summary>
    private static BitStats[]? WindowBits(Session s, string id, double markerAt)
    {
        var window = FramesIn(s, id, markerAt + SettleMs, markerAt + SettleMs + HoldMs)
            .OrderBy(f => f.AtMs)
            .ToList();

        if (window.Count == 0)
        {
            return null;
        }

        var width = window.Min(f => f.Payload.Length) * 8;
        var stats = new BitStats[width];

        for (var bit = 0; bit < width; bit++)
        {
            var ones = 0;
            var transitions = 0;
            var previous = -1;

            foreach (var frame in window)
            {
                var v = GetBit(frame.Payload, bit);
                if (v == 1)
                {
                    ones++;
                }

                if (previous >= 0 && v != previous)
                {
                    transitions++;
                }

                previous = v;
            }

            var modal = ones * 2 >= window.Count ? 1 : 0;
            stats[bit] = new BitStats(modal, transitions, transitions == 0);
        }

        return stats;
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
        string ConfoundedBy)
    {
        public ResponseKind Kind { get; init; } = ResponseKind.Static;

        /// <summary>Mean transitions per active window, for blink responses.</summary>
        public double BlinkRate { get; init; }

        /// <summary>
        ///     Mean frames observed per window. Below roughly 8 the sampling is too sparse to
        ///     resolve a ~1.5 Hz blink (Nyquist), so BlinkRate is a lower bound and the finding
        ///     should be treated as provisional rather than measured.
        /// </summary>
        public double SamplesPerWindow { get; init; }

        public bool Undersampled => Kind == ResponseKind.Blink && SamplesPerWindow < 8;
    }

    /// <summary>Per-bit behaviour within one hold window.</summary>
    private readonly record struct BitStats(int Modal, int Transitions, bool Constant);
}
