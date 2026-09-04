using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Communication.Slcan;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;
using ObdInsight.Transports.Serial;
using Serilog;
using Serilog.Extensions.Logging;
using Spectre.Console;

namespace ObdInsight.Application;

/// <summary>
///     Console session for a USB-CAN adapter on a serial port (CANable and compatible, SLCAN
///     firmware): raw broadcast capture with live decode, no ELM327 anywhere in the path.
/// </summary>
/// <remarks>
///     <para>
///         What this proves: <c>SerialElmTransport</c> → <c>SlcanFrameSource</c> →
///         <c>CanMonitor</c> → generated frame decoders / Leaf broadcast capabilities, end to
///         end on real hardware. Everything the BLE path does with monitoring, minus the
///         request/response half (UDS needs transmit + ISO-TP over raw CAN, which does not exist
///         yet - see <c>docs/CANABLE_SUPPORT.md</c>).
///     </para>
///     <para>
///         Listen-only unless <c>--tx</c> is given. On a powertrain bus a transmitting node is a
///         physical-safety concern, so silent mode is the default and normal mode has to be asked
///         for by name.
///     </para>
/// </remarks>
internal static class SerialCanSession
{
    /// <summary>Command-line switch that selects this session: <c>--serial=COM5</c>.</summary>
    public const string PortArgument = "--serial=";

    /// <summary>Nominal bitrate in kbit/s: <c>--bitrate=500</c> (default 500, the Leaf's buses).</summary>
    public const string BitrateArgument = "--bitrate=";

    /// <summary>Bounded run in seconds: <c>--duration=10</c>. Default runs until Ctrl+C.</summary>
    public const string DurationArgument = "--duration=";

    /// <summary>Open in NORMAL mode (adapter acknowledges frames on the bus). Off by default.</summary>
    public const string TransmitArgument = "--tx";

    public static bool IsRequested(string[] args) => args.Any(a => a.StartsWith(PortArgument, StringComparison.Ordinal));

