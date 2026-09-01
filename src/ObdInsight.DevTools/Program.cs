using ObdInsight.Core.Communication.Bluetooth;
using ObdInsight.DevTools.Commands;
using Spectre.Console;

namespace ObdInsight.DevTools;

internal class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Headless subcommand, checked before any banner or menu so the process can be driven
        // over SSH from a development machine while the laptop sits in the car.
        if (args.Length > 0 && args[0].Equals("capture", StringComparison.OrdinalIgnoreCase))
        {
            return await RunHeadlessCaptureAsync(args);
        }

        if (args.Length > 0 && args[0].Equals("scan", StringComparison.OrdinalIgnoreCase))
        {
            return await RunHeadlessScanAsync(args);
        }

        // Offline: no vehicle, no adapter. Pure function over a recorded capture directory.
        if (args.Length > 0 && args[0].Equals("analyze", StringComparison.OrdinalIgnoreCase))
        {
            return RunAnalyze(args);
        }

        // Offline: compares compiled [CanSignal] definitions against the DBCs they came from.
        if (args.Length > 0 && args[0].Equals("dbc-audit", StringComparison.OrdinalIgnoreCase))
        {
            return DbcAudit.Run(args);
        }

        // Offline: settles the byte-order disputes that audit reports, using captured frames.
        if (args.Length > 0 && args[0].Equals("dbc-crosscheck", StringComparison.OrdinalIgnoreCase))
        {
            return DbcAudit.CrossReference(args);
        }

        // Offline: checks every declared signal against captured data using its own declared range.
        if (args.Length > 0 && args[0].Equals("signal-sanity", StringComparison.OrdinalIgnoreCase))
        {
            return DbcAudit.SignalSanity(args);
        }

        AnsiConsole.Write(new FigletText("OBD DevTools").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]BLE OBD-II Development Tool[/]");
        AnsiConsole.WriteLine();

        // Create the session that persists across menu operations
        await using var session = new DevToolsSession();

        // Check for command line device argument
        if (args.Length > 0 && !string.IsNullOrEmpty(args[0]))
        {
            var mac = args[0].Replace(":", "").Replace("-", "");
            if (mac.Length == 12 && mac.All(c => Uri.IsHexDigit(c)))
            {
                session.SetDevice(args[0], args.Length > 1 ? args[1] : null);
                AnsiConsole.MarkupLine($"[cyan]Device set from command line:[/] {session.DeviceAddress}");
                AnsiConsole.WriteLine();
            }
        }

        await RunMainMenuAsync(session);
        return 0;
    }

    /// <summary>
    ///     <c>ObdInsight.DevTools.exe analyze &lt;capture-dir&gt; [&lt;capture-dir&gt; ...]</c>
    ///     Correlates guided-probe captures offline and prints the bits that tracked each stimulus.
    ///     Needs no vehicle and no adapter, so the scoring can be iterated on at a desk.
    /// </summary>
    private static int RunAnalyze(string[] args)
    {
        var dirs = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (dirs.Count == 0)
        {
            Console.Error.WriteLine("usage: ObdInsight.DevTools.exe analyze <capture-dir> [<capture-dir> ...]");
            return 2;
        }

        var failed = false;

        foreach (var dir in dirs)
        {
            try
            {
                var session = ProbeAnalyzer.Load(dir);
                var findings = ProbeAnalyzer.Analyze(session, out var header);
                Console.Out.Write(ProbeAnalyzer.Format(session, findings, header));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {dir}: {ex.Message}");
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    ///     <c>ObdInsight.DevTools.exe scan [--seconds 10]</c>
    ///     Lists nearby BLE devices as tab-separated <c>address name rssi</c> on stdout, strongest
    ///     first, so the adapter's MAC can be discovered remotely instead of by driving the
    ///     interactive menu on a laptop that is sitting in a car.
    /// </summary>
    private static async Task<int> RunHeadlessScanAsync(string[] args)
    {
        var seconds = 10;

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--seconds", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out seconds) || seconds <= 0)
                {
                    Console.Error.WriteLine("error: --seconds must be a positive integer.");
                    return 2;
                }
            }
            else
            {
                Console.Error.WriteLine($"error: unrecognised argument '{args[i]}'. usage: scan [--seconds <n>]");
                return 2;
            }
        }

        var devices = new Dictionary<string, BleDeviceInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var scanner = new WindowsBleScanner();
            scanner.DeviceDiscovered += (_, e) => devices[e.Device.Address] = e.Device;

            Console.Error.WriteLine($"scanning for {seconds}s...");
            await scanner.StartScanAsync();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            await scanner.StopScanAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: scan failed: {ex.Message}");
            return 1;
        }

        if (devices.Count == 0)
        {
            Console.Error.WriteLine(
                "no BLE devices found. The adapter only advertises once it has power - check it is " +
                "plugged into the OBD port and its LED is lit.");
            return 1;
        }

        // Tab-separated so the caller can cut/parse it; strongest signal first, since an adapter
        // in the same vehicle as the laptop should sort near the top.
        foreach (var d in devices.Values.OrderByDescending(d => d.Rssi))
        {
            var name = string.IsNullOrWhiteSpace(d.Name) ? "(unnamed)" : d.Name;
            Console.Out.WriteLine($"{d.Address}\t{name}\t{d.Rssi}");
        }

        Console.Error.WriteLine($"{devices.Count} device(s).");
        return 0;
    }

    /// <summary>
    ///     <c>
    ///         ObdInsight.DevTools.exe capture --device &lt;mac&gt; --bus EV-CAN --seconds 60
    ///         [--out DIR] [--markers FILE]
    ///     </c>
    ///     No prompts, no live table, no keyboard. Progress and diagnostics go to stderr; on success
    ///     the summary JSON path is the only thing written to stdout, so a caller can consume it
    ///     directly. Exit code: 0 success, 2 bad arguments, 1 failure, 130 cancelled.
    /// </summary>
    private static async Task<int> RunHeadlessCaptureAsync(string[] args)
    {
        string? device = null, bus = null, output = null, markers = null;
        var seconds = 0;
        var verbose = false;

        for (var i = 1; i < args.Length; i++)
        {
            var isLast = i + 1 >= args.Length;
            switch (args[i].ToLowerInvariant())
            {
                case "--device" when !isLast: device = args[++i]; break;
                case "--bus" when !isLast: bus = args[++i]; break;
                case "--out" when !isLast: output = args[++i]; break;
                case "--markers" when !isLast: markers = args[++i]; break;
                case "--verbose": verbose = true; break;
                case "--seconds" when !isLast:
                    if (!int.TryParse(args[++i], out seconds))
                    {
                        Console.Error.WriteLine("error: --seconds must be an integer.");
                        return 2;
                    }

                    break;
                case "--help":
                case "-h":
                    Console.Error.WriteLine(
                        "usage: ObdInsight.DevTools.exe capture --device <mac> --bus <label> --seconds <n> [--out <dir>] [--markers <file>] [--verbose]");
                    return 2;
                default:
                    Console.Error.WriteLine($"error: unrecognised argument '{args[i]}'.");
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(device))
        {
            Console.Error.WriteLine("error: --device <mac> is required.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(bus))
        {
            Console.Error.WriteLine(
                "error: --bus <label> is required (it records which bus the adapter was wired to).");
            return 2;
        }

        await using var session = new DevToolsSession
        {
            // Per-chunk BLE traffic logging would swamp an SSH pipe.
            EnableTrafficLogging = false,
            // Keeps status chatter off stdout, which carries only the summary JSON path.
            SuppressTrafficLogging = true
        };
        session.SetDevice(device);

        return await RawCaptureCommand.RunHeadlessAsync(
            session,
            new RawCaptureOptions
            {
                BusLabel = bus,
                DurationSeconds = seconds,
                OutputRoot = output ?? RawCaptureCommand.DefaultOutputRoot(),
                Headless = true,
                MarkerFilePath = markers,
                Verbose = verbose
            });
    }

    private static async Task RunMainMenuAsync(DevToolsSession session)
    {
        while (true)
        {
            // Show current device status
            AnsiConsole.MarkupLine($"[grey]Status:[/] {session.GetStatusDisplay()}");
            AnsiConsole.WriteLine();

            var choices = new List<string>();

            // Quick connect section - show recent/favorite devices first
            var recentDevices = session.DeviceHistory.GetOrderedDevices().Take(5).ToList();
            if (recentDevices.Count > 0)
            {
                choices.Add("-- Quick Connect --");
                foreach (var device in recentDevices)
                {
                    var star = device.IsFavorite ? "? " : "";
                    choices.Add($"Connect to {star}{device.Name}");
                }
            }

            // Device selection section
            choices.Add("-- Device Selection --");
            choices.Add("Scan for BLE devices");
            choices.Add("Set device address manually");
            if (!string.IsNullOrEmpty(session.DeviceAddress))
            {
                choices.Add("Discover device services");
            }

            if (recentDevices.Count > 0)
            {
                choices.Add("Manage saved devices");
            }

            // Connection section (only if device selected)
            if (!string.IsNullOrEmpty(session.DeviceAddress))
            {
                choices.Add("-- Connection --");
                if (!session.IsConnected)
                {
                    choices.Add("Connect to device");
                }
                else
                {
                    choices.Add("Disconnect");
                }
            }

            // Diagnostics section (only if device selected)
            if (!string.IsNullOrEmpty(session.DeviceAddress))
            {
                choices.Add("-- Diagnostics --");
                choices.Add("OBD command console");
                choices.Add("Vehicle detection mode");
                choices.Add("Nissan Leaf diagnostics (OVMS-style)");
                choices.Add("Nissan Leaf interactive");
                choices.Add("Test binary protocol (Service 6287)");
            }

            // Tools section
            choices.Add("-- Tools --");
            if (!string.IsNullOrEmpty(session.DeviceAddress))
            {
                choices.Add("Raw CAN capture (unfiltered)");
                choices.Add("Guided stimulus probes");
                choices.Add("Record OBD session");
                choices.Add("Generate vehicle support report");
            }

            choices.Add("List supported vehicles");
            choices.Add("Show BLE profiles");

            // Exit
            choices.Add("----------");
            choices.Add("Exit");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an option:[/]")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoices(choices)
                    .PageSize(25));

            // Skip separator lines
            if (choice.StartsWith("--"))
            {
                continue;
            }

            AnsiConsole.WriteLine();

            try
            {
                // Handle quick connect options
                if (choice.StartsWith("Connect to "))
                {
                    var deviceName = choice["Connect to ".Length..].TrimStart('?', ' ');
                    var device = recentDevices.FirstOrDefault(d =>
                        d.Name == deviceName || choice.Contains(d.Name));

                    if (device != null)
                    {
                        // Determine profile from saved profile name
                        var profile = BleDeviceProfile.AllProfiles
                                          .FirstOrDefault(p => p.Name == device.ProfileName)
                                      ?? BleDeviceProfile.VeepeakBle;

                        session.SetDevice(device.Address, device.Name, profile);
                        await session.ConnectAndInitializeAdapterAsync();
                    }

                    continue;
                }

                switch (choice)
                {
                    // Device selection
                    case "Scan for BLE devices":
                        await DeviceCommands.ScanAndSelectDeviceAsync(session);
                        break;

                    case "Set device address manually":
                        DeviceCommands.SetDeviceAddress(session);
                        break;

                    case "Discover device services":
                        await DeviceCommands.DiscoverServicesAsync(session);
                        break;

                    case "Manage saved devices":
                        await ManageSavedDevicesAsync(session);
                        break;

                    // Connection
                    case "Connect to device":
                        await session.ConnectAndInitializeAdapterAsync();
                        break;

                    case "Disconnect":
                        await session.DisconnectAsync();
                        break;

                    // Diagnostics
                    case "OBD command console":
                        await DiagnosticCommands.RunCommandLoopAsync(session);
                        break;

                    case "Vehicle detection mode":
                        await DiagnosticCommands.RunWithVehicleDetectionAsync(session);
                        break;

                    case "Nissan Leaf diagnostics (OVMS-style)":
                        await NissanLeafCommands.RunLeafDiagnosticsAsync(session);
                        break;

                    case "Nissan Leaf interactive":
                        await NissanLeafCommands.RunInteractiveAsync(session);
                        break;

                    case "Test binary protocol (Service 6287)":
                        await BinaryProtocolTest.RunAsync(session);
                        break;

                    // Tools
                    case "Raw CAN capture (unfiltered)":
                        await RawCaptureCommand.RunAsync(session);
                        break;


                    case "Guided stimulus probes":
                        await RawCaptureCommand.RunGuidedAsync(session);
                        break;

                    case "Record OBD session":
                        await RecordingCommands.RecordSessionAsync(session);
                        break;

                    case "Generate vehicle support report":
                        await ReportCommands.GenerateVehicleSupportReportAsync(session);
                        break;

                    case "List supported vehicles":
                        DiagnosticCommands.ListSupportedVehicles();
                        break;

                    case "Show BLE profiles":
                        DeviceCommands.ShowKnownProfiles();
                        break;

                    // Exit
                    case "Exit":
                        await session.DisconnectAsync();
                        return;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
            }

            AnsiConsole.WriteLine();
        }
    }

    private static async Task ManageSavedDevicesAsync(DevToolsSession session)
    {
        while (true)
        {
            var devices = session.DeviceHistory.GetOrderedDevices().ToList();

            if (devices.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No saved devices.[/]");
                return;
            }

            // Display device table
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("#")
                .AddColumn("Name")
                .AddColumn("Address")
                .AddColumn("Profile")
                .AddColumn("Last Used")
                .AddColumn("Uses")
                .AddColumn("Favorite");

            var index = 1;
            foreach (var device in devices)
            {
                table.AddRow(
                    index.ToString(),
                    device.Name.EscapeMarkup(),
                    $"[cyan]{device.Address}[/]",
                    device.ProfileName ?? "[grey]default[/]",
                    device.LastUsed.ToLocalTime().ToString("g"),
                    device.UseCount.ToString(),
                    device.IsFavorite ? "[yellow]?[/]" : "[grey]-[/]"
                );
                index++;
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            var choices = new List<string> { "Toggle favorite", "Remove device", "Clear all", "Back" };
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Manage devices:[/]")
                    .AddChoices(choices));

            switch (choice)
            {
                case "Toggle favorite":
                    var favNum = AnsiConsole.Prompt(
                        new TextPrompt<int>("[cyan]Enter device number to toggle favorite:[/]")
                            .Validate(n => n >= 1 && n <= devices.Count
                                ? ValidationResult.Success()
                                : ValidationResult.Error($"Enter 1-{devices.Count}")));
                    var favDevice = devices[favNum - 1];
                    session.DeviceHistory.SetFavorite(favDevice.Address, !favDevice.IsFavorite);
                    AnsiConsole.MarkupLine(favDevice.IsFavorite
                        ? $"[grey]Removed {favDevice.Name} from favorites[/]"
                        : $"[yellow]?[/] Added {favDevice.Name} to favorites");
                    break;

                case "Remove device":
                    var delNum = AnsiConsole.Prompt(
                        new TextPrompt<int>("[cyan]Enter device number to remove:[/]")
                            .Validate(n => n >= 1 && n <= devices.Count
                                ? ValidationResult.Success()
                                : ValidationResult.Error($"Enter 1-{devices.Count}")));
                    var delDevice = devices[delNum - 1];
                    if (AnsiConsole.Confirm($"Remove {delDevice.Name}?"))
                    {
                        session.DeviceHistory.Remove(delDevice.Address);
                        AnsiConsole.MarkupLine($"[grey]Removed {delDevice.Name}[/]");
                    }

                    break;

                case "Clear all":
                    if (AnsiConsole.Confirm("[red]Remove ALL saved devices?[/]", false))
                    {
                        foreach (var d in devices)
                            session.DeviceHistory.Remove(d.Address);
                        AnsiConsole.MarkupLine("[grey]All devices cleared[/]");
                        return;
                    }

                    break;

                case "Back":
                    return;
            }

            AnsiConsole.WriteLine();
        }
    }
}
