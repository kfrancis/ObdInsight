using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ObdInsight.Core.Communication.Elm327;
using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
///     Unfiltered raw CAN capture.
///     Purpose: discovery. Unlike the capability-driven broadcast paths (which configure
///     <c>AT CRA</c> hardware filters for a known ID set), this command explicitly *resets* the
///     receive filter and runs <c>AT MA</c> wide open, so IDs nobody has documented still show up.
///     That is the difference between validating a decoder and finding a signal.
///     Output is a timestamped line log plus a JSON summary, both replayable offline. The live
///     display shows a per-ID histogram and, per ID, which bits have ever changed during the
///     capture - the cheapest useful form of the bit-flip analysis the discovery harness formalises.
///     Runs two ways over the same body: interactively (prompts, live table, SPACE for markers) and
///     headlessly (arguments, plain stdout, markers appended to a watched file). The headless path
///     exists so the tool can be driven over SSH from a development machine while the laptop sits in
///     the car.
///     This command never transmits a CAN frame. It sends AT configuration commands through
///     <see cref="ListenOnlyElmTransport" />, which throws on anything outside the listen-only
///     whitelist, and then listens.
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

    // ------------------------------------------------------------ entry points

    /// <summary>Interactive entry point: prompts for options, then runs the shared body.</summary>
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

                [grey]Writes are whitelisted at the transport: anything that could transmit
                is refused, not merely avoided.[/]
                """)
            .Header("[cyan]Raw capture[/]")
            .Border(BoxBorder.Rounded));

        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("Continue?", false))
        {
            return;
        }

        var options = new RawCaptureOptions
        {
            BusLabel = AnsiConsole.Prompt(
                new TextPrompt<string>("[cyan]Bus label[/] [grey](free text, becomes the filename)[/]:")
                    .DefaultValue("CAR-CAN")),
            DurationSeconds = AnsiConsole.Prompt(
                new TextPrompt<int>("[cyan]Capture duration in seconds[/] [grey](0 = until Q)[/]:")
                    .DefaultValue(60)
                    .Validate(v => v >= 0 ? ValidationResult.Success() : ValidationResult.Error("Must be >= 0"))),
            OutputRoot = AnsiConsole.Prompt(
                new TextPrompt<string>("[cyan]Output directory[/]:")
                    .DefaultValue(DefaultOutputRoot())),
            Headless = false
        };

        await ExecuteAsync(session, options, ct);
    }

    /// <summary>
    ///     Guided stimulus probes: pick a script, then follow prompts while a capture runs behind
    ///     them. Produces the same artefacts as a plain capture, but with markers that are already
    ///     labelled and a sequence that actually follows the discovery protocol - baseline first,
    ///     three alternations per stimulus, confounders included.
    /// </summary>
    public static async Task RunGuidedAsync(DevToolsSession session, CancellationToken ct = default)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Guided stimulus probes[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Script");
        table.AddColumn("Steps");
        table.AddColumn("Safe when");
        foreach (var s in ProbeScripts.All)
        {
            table.AddRow(s.Name.EscapeMarkup(), s.Steps.Count.ToString(), $"[yellow]{s.SafeWhen.EscapeMarkup()}[/]");
        }

        AnsiConsole.Write(table);

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Which script?[/]")
                .AddChoices(ProbeScripts.All.Select(s => s.Name).Append("cancel")));

        if (choice == "cancel")
        {
            return;
        }

        var script = ProbeScripts.Find(choice)!;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[yellow]Confirm the vehicle is: {script.SafeWhen.EscapeMarkup()}[/]");
        if (!AnsiConsole.Confirm("Ready?", false))
        {
            return;
        }

        var options = new RawCaptureOptions
        {
            BusLabel = AnsiConsole.Prompt(
                new TextPrompt<string>("[cyan]Bus label[/]:").DefaultValue("CAR-CAN")),
            // The script decides when the capture ends, not a timer.
            DurationSeconds = 0,
            OutputRoot = AnsiConsole.Prompt(
                new TextPrompt<string>("[cyan]Output directory[/]:").DefaultValue(DefaultOutputRoot())),
            Headless = false,
            Script = script
        };

        await ExecuteAsync(session, options, ct);
    }

    /// <summary>
    ///     Headless entry point. Returns a process exit code: 0 on success, non-zero on failure, so
    ///     a remote caller can branch on it without parsing output.
    /// </summary>
    public static async Task<int> RunHeadlessAsync(
        DevToolsSession session,
        RawCaptureOptions options,
        CancellationToken ct = default)
    {
        if (options.DurationSeconds <= 0)
        {
            Console.Error.WriteLine(
                "error: --seconds must be greater than 0 in headless mode (no keyboard to stop it).");
            return 2;
        }

        try
        {
            var jsonPath = await ExecuteAsync(session, options, ct);
            if (jsonPath is null)
            {
                return 1;
            }

            // Sole stdout line on success, so a caller can consume it directly.
            Console.Out.WriteLine(jsonPath);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("error: capture cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    // ------------------------------------------------------------ shared body

    /// <summary>Returns the summary JSON path on success, null if the capture did not run.</summary>
    private static async Task<string?> ExecuteAsync(
        DevToolsSession session,
        RawCaptureOptions options,
        CancellationToken ct)
    {
        // A connection that already ran ELM327 bring-up has transmitted AT SP 0 auto-detect and
        // 0100 probes. On a powertrain bus that is exactly what must never happen, so such a
        // connection is not reusable here - reconnect transport-only instead.
        if (session.IsConnected && session.AdapterInitialized)
        {
            const string warning =
                "This connection already ran ELM327 bring-up, which probes the bus (AT SP 0, 0100). " +
                "Those frames were transmitted on whatever bus the adapter is wired to. " +
                "A clean transport-only reconnect is required before a listen-only capture.";

            if (options.Headless)
            {
                // No human to ask; reconnecting is the safe action and is what the operator
                // would have chosen, so do it and say so loudly.
                Console.Error.WriteLine("warning: " + warning + " Reconnecting transport-only.");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]{warning.EscapeMarkup()}[/]");
                if (!AnsiConsole.Confirm("Reconnect transport-only now?", true))
                {
                    return null;
                }
            }

            await session.DisconnectAsync();
        }

        // Must precede ConnectAsync: the transport is built inside it, and the failures worth
        // seeing (device lookup, GATT session, service/characteristic discovery) happen there.
        session.EnableTransportDebugLogging = options.Verbose;

        if (!session.IsConnected && !await session.ConnectAsync(ct))
        {
            Report(options, "error: failed to connect to the adapter.", true);
            return null;
        }

        var transport = session.Transport;
        if (transport is null)
        {
            Report(options, "error: no transport available.", true);
            return null;
        }

        if (!session.ArmListenOnly())
        {
            return null;
        }

        Report(options, "Listen-only armed. Writes are whitelisted at the transport.");

        var previousSuppress = session.SuppressTrafficLogging;
        session.SuppressTrafficLogging = true;

        // The guard lives at the transport, not in this method, so it holds for anything written
        // through the framer regardless of later edits to the command sequence below.
        var guarded = new ListenOnlyElmTransport(transport);
        var framer = new ElmFramer(guarded);

        try
        {
            var setup = await ConfigureForMonitoringAsync(framer, options, ct);

            if (options.Headless)
            {
                ReportSetupPlain(setup);
            }
            else
            {
                RenderSetupTable(setup);
                if (!AnsiConsole.Confirm("Start capture?", true))
                {
                    return null;
                }
            }

            var result = await CaptureAsync(framer, options, ct);

            if (!options.Headless)
            {
                PromptForMarkerLabels(result);
            }

            var paths = await WriteOutputAsync(options, session, setup, result, ct);

            if (options.Headless)
            {
                ReportSummaryPlain(result, paths.LogPath);
            }
            else
            {
                RenderSummary(result);
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[green]Log:[/]     {paths.LogPath.EscapeMarkup()}");
                AnsiConsole.MarkupLine($"[green]Summary:[/] {paths.JsonPath.EscapeMarkup()}");
            }

            return paths.JsonPath;
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

            if (guarded.BlockedAttempts.Count > 0)
            {
                Report(
                    options,
                    $"listen-only guard blocked {guarded.BlockedAttempts.Count} write(s): " +
                    string.Join(", ", guarded.BlockedAttempts),
                    true);
            }

            session.DisarmListenOnly();
            session.SuppressTrafficLogging = previousSuppress;
        }
    }

    // ---------------------------------------------------------------- setup

    /// <summary>
    ///     Puts the adapter into wide-open monitoring configuration and records what each command
    ///     answered, so the capture metadata says exactly how the data was collected.
    /// </summary>
    private static async Task<List<SetupStep>> ConfigureForMonitoringAsync(
        ElmFramer framer,
        RawCaptureOptions options,
        CancellationToken ct)
    {
        // ATH1 is required (frames must carry their CAN ID) and ATCAF0 disables the adapter's
        // ISO-TP auto-formatting, which would otherwise reassemble/hide raw frames.
        // "AT CRA" with no argument resets the receive-address filter - this is the command
        // that makes the capture unfiltered.
        var commands = new (string Command, string Why, TimeSpan Timeout)[]
        {
            ("ATZ", "reset adapter", TimeSpan.FromSeconds(6)), ("ATE0", "echo off", TimeSpan.FromSeconds(3)),
            ("ATL0", "linefeeds off", TimeSpan.FromSeconds(3)),
            ("ATS0", "spaces off (compact frames)", TimeSpan.FromSeconds(3)),
            ("ATH1", "headers ON (need the CAN ID)", TimeSpan.FromSeconds(3)),
            ("ATCAF0", "auto-formatting off (raw frames)", TimeSpan.FromSeconds(3)),
            ("ATSP6", "ISO 15765-4 CAN 11-bit/500k", TimeSpan.FromSeconds(4)),
            ("ATCSM1", "silent monitoring ON", TimeSpan.FromSeconds(3)),
            ("ATCRA", "RESET receive filter (unfiltered)", TimeSpan.FromSeconds(3))
        };

        var steps = new List<SetupStep>(commands.Length);

        async Task RunAllAsync()
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
        }

        if (options.Headless)
        {
            await RunAllAsync();
        }
        else
        {
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Configuring adapter for unfiltered monitoring...", async _ => await RunAllAsync());
        }

        return steps;
    }

    private static bool IsSetupStepOk(SetupStep step) =>
        !step.Response.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
        && !step.Response.Contains('?')
        && step.Response != "(timeout)";

    private static void RenderSetupTable(List<SetupStep> steps)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Command");
        table.AddColumn("Purpose");
        table.AddColumn("Response");

        foreach (var step in steps)
        {
            var ok = IsSetupStepOk(step);
            table.AddRow(
                step.Command.EscapeMarkup(),
                $"[grey]{step.Why.EscapeMarkup()}[/]",
                ok ? $"[green]{step.Response.EscapeMarkup()}[/]" : $"[yellow]{step.Response.EscapeMarkup()}[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        WarnIfSilentMonitoringUnconfirmed(steps,
            s => AnsiConsole.MarkupLine($"[yellow]Warning:[/] {s.EscapeMarkup()}"));
    }

    private static void ReportSetupPlain(List<SetupStep> steps)
    {
        foreach (var step in steps)
        {
            Console.Error.WriteLine($"setup {step.Command,-8} {(IsSetupStepOk(step) ? "ok" : "??")}  {step.Response}");
        }

        WarnIfSilentMonitoringUnconfirmed(steps, s => Console.Error.WriteLine("warning: " + s));
    }

    private static void WarnIfSilentMonitoringUnconfirmed(List<SetupStep> steps, Action<string> warn)
    {
        var csm = steps.FirstOrDefault(s => s.Command == "ATCSM1");
        if (csm is not null && !IsSetupStepOk(csm))
        {
            warn("the adapter did not accept AT CSM1. Silent monitoring is not confirmed - do not " +
                 "use this capture configuration on a powertrain bus until you have verified " +
                 "listen-only behaviour another way.");
        }
    }

    // -------------------------------------------------------------- capture

    private static async Task<CaptureResult> CaptureAsync(
        ElmFramer framer,
        RawCaptureOptions options,
        CancellationToken ct)
    {
        var result = new CaptureResult { StartedUtc = DateTime.UtcNow };
        var clock = Stopwatch.StartNew();
        var markers = new MarkerFileWatcher(options.MarkerFilePath);

        framer.ClearBuffer();
        await framer.WriteAsync("AT MA\r", ct);

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (options.DurationSeconds > 0)
        {
            window.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));
        }

        if (options.Script is not null)
        {
            // The read loop must keep draining while the operator is being prompted - a blocked
            // reader means BUFFER FULL and a dead capture. So the loop runs in the background and
            // the script drives the foreground, both writing into the same result.
            var reader = CaptureLoopAsync(framer, result, clock, markers, window, null);
            try
            {
                await RunScriptAsync(options.Script, result, clock, window.Token);
            }
            finally
            {
                window.Cancel();
                try { await reader; }
                catch (OperationCanceledException) { }
            }
        }
        else if (options.Headless)
        {
            await CaptureLoopAsync(framer, result, clock, markers, window, null);
        }
        else
        {
            var lastRender = TimeSpan.Zero;
            await AnsiConsole.Live(BuildLiveTable(result, clock))
                .AutoClear(false)
                .StartAsync(async ctx =>
                {
                    await CaptureLoopAsync(framer, result, clock, markers, window, refreshNow =>
                    {
                        if (!refreshNow && clock.Elapsed - lastRender <= TimeSpan.FromMilliseconds(400))
                        {
                            return;
                        }

                        lastRender = clock.Elapsed;
                        ctx.UpdateTarget(BuildLiveTable(result, clock));
                    });
                });
        }

        result.Duration = clock.Elapsed;
        return result;
    }

    /// <summary>
    ///     The read loop, shared by both modes. <paramref name="onTick" /> is null headlessly; when
    ///     present it is called to refresh the live display (true = refresh immediately).
    /// </summary>
    private static async Task CaptureLoopAsync(
        ElmFramer framer,
        CaptureResult result,
        Stopwatch clock,
        MarkerFileWatcher markers,
        CancellationTokenSource window,
        Action<bool>? onTick)
    {
        var interactive = onTick is not null;

        while (!window.IsCancellationRequested)
        {
            // Markers first so one dropped just before the window closes still lands.
            if (markers.Poll(clock.Elapsed.TotalMilliseconds, result))
            {
                onTick?.Invoke(true);
            }

            if (interactive)
            {
                // Drain the keyboard so Q stops promptly even on a quiet bus.
                if (PumpKeyboard(result, clock, out var stop))
                {
                    onTick?.Invoke(true);
                }

                if (stop)
                {
                    break;
                }
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

            var bufferFullBefore = result.BufferFullCount;
            Ingest(result, clock.Elapsed, line);

            // BUFFER FULL means the adapter gave up and left monitoring mode on its own - it does
            // not resume. Without a restart the rest of the window is silence: a 20s capture of a
            // busy CAR-CAN yielded 33 frames in the first 183ms and nothing after (2026-08-31).
            // Re-issue AT MA and keep going. Frames between the overflow and the restart are lost,
            // which is why bufferFullCount is reported and counts are only lower bounds.
            if (result.BufferFullCount > bufferFullBefore)
            {
                result.MonitorRestarts++;
                framer.ClearBuffer();
                try
                {
                    await framer.WriteAsync("AT MA\r", window.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            onTick?.Invoke(false);
        }
    }

    /// <summary>
    ///     Walks the operator through a stimulus script, recording a labelled marker at the moment
    ///     each action is confirmed.
    ///     Confirmation is explicit rather than timed: the harness must not assume the stimulus
    ///     happened because it printed a prompt. The operator presses ENTER once the action is done
    ///     AND held, then a settle delay discards the transient before the hold window is measured.
    ///     The marker is stamped at confirmation, so the analysis knows where the boundary is even
    ///     though the exact actuation moment is a little earlier.
    /// </summary>
    private static async Task RunScriptAsync(
        ProbeScript script,
        CaptureResult result,
        Stopwatch clock,
        CancellationToken ct)
    {
        // Transient at the start of an action is discarded before the state is treated as held.
        var settle = TimeSpan.FromMilliseconds(1500);

        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[cyan]Guided probes: {script.Name}[/]").RuleStyle("grey"));
        AnsiConsole.MarkupLine($"[grey]{script.Description.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine($"[yellow]Safe when:[/] {script.SafeWhen.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"[grey]{script.Steps.Count} steps. Ctrl+C aborts; partial data is still written.[/]");
        AnsiConsole.WriteLine();

        for (var i = 0; i < script.Steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var step = script.Steps[i];
            var n = $"[grey][[{i + 1}/{script.Steps.Count}]][/]";

            if (step.Kind == ProbeStepKind.Idle)
            {
                result.AddMarker(clock.Elapsed.TotalMilliseconds, step.Label + "-start");
                AnsiConsole.MarkupLine($"{n} [yellow]{step.Instruction.EscapeMarkup()}[/]");

                for (var remaining = step.Seconds; remaining > 0; remaining--)
                {
                    ct.ThrowIfCancellationRequested();
                    AnsiConsole.Markup($"\r      [grey]{remaining,3}s remaining, frames {result.TotalFrames}   [/]");
                    await Task.Delay(1000, ct);
                }

                AnsiConsole.MarkupLine("\r      [green]done[/]                                   ");
                result.AddMarker(clock.Elapsed.TotalMilliseconds, step.Label + "-end");
                continue;
            }

            AnsiConsole.MarkupLine($"{n} [white]{step.Instruction.EscapeMarkup()}[/]");
            AnsiConsole.Markup("      [grey]press ENTER when done and held...[/]");
            Bell();

            // Console.ReadLine blocks a thread; keep it off the loop that owns the read side.
            await Task.Run(Console.ReadLine, ct);

            result.AddMarker(clock.Elapsed.TotalMilliseconds, step.Label);

            await Task.Delay(settle, ct);
            for (var remaining = step.Seconds; remaining > 0; remaining--)
            {
                ct.ThrowIfCancellationRequested();
                AnsiConsole.Markup($"\r      [grey]holding {remaining}s, frames {result.TotalFrames}    [/]");
                await Task.Delay(1000, ct);
            }

            AnsiConsole.MarkupLine($"\r      [green]recorded '{step.Label}'[/]                     ");
            // Two: the hold window is measured, you can move on to the next control.
            Bell(2);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Script complete.[/]");

        // Three, so the end of the run is distinguishable from yet another prompt.
        Bell(3);
    }

    /// <summary>
    ///     ASCII BEL. During a guided run the operator is looking at the vehicle, not the screen, so
    ///     a prompt that only appears visually is a prompt that gets missed. Written straight to the
    ///     console rather than through AnsiConsole, which treats it as markup-free text and may
    ///     buffer it, and it survives an SSH session so a remotely-driven run signals too.
    /// </summary>
    private static void Bell(int times = 1)
    {
        try
        {
            for (var i = 0; i < times; i++)
            {
                Console.Out.Write('\a');
                if (i + 1 < times)
                {
                    Thread.Sleep(120);
                }
            }

            Console.Out.Flush();
        }
        catch
        {
            // A console that cannot beep must not abort a capture.
        }
    }

    /// <summary>Reads pending keystrokes. Returns true if the display should refresh.</summary>
    private static bool PumpKeyboard(CaptureResult result, Stopwatch clock, out bool stop)
    {
        stop = false;
        var changed = false;

        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true).Key;
            switch (key)
            {
                case ConsoleKey.Spacebar:
                    result.AddMarker(clock.Elapsed.TotalMilliseconds, null);
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
    ///     Parses one line of ELM327 monitoring output into a frame or a status event.
    ///     With ATH1 + ATS0 + ATCAF0 a frame arrives as contiguous hex: 3 ID nibbles (11-bit) or
    ///     8 (29-bit) followed by payload bytes. The two are distinguishable by length parity.
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
            idLength = 3; // 11-bit ID + whole payload bytes => odd total
        }
        else if (upper.Length >= 10 && upper.Length % 2 == 0)
        {
            idLength = 8; // 29-bit ID + whole payload bytes => even total
        }
        else
        {
            result.Events.Add(new BusEvent(at.TotalMilliseconds, "UNPARSED:" + upper));
            result.UnparsedLines++;
            return;
        }

        var idText = upper[..idLength];
        var payload = ParseHexBytes(upper[idLength..]);

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
    ///     Renders the per-bit change mask, dimming bytes that never moved. Static bytes are
    ///     constants or unused padding; moving bytes are where signals (and counters) live.
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

    private static void PromptForMarkerLabels(CaptureResult result)
    {
        if (result.Markers.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]{result.Markers.Count} marker(s) recorded.[/] Label them now (blank to skip).");

        foreach (var marker in result.Markers.Where(m => m.Label is null).ToList())
        {
            var label = AnsiConsole.Prompt(
                new TextPrompt<string>($"  Marker {marker.Number} @ {marker.AtMs / 1000.0:F1}s:")
                    .AllowEmpty());

            if (!string.IsNullOrWhiteSpace(label))
            {
                result.SetMarkerLabel(marker.Number, label.Trim());
            }
        }
    }

    private static async Task<(string LogPath, string JsonPath)> WriteOutputAsync(
        RawCaptureOptions options,
        DevToolsSession session,
        List<SetupStep> setup,
        CaptureResult result,
        CancellationToken ct)
    {
        var safeBus = string.Concat(options.BusLabel.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        var stamp = result.StartedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(options.OutputRoot, $"{safeBus}-{stamp}");
        Directory.CreateDirectory(dir);

        var logPath = Path.Combine(dir, "capture.log");
        var jsonPath = Path.Combine(dir, "summary.json");

        var log = new StringBuilder();
        log.AppendLine("# ObdInsight raw CAN capture");
        log.AppendLine($"# bus={options.BusLabel}");
        log.AppendLine($"# toolVersion={ToolVersion()}");
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
            lines.Add((m.AtMs, $"{m.AtMs,10:F1} M {m.Number} {m.Label ?? $"marker-{m.Number}"}"));
        }

        foreach (var (_, text) in lines.OrderBy(l => l.At))
        {
            log.AppendLine(text);
        }

        await File.WriteAllTextAsync(logPath, log.ToString(), ct);

        var summary = new
        {
            bus = options.BusLabel,
            // Stamped so a capture stays interpretable after the decoders change underneath it.
            toolVersion = ToolVersion(),
            headless = options.Headless,
            device = session.DeviceName,
            profile = session.Profile?.Name,
            startedUtc = result.StartedUtc,
            durationMs = result.Duration.TotalMilliseconds,
            totalFrames = result.TotalFrames,
            totalLines = result.TotalLines,
            unparsedLines = result.UnparsedLines,
            bufferFullCount = result.BufferFullCount,
            idleReads = result.IdleReads,
            monitorRestarts = result.MonitorRestarts,
            setup = setup.Select(s => new { s.Command, s.Why, s.Response }),
            markers = result.Markers.Select(m => new { m.Number, atMs = m.AtMs, label = m.Label }),
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

    private static void ReportSummaryPlain(CaptureResult result, string logPath)
    {
        var seconds = Math.Max(result.Duration.TotalSeconds, 0.001);
        Console.Error.WriteLine(
            $"captured {result.TotalFrames} frames across {result.Ids.Count} IDs in {seconds:F1}s " +
            $"({result.UnparsedLines} unparsed, {result.BufferFullCount} BUFFER FULL, {result.MonitorRestarts} restarts, {result.Markers.Count} markers)");

        foreach (var s in result.Ids.Values.OrderBy(v => v.Id, StringComparer.Ordinal))
        {
            Console.Error.WriteLine(
                $"  {s.Id}  n={s.Count,-6} {s.Count / seconds,6:F1}Hz  dlc={string.Join(",", s.Dlcs.OrderBy(d => d))}  " +
                $"changed={Convert.ToHexString(s.ChangedMask)}  last={Convert.ToHexString(s.LastPayload)}");
        }

        if (result.BufferFullCount > 0)
        {
            Console.Error.WriteLine(
                "warning: BUFFER FULL seen - the adapter dropped frames. Counts and rates are lower bounds; " +
                "do not conclude an ID is absent from this capture.");
        }

        Console.Error.WriteLine("log: " + logPath);
    }

    // ---------------------------------------------------------------- utils

    private static void Report(RawCaptureOptions options, string message, bool isError = false)
    {
        if (options.Headless)
        {
            Console.Error.WriteLine(isError ? message : "info: " + message);
        }
        else
        {
            AnsiConsole.MarkupLine(isError
                ? $"[red]{message.EscapeMarkup()}[/]"
                : $"[green]{message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    ///     Informational version of the running build (MinVer-computed), recorded in every capture
    ///     so a session can be traced back to the exact tool that produced it.
    /// </summary>
    private static string ToolVersion() =>
        typeof(RawCaptureCommand).Assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
        ?? typeof(RawCaptureCommand).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string DefaultOutputRoot() =>
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

    /// <summary>
    ///     Tails a text file, turning each appended line into a labelled marker. Opened shared so an
    ///     external appender (<c>echo ... &gt;&gt; markers.txt</c>, over SSH or from a phone) is never
    ///     blocked by this reader.
    /// </summary>
    private sealed class MarkerFileWatcher
    {
        private readonly string? _path;
        private long _position;

        public MarkerFileWatcher(string? path)
        {
            _path = path;

            // Only lines appended after the capture starts count; ignore pre-existing content.
            if (path is not null && File.Exists(path))
            {
                _position = new FileInfo(path).Length;
            }
        }

        /// <summary>Returns true if any marker was added.</summary>
        public bool Poll(double atMs, CaptureResult result)
        {
            if (_path is null || !File.Exists(_path))
            {
                return false;
            }

            try
            {
                var length = new FileInfo(_path).Length;
                if (length <= _position)
                {
                    // Truncated or replaced: resync rather than reading garbage.
                    _position = Math.Min(_position, length);
                    return false;
                }

                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(_position, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var added = false;

                while (reader.ReadLine() is { } line)
                {
                    var label = line.Trim();
                    if (label.Length > 0)
                    {
                        result.AddMarker(atMs, label);
                        added = true;
                    }
                }

                _position = stream.Position;
                return added;
            }
            catch (IOException)
            {
                // Mid-write; try again on the next poll rather than failing the capture.
                return false;
            }
        }
    }

    private sealed record SetupStep(string Command, string Why, string Response);

    private sealed record Marker(int Number, double AtMs)
    {
        public string? Label { get; set; }
    }

    private sealed record BusEvent(double AtMs, string Text);

    private sealed class CaptureResult
    {
        private readonly List<Marker> _markers = [];

        public DateTime StartedUtc { get; init; }
        public TimeSpan Duration { get; set; }
        public int TotalFrames { get; set; }
        public int TotalLines { get; set; }
        public int UnparsedLines { get; set; }
        public int BufferFullCount { get; set; }
        public int IdleReads { get; set; }
        public int MonitorRestarts { get; set; }
        public Dictionary<string, IdStats> Ids { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<Marker> Markers => _markers;
        public List<BusEvent> Events { get; } = [];

        /// <summary>
        ///     Guarded because guided probe mode adds markers from the prompt thread while the read
        ///     loop runs on another. Contention is negligible - a marker per operator action.
        /// </summary>
        public void AddMarker(double atMs, string? label)
        {
            lock (_markers)
            {
                _markers.Add(new Marker(_markers.Count + 1, atMs) { Label = label });
            }
        }

        public void SetMarkerLabel(int number, string label)
        {
            lock (_markers)
            {
                var marker = _markers.FirstOrDefault(m => m.Number == number);
                if (marker is not null)
                {
                    marker.Label = label;
                }
            }
        }

        /// <summary>Snapshot for readers that must not race the prompt thread.</summary>
        public IReadOnlyList<Marker> MarkerSnapshot()
        {
            lock (_markers)
            {
                return _markers.ToList();
            }
        }
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
