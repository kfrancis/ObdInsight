using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ObdInsight.Core.Communication.Elm327;
using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Unfiltered raw CAN capture.
///
/// Purpose: discovery. Unlike the capability-driven broadcast paths (which configure
/// <c>AT CRA</c> hardware filters for a known ID set), this command explicitly *resets* the
/// receive filter and runs <c>AT MA</c> wide open, so IDs nobody has documented still show up.
/// That is the difference between validating a decoder and finding a signal.
///
/// Output is a timestamped line log plus a JSON summary, both replayable offline. The live
/// display shows a per-ID histogram and, per ID, which bits have ever changed during the
/// capture — the cheapest useful form of the bit-flip analysis the discovery harness formalises.
///
/// This command never transmits a CAN frame. It sends AT configuration commands to the adapter
/// and then listens. Whether the adapter itself acknowledges frames on the bus is governed by
/// its silent-monitoring setting, which this command sets explicitly and reports.
/// </summary>
public static class RawCaptureCommand
{
    private const string MarkerKeyHint = "SPACE = drop marker    Q = stop capture";

    /// <summary>Status/among-frames lines the ELM327 emits that are not CAN frames.</summary>
    private static readonly string[] s_statusLines =
    [
        "BUFFER FULL", "STOPPED", "CAN ERROR", "NO DATA", "DATA ERROR",
        "UNABLE TO CONNECT", "SEARCHING", "BUS INIT", "BUS ERROR", "FB ERROR", "?"
    ];

    public static async Task RunAsync(DevToolsSession session, CancellationToken ct = default)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Raw CAN Capture (unfiltered)[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Panel(
            """
            Captures every CAN frame the adapter can see, with no receive filter.

            [yellow]Before starting, confirm:[/]
              1. You know which bus the adapter is physically connected to.
              2. The vehicle is parked with the wheels chocked (first capture on a
                 new bus should not be done in READY).
              3. The adapter is in silent/listen-only mode. This command issues
                 AT CSM1 and reports the response, but older firmware may not
                 support it - check the reported result before trusting it.

            [grey]This command sends AT commands to the adapter and then listens.
            It never transmits a CAN frame.[/]
            """)
            .Header("[cyan]Raw capture[/]")
            .Border(BoxBorder.Rounded));

        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Continue?", defaultValue: false))
        {
            return;
        }