    public static async Task RunAsync(string[] args, CancellationToken ct)
    {
        var port = args.First(a => a.StartsWith(PortArgument, StringComparison.Ordinal))[PortArgument.Length..];
        var bitrate = ParseInt(args, BitrateArgument, 500);
        var duration = ParseInt(args, DurationArgument, 0);
        var listenOnly = !args.Contains(TransmitArgument);

        Log.Information("=== Starting serial CAN session on {Port} @ {Bitrate} kbit/s, {Mode} ===",
            port, bitrate, listenOnly ? "listen-only" : "NORMAL");
        AnsiConsole.MarkupLine(
            $"[cyan]Serial CAN session:[/] {port.EscapeMarkup()} @ {bitrate} kbit/s, " +
            (listenOnly ? "[green]listen-only[/]" : "[red]NORMAL mode (adapter will ACK on the bus)[/]"));

        var available = SerialElmTransport.AvailablePorts();
        if (!available.Contains(port, StringComparer.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine(
                $"[red]Port {port.EscapeMarkup()} not found.[/] Available: {string.Join(", ", available).EscapeMarkup()}");
            return;
        }

        using var loggerFactory = new SerilogLoggerFactory(Log.Logger);

        await using var transport = new SerialElmTransport(port, logger: loggerFactory.CreateLogger<SerialElmTransport>());
        await transport.OpenAsync(ct);
        AnsiConsole.MarkupLine("[green]✓[/] Serial port open.");

        await using var source = new SlcanFrameSource(
            transport,
            SlcanProtocol.BitrateCommand(bitrate),
            listenOnly,
            logger: loggerFactory.CreateLogger<SlcanFrameSource>());

        // The Leaf broadcast command set over the raw source: every decoder that exists for
        // broadcast frames, none of the UDS ones. Which bus the adapter is on decides which
        // capabilities actually see data.
        var commands = new LeafAze0CommandSet(source);
        var monitor = commands.Monitor;
        await using var monitorLifetime = monitor;

        await monitor.StartAsync(ct);

        AnsiConsole.MarkupLine(
            $"[green]✓[/] Adapter: [cyan]{(source.FirmwareVersion ?? "(no version banner)").EscapeMarkup()}[/] " +
            $"dialect [cyan]{source.Dialect}[/]");
        if (source.Dialect == SlcanDialect.Unknown)
        {
            AnsiConsole.MarkupLine(
                "[yellow]⚠ Unknown firmware: opened with the Lawicel 'L' command. A CANable ignores that and stays closed - " +
                "if no frames arrive, the firmware banner in the log is the first thing to check.[/]");
        }

        var vehicle = new VehicleSession(commands);
        AnsiConsole.MarkupLine(
            $"[grey]Capabilities available on a raw CAN source: {string.Join(", ", commands.Capabilities.Select(t => t.Name)).EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine(
            $"[grey]BMS (UDS) supported: {vehicle.Supports<IBatteryManagementSystem>()} - needs transmit + ISO-TP over raw CAN[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(duration > 0
            ? $"[yellow]Capturing for {duration}s...[/]"
            : "[yellow]Capturing until Ctrl+C...[/]");

        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (duration > 0)
        {
            window.CancelAfter(TimeSpan.FromSeconds(duration));
        }

        var counts = new Dictionary<int, int>();
        var samples = new Dictionary<int, int>();
        var total = 0;
        var started = DateTime.UtcNow;
        var lastReport = started;

        try
        {
            await foreach (var frame in monitor.Subscribe(ReadOnlyMemory<int>.Empty, window.Token))
            {
                total++;
                counts[frame.CanId] = counts.GetValueOrDefault(frame.CanId) + 1;

                var seen = samples.GetValueOrDefault(frame.CanId);
                if (seen < 2)
                {
                    samples[frame.CanId] = seen + 1;
                    var hex = Convert.ToHexString(frame.Data.ToArray());
                    Log.Information("SLCAN RAW {CanId} [{Hex}] => {Decoded}",
                        frame.CanIdHex, hex, TryDecode(frame.CanId, frame.Data.Span) ?? "(no decoder)");
                }

                var now = DateTime.UtcNow;
                if (now - lastReport >= TimeSpan.FromSeconds(5))
                {
                    lastReport = now;
                    RenderReport(counts, total, now - started, source, monitor);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Duration elapsed.
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C.
        }

        await monitor.StopAsync(CancellationToken.None);

        var elapsed = DateTime.UtcNow - started;
        RenderReport(counts, total, elapsed, source, monitor);

        if (total == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No frames.[/] Either nothing is on the bus (adapter on the bench, car asleep), the bitrate is wrong, " +
                "or the channel never opened (firmware dialect). The error register may say which:");
            var error = await source.QueryAsync(SlcanProtocol.ErrorRegister, TimeSpan.FromSeconds(1), CancellationToken.None);
            AnsiConsole.MarkupLine($"  [grey]{(error ?? "(no reply - normal for stock CANable firmware when closed)").EscapeMarkup()}[/]");
            Log.Information("SLCAN error register after empty capture: {Reply}", error ?? "(none)");
        }

        Log.Information("Serial CAN session ended: {Total} frames, {Ids} IDs, {Fd} CAN FD, {Chatter} non-frame lines, EndReason={EndReason}",
            total, counts.Count, source.CanFdFrameCount, source.NonFrameLineCount, monitor.EndReason);
    }

    private static void RenderReport(
        Dictionary<int, int> counts, int total, TimeSpan elapsed, SlcanFrameSource source, CanMonitor monitor)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("CAN ID").AddColumn("Frames").AddColumn("Hz").AddColumn("Decoder");

        foreach (var kvp in counts.OrderByDescending(k => k.Value).Take(24))
        {
            var hz = elapsed.TotalSeconds > 0 ? kvp.Value / elapsed.TotalSeconds : 0;
            var decoder = monitor.TryGetLatest(kvp.Key, out var latest)
                ? CanFrameRouter.TryParseAny(kvp.Key, latest.Data.Span)?.GetType().Name ?? "-"
                : "-";
            table.AddRow($"{kvp.Key:X3}", kvp.Value.ToString(), $"{hz:F1}", decoder.EscapeMarkup());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[cyan]{total}[/] frames, [cyan]{counts.Count}[/] IDs in {elapsed.TotalSeconds:F0}s; " +
            $"CAN FD frames: {source.CanFdFrameCount}; non-frame lines: {source.NonFrameLineCount}; monitor: {(monitor.IsRunning ? "running" : monitor.EndReason.ToString())}");

        if (monitor.TryGetLatest<BatteryFrame_1DB_AZE0>(out var battery))
        {
            AnsiConsole.MarkupLine(
                $"[green]EV-CAN visible:[/] 1DB pack {battery.Voltage:F1} V / {battery.Current:F1} A, usable SOC {battery.UsableSoc}%");
        }
    }

    private static string? TryDecode(int canId, ReadOnlySpan<byte> data)
    {
        if (data.Length != 8)
        {
            return $"(len={data.Length}, decoders need 8 bytes)";
        }

        var decoded = CanFrameRouter.TryParseAny(canId, data);
        return decoded is null ? null : $"{decoded.GetType().Name} {JsonSerializer.Serialize(decoded, decoded.GetType())}";
    }

    private static int ParseInt(string[] args, string prefix, int fallback)
    {
        var text = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        return int.TryParse(text, out var value) ? value : fallback;
    }
}
