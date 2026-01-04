using ObdInsight.Core.Transports.Ble;
using ObdInsight.DevTools.Commands;
using Spectre.Console;

namespace ObdInsight.DevTools;

internal class Program
{
    private static async Task Main(string[] args)
    {
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
                choices.Add("── Quick Connect ──");
                foreach (var device in recentDevices)
                {
                    var star = device.IsFavorite ? "★ " : "";
                    choices.Add($"Connect to {star}{device.Name}");
                }
            }

            // Device selection section
            choices.Add("── Device Selection ──");
            choices.Add("Scan for BLE devices");
            choices.Add("Set device address manually");
            choices.Add("Discover device services");
            choices.Add("Read all characteristics");
            if (recentDevices.Count > 0)
            {
                choices.Add("Manage saved devices");
            }

            // Connection section (only if device selected)
            if (!string.IsNullOrEmpty(session.DeviceAddress))
            {
                choices.Add("── Connection ──");
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
                choices.Add("── Diagnostics ──");
                choices.Add("Device information");
                choices.Add("OBD command console");
                choices.Add("Vehicle detection mode");
                choices.Add("Nissan Leaf diagnostics (OVMS-style)");
                choices.Add("Nissan Leaf interactive");
                choices.Add("Nissan Leaf battery health assessment");
                choices.Add("Test binary protocol (Service 6287)");
            }

            // Tools section
            choices.Add("── Tools ──");
            if (!string.IsNullOrEmpty(session.DeviceAddress))
            {
                choices.Add("Record OBD session");
                choices.Add("Generate vehicle support report");
            }
            choices.Add("List supported vehicles");
            choices.Add("Show BLE profiles");

            // Exit
            choices.Add("──────────");
            choices.Add("Exit");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an option:[/]")
                    .HighlightStyle(new Style(Color.Cyan1))
                    .AddChoices(choices)
                    .PageSize(25));

            // Skip separator lines
            if (choice.StartsWith("──"))
            {
                continue;
            }

            AnsiConsole.WriteLine();

            try
            {
                // Handle quick connect options
                if (choice.StartsWith("Connect to "))
                {
                    var deviceName = choice["Connect to ".Length..].TrimStart('★', ' ');
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

                    case "Read all characteristics":
                        await DeviceCommands.ReadAllCharacteristicsAsync(session);
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
                    case "Device information":
                        await DeviceCommands.ShowDeviceInfoAsync(session);
                        break;

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

                    case "Nissan Leaf battery health assessment":
                        await LeafBatteryHealthCommand.RunAsync(session);
                        break;

                    case "Test binary protocol (Service 6287)":
                        await BinaryProtocolTest.RunAsync(session);
                        break;

                    // Tools
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
                    device.IsFavorite ? "[yellow]★[/]" : "[grey]-[/]"
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
                        : $"[yellow]★[/] Added {favDevice.Name} to favorites");
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
                    if (AnsiConsole.Confirm("[red]Remove ALL saved devices?[/]", defaultValue: false))
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