        var busLabel = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Bus label[/] [grey](free text, becomes the filename)[/]:")
                .DefaultValue("CAR-CAN"));

        var durationSeconds = AnsiConsole.Prompt(
            new TextPrompt<int>("[cyan]Capture duration in seconds[/] [grey](0 = until Q)[/]:")
                .DefaultValue(60)
                .Validate(v => v >= 0 ? ValidationResult.Success() : ValidationResult.Error("Must be >= 0")));

        var outputRoot = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Output directory[/]:")
                .DefaultValue(DefaultOutputRoot()));

        if (!session.IsConnected && !await session.ConnectAsync(ct))
        {
            return;
        }

        var transport = session.Transport;
        if (transport is null)
        {
            AnsiConsole.MarkupLine("[red]No transport available.[/]");
            return;
        }

        // Per-chunk RX logging would flood the console and corrupt the live display.
        var previousSuppress = session.SuppressTrafficLogging;
        session.SuppressTrafficLogging = true;

        var framer = new ElmFramer(transport);

        try
        {
            var setup = await ConfigureForMonitoringAsync(framer, ct);
            RenderSetupTable(setup);

            if (!AnsiConsole.Confirm("Start capture?", defaultValue: true))
            {
                return;
            }

            var result = await CaptureAsync(framer, durationSeconds, ct);
            var labels = PromptForMarkerLabels(result);
            var paths = await WriteOutputAsync(outputRoot, busLabel, session, setup, result, labels, ct);

            RenderSummary(result);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Log:[/]     {paths.LogPath.EscapeMarkup()}");
            AnsiConsole.MarkupLine($"[green]Summary:[/] {paths.JsonPath.EscapeMarkup()}");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Capture cancelled.[/]");
        }
        finally
        {
            // Best-effort: get the adapter out of monitoring mode so the session stays usable.
            try
            {
                await framer.WriteAsync("\r", CancellationToken.None);
                await Task.Delay(200, CancellationToken.None);
                framer.ClearBuffer();
            }
            catch
            {
                // The connection may already be gone; nothing useful to do here.
            }

            session.SuppressTrafficLogging = previousSuppress;
        }
    }

    // ---------------------------------------------------------------- setup

    /// <summary>
    /// Puts the adapter into wide-open monitoring configuration and records what each command
    /// answered, so the capture metadata says exactly how the data was collected.
    /// </summary>
    private static async Task<List<SetupStep>> ConfigureForMonitoringAsync(ElmFramer framer, CancellationToken ct)
    {
        // ATH1 is required (frames must carry their CAN ID) and ATCAF0 disables the adapter's
        // ISO-TP auto-formatting, which would otherwise reassemble/hide raw frames.
        // "AT CRA" with no argument resets the receive-address filter - this is the command
        // that makes the capture unfiltered.
        var commands = new (string Command, string Why, TimeSpan Timeout)[]
        {
            ("ATZ",    "reset adapter",                    TimeSpan.FromSeconds(6)),
            ("ATE0",   "echo off",                         TimeSpan.FromSeconds(3)),
            ("ATL0",   "linefeeds off",                    TimeSpan.FromSeconds(3)),
            ("ATS0",   "spaces off (compact frames)",      TimeSpan.FromSeconds(3)),
            ("ATH1",   "headers ON (need the CAN ID)",     TimeSpan.FromSeconds(3)),
            ("ATCAF0", "auto-formatting off (raw frames)", TimeSpan.FromSeconds(3)),
            ("ATSP6",  "ISO 15765-4 CAN 11-bit/500k",      TimeSpan.FromSeconds(4)),
            ("ATCSM1", "silent monitoring ON",             TimeSpan.FromSeconds(3)),
            ("ATCRA",  "RESET receive filter (unfiltered)", TimeSpan.FromSeconds(3)),
        };

        var steps = new List<SetupStep>(commands.Length);

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Configuring adapter for unfiltered monitoring...", async _ =>
            {
                foreach (var (command, why, timeout) in commands)
                {
                    string response;
                    try
                    {
                        framer.ClearBuffer();
                        response = Clean(await framer.SendAndReadFrameAsync(command, timeout, ct));
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        response = "(timeout)";
                    }
                    catch (TimeoutException)
                    {
                        response = "(timeout)";
                    }

                    steps.Add(new SetupStep(command, why, response));

                    if (command == "ATZ")
                    {
                        await Task.Delay(500, ct);
                    }
                }
            });

        return steps;
    }

    private static void RenderSetupTable(List<SetupStep> steps)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Command");
        table.AddColumn("Purpose");
        table.AddColumn("Response");

        foreach (var step in steps)
        {
            var ok = !step.Response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                     && !step.Response.Contains('?')
                     && step.Response != "(timeout)";

            table.AddRow(
                step.Command.EscapeMarkup(),
                $"[grey]{step.Why.EscapeMarkup()}[/]",
                ok ? $"[green]{step.Response.EscapeMarkup()}[/]" : $"[yellow]{step.Response.EscapeMarkup()}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);

        var csm = steps.FirstOrDefault(s => s.Command == "ATCSM1");
        if (csm is not null && (csm.Response.Contains('?') || csm.Response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)))
        {
            AnsiConsole.MarkupLine(
                "[yellow]Warning:[/] the adapter did not accept AT CSM1. Silent monitoring is " +
                "not confirmed - do not use this capture configuration on a powertrain bus " +
                "until you have verified listen-only behaviour another way.");
        }
    }

    // -------------------------------------------------------------- capture

    private static async Task<CaptureResult> CaptureAsync(ElmFramer framer, int durationSeconds, CancellationToken ct)
    {
        var result = new CaptureResult { StartedUtc = DateTime.UtcNow };
        var clock = Stopwatch.StartNew();

        framer.ClearBuffer();
        await framer.WriteAsync("AT MA\r", ct);

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (durationSeconds > 0)
        {
            window.CancelAfter(TimeSpan.FromSeconds(durationSeconds));
        }

        var table = BuildLiveTable(result, clock);
        var lastRender = TimeSpan.Zero;

        await AnsiConsole.Live(table)
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                while (!window.IsCancellationRequested)
                {
                    // Drain the keyboard first so Q stops promptly even on a quiet bus.
                    if (PumpKeyboard(result, clock, out var stop))
                    {
                        ctx.Refresh();
                    }

                    if (stop)
                    {
                        break;
                    }

                    string line;
                    try
                    {
                        line = await framer.ReadUntilAsync("\r", TimeSpan.FromMilliseconds(250), window.Token);
                    }
                    catch (TimeoutException)
                    {
                        result.IdleReads++;
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    Ingest(result, clock.Elapsed, line);

                    if (clock.Elapsed - lastRender > TimeSpan.FromMilliseconds(400))
                    {
                        lastRender = clock.Elapsed;
                        ctx.UpdateTarget(BuildLiveTable(result, clock));
                    }
                }
            });

        result.Duration = clock.Elapsed;
        return result;
    }

    /// <summary>Reads pending keystrokes. Returns true if the display should refresh.</summary>
    private static bool PumpKeyboard(CaptureResult result, Stopwatch clock, out bool stop)
    {
        stop = false;
        var changed = false;

        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;
            switch (key)
            {
                case ConsoleKey.Spacebar:
                    result.Markers.Add(new Marker(result.Markers.Count + 1, clock.Elapsed.TotalMilliseconds));
                    changed = true;
                    break;
                case ConsoleKey.Q:
                case ConsoleKey.Escape:
                    stop = true;
                    return true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Parses one line of ELM327 monitoring output into a frame or a status event.
    /// With ATH1 + ATS0 + ATCAF0 a frame arrives as contiguous hex: 3 ID nibbles (11-bit) or
    /// 8 (29-bit) followed by payload bytes. The two are distinguishable by length parity.
    /// </summary>
    private static void Ingest(CaptureResult result, TimeSpan at, string rawLine)
    {
        var line = rawLine.Replace("\n", "").Replace(">", "").Trim();
        if (line.Length == 0)
        {
            return;
        }

        result.TotalLines++;

        var upper = line.ToUpperInvariant();

        if (s_statusLines.Any(s => upper.Contains(s, StringComparison.Ordinal)))
        {
            result.Events.Add(new BusEvent(at.TotalMilliseconds, upper));
            if (upper.Contains("BUFFER FULL", StringComparison.Ordinal))
            {
                result.BufferFullCount++;
            }

            return;
        }

        if (!IsHex(upper))
        {
            result.Events.Add(new BusEvent(at.TotalMilliseconds, "UNPARSED:" + upper));
            result.UnparsedLines++;
            return;
        }

        int idLength;
        if (upper.Length >= 5 && upper.Length % 2 == 1)
        {
            idLength = 3;   // 11-bit ID + whole payload bytes => odd total
        }
        else if (upper.Length >= 10 && upper.Length % 2 == 0)
        {
            idLength = 8;   // 29-bit ID + whole payload bytes => even total
        }
        else
        {
            result.Events.Add(new BusEvent(at.TotalMilliseconds, "UNPARSED:" + upper));
            result.UnparsedLines++;
            return;
        }

        var idText = upper[..idLength];
        var payloadText = upper[idLength..];
        var payload = ParseHexBytes(payloadText);

        result.TotalFrames++;

        if (!result.Ids.TryGetValue(idText, out var stats))
        {
            stats = new IdStats(idText, payload, at.TotalMilliseconds);
            result.Ids[idText] = stats;
        }

        stats.Observe(payload, at.TotalMilliseconds);
    }

    private static Table BuildLiveTable(CaptureResult result, Stopwatch clock)
    {
        var seconds = Math.Max(clock.Elapsed.TotalSeconds, 0.001);

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.Title = new TableTitle(
            $"{result.TotalFrames} frames  |  {result.Ids.Count} IDs  |  {result.TotalFrames / seconds:F0}/s  " +
            $"|  {clock.Elapsed:mm\\:ss}  |  markers {result.Markers.Count}" +
            (result.BufferFullCount > 0 ? $"  |  [red]BUFFER FULL x{result.BufferFullCount}[/]" : ""));

        table.AddColumn("ID");
        table.AddColumn(new TableColumn("Count").RightAligned());
        table.AddColumn(new TableColumn("Hz").RightAligned());
        table.AddColumn("Last payload");
        table.AddColumn("Changing bits");

        foreach (var stats in result.Ids.Values.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            table.AddRow(
                $"[cyan]{stats.Id}[/]",
                stats.Count.ToString(),
                (stats.Count / seconds).ToString("F1"),
                Convert.ToHexString(stats.LastPayload),
                FormatChangedMask(stats.ChangedMask));
        }

        table.Caption = new TableTitle($"[grey]{MarkerKeyHint}[/]");
        return table;
    }

    /// <summary>
    /// Renders the per-bit change mask, dimming bytes that never moved. Static bytes are
    /// constants or unused padding; moving bytes are where signals (and counters) live.
    /// </summary>
    private static string FormatChangedMask(byte[] mask)
    {
        var sb = new StringBuilder();
        foreach (var b in mask)
        {
            sb.Append(b == 0 ? $"[grey]{b:X2}[/]" : $"[yellow]{b:X2}[/]");
        }

        return sb.ToString();
    }

    // --------------------------------------------------------------- output

    private static Dictionary<int, string> PromptForMarkerLabels(CaptureResult result)
    {
        var labels = new Dictionary<int, string>();
        if (result.Markers.Count == 0)
        {
            return labels;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]{result.Markers.Count} marker(s) recorded.[/] Label them now (blank to skip).");

        foreach (var marker in result.Markers)
        {
            var label = AnsiConsole.Prompt(
                new TextPrompt<string>($"  Marker {marker.Number} @ {marker.AtMs / 1000.0:F1}s:")
                    .AllowEmpty());

            if (!string.IsNullOrWhiteSpace(label))
            {
                labels[marker.Number] = label.Trim();
            }
        }

        return labels;
    }

    private static async Task<(string LogPath, string JsonPath)> WriteOutputAsync(
        string outputRoot,
        string busLabel,
        DevToolsSession session,
        List<SetupStep> setup,
        CaptureResult result,
        Dictionary<int, string> markerLabels,
        CancellationToken ct)
    {
        var safeBus = string.Concat(busLabel.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        var stamp = result.StartedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(outputRoot, $"{safeBus}-{stamp}");
        Directory.CreateDirectory(dir);

        var logPath = Path.Combine(dir, "capture.log");
        var jsonPath = Path.Combine(dir, "summary.json");

        var log = new StringBuilder();
        log.AppendLine("# ObdInsight raw CAN capture");
        log.AppendLine($"# bus={busLabel}");
        log.AppendLine($"# device={session.DeviceName} profile={session.Profile?.Name}");
        log.AppendLine($"# startedUtc={result.StartedUtc:O}");
        log.AppendLine($"# durationMs={result.Duration.TotalMilliseconds:F0}");
        log.AppendLine("# setup: " + string.Join(", ", setup.Select(s => $"{s.Command}=>{s.Response}")));
        log.AppendLine("# format: <elapsed_ms> <F|E|M> <id-or-kind> <payload-or-text>");

        // One merged, time-ordered stream so markers and bus events stay aligned with frames.
        var lines = new List<(double At, string Text)>();
        foreach (var stats in result.Ids.Values)
        {
            foreach (var (at, payload) in stats.Samples)
            {
                lines.Add((at, $"{at,10:F1} F {stats.Id} {Convert.ToHexString(payload)}"));
            }
        }

        foreach (var e in result.Events)
        {
            lines.Add((e.AtMs, $"{e.AtMs,10:F1} E - {e.Text}"));
        }

        foreach (var m in result.Markers)
        {
            var label = markerLabels.TryGetValue(m.Number, out var l) ? l : $"marker-{m.Number}";
            lines.Add((m.AtMs, $"{m.AtMs,10:F1} M {m.Number} {label}"));
        }

        foreach (var (_, text) in lines.OrderBy(l => l.At))
        {
            log.AppendLine(text);
        }

        await File.WriteAllTextAsync(logPath, log.ToString(), ct);

        var summary = new
        {
            bus = busLabel,
            // Stamped so a capture stays interpretable after the decoders change underneath it.
            toolVersion = ToolVersion(),
            device = session.DeviceName,
            profile = session.Profile?.Name,
            startedUtc = result.StartedUtc,
            durationMs = result.Duration.TotalMilliseconds,
            totalFrames = result.TotalFrames,
            totalLines = result.TotalLines,
            unparsedLines = result.UnparsedLines,
            bufferFullCount = result.BufferFullCount,
            idleReads = result.IdleReads,
            setup = setup.Select(s => new { s.Command, s.Why, s.Response }),
            markers = result.Markers.Select(m => new
            {
                m.Number,
                atMs = m.AtMs,
                label = markerLabels.TryGetValue(m.Number, out var l) ? l : null
            }),
            events = result.Events.Select(e => new { atMs = e.AtMs, e.Text }),
            ids = result.Ids.Values.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => new
            {
                id = s.Id,
                count = s.Count,
                hz = s.Count / Math.Max(result.Duration.TotalSeconds, 0.001),
                firstSeenMs = s.FirstSeenMs,
                lastSeenMs = s.LastSeenMs,
                dlcs = s.Dlcs.OrderBy(d => d).ToArray(),
                firstPayload = Convert.ToHexString(s.FirstPayload),
                lastPayload = Convert.ToHexString(s.LastPayload),
                changedMask = Convert.ToHexString(s.ChangedMask),
                distinctPayloads = s.DistinctPayloads
            })
        };

        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        return (logPath, jsonPath);
    }

    private static void RenderSummary(CaptureResult result)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Capture summary[/]").RuleStyle("grey"));

        var seconds = Math.Max(result.Duration.TotalSeconds, 0.001);
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("ID");
        table.AddColumn(new TableColumn("Count").RightAligned());
        table.AddColumn(new TableColumn("Hz").RightAligned());
        table.AddColumn("DLC");
        table.AddColumn(new TableColumn("Distinct").RightAligned());
        table.AddColumn("Changing bits");

        foreach (var s in result.Ids.Values.OrderBy(v => v.Id, StringComparer.Ordinal))
        {
            table.AddRow(
                $"[cyan]{s.Id}[/]",
                s.Count.ToString(),
                (s.Count / seconds).ToString("F1"),
                string.Join(",", s.Dlcs.OrderBy(d => d)),
                s.DistinctPayloads.ToString(),
                FormatChangedMask(s.ChangedMask));
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine(
            $"[grey]{result.TotalFrames} frames, {result.Ids.Count} IDs, " +
            $"{result.UnparsedLines} unparsed lines, {result.BufferFullCount} BUFFER FULL, " +
            $"{result.Duration.TotalSeconds:F1}s[/]");

        if (result.BufferFullCount > 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]BUFFER FULL seen:[/] the adapter dropped frames. Counts and rates are " +
                "lower bounds. A raw CAN interface is needed for drop-free capture at this bus load.");
        }
    }

    // ---------------------------------------------------------------- utils

    /// <summary>
    /// Informational version of the running build (MinVer-computed), recorded in every capture
    /// so a session can be traced back to the exact tool that produced it.
    /// </summary>
    private static string ToolVersion() =>
        typeof(RawCaptureCommand).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
        ?? typeof(RawCaptureCommand).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string DefaultOutputRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "ObdInsight-Captures");

    private static string Clean(string response) =>
        response.Replace("\r", " ").Replace("\n", " ").Replace(">", "").Trim();

    private static bool IsHex(string s) => s.Length > 0 && s.All(Uri.IsHexDigit);

    private static byte[] ParseHexBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        }

        return bytes;
    }

    // ---------------------------------------------------------------- model

    private sealed record SetupStep(string Command, string Why, string Response);

    private sealed record Marker(int Number, double AtMs);

    private sealed record BusEvent(double AtMs, string Text);

    private sealed class CaptureResult
    {
        public DateTime StartedUtc { get; init; }
        public TimeSpan Duration { get; set; }
        public int TotalFrames { get; set; }
        public int TotalLines { get; set; }
        public int UnparsedLines { get; set; }
        public int BufferFullCount { get; set; }
        public int IdleReads { get; set; }
        public Dictionary<string, IdStats> Ids { get; } = new(StringComparer.Ordinal);
        public List<Marker> Markers { get; } = [];
        public List<BusEvent> Events { get; } = [];
    }

    private sealed class IdStats
    {
        private readonly HashSet<string> _distinct = new(StringComparer.Ordinal);

        public IdStats(string id, byte[] firstPayload, double firstSeenMs)
        {
            Id = id;
            FirstPayload = firstPayload;
            LastPayload = firstPayload;
            FirstSeenMs = firstSeenMs;
            ChangedMask = new byte[Math.Max(firstPayload.Length, 8)];
        }

        public string Id { get; }
        public byte[] FirstPayload { get; }
        public byte[] LastPayload { get; private set; }
        public byte[] ChangedMask { get; }
        public double FirstSeenMs { get; }
        public double LastSeenMs { get; private set; }
        public int Count { get; private set; }
        public HashSet<int> Dlcs { get; } = [];
        public int DistinctPayloads => _distinct.Count;

        /// <summary>Raw samples, kept so the capture can be replayed offline frame-by-frame.</summary>
        public List<(double At, byte[] Payload)> Samples { get; } = [];

        public void Observe(byte[] payload, double atMs)
        {
            Count++;
            LastPayload = payload;
            LastSeenMs = atMs;
            Dlcs.Add(payload.Length);
            _distinct.Add(Convert.ToHexString(payload));
            Samples.Add((atMs, payload));

            // OR the XOR against the first sample: any bit that has ever differed stays set.
            var n = Math.Min(payload.Length, Math.Min(FirstPayload.Length, ChangedMask.Length));
            for (var i = 0; i < n; i++)
            {
                ChangedMask[i] |= (byte)(payload[i] ^ FirstPayload[i]);
            }
        }
    }
}
