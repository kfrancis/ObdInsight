using ObdInsight.Core;
using ObdInsight.Core.Adapters;
using ObdInsight.Drivers.Adapters.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Drivers;
using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Commands for OBD diagnostics and vehicle communication.
/// </summary>
public static class DiagnosticCommands
{
    /// <summary>
    /// Run an interactive OBD command loop.
    /// </summary>
    public static async Task RunCommandLoopAsync(DevToolsSession session)
    {
        if (!session.IsConnected || session.Adapter == null)
        {
            if (!await session.ConnectAndInitializeAdapterAsync())
                return;
        }

        var adapter = session.Adapter!;
        var transport = session.Transport!;
        var service = new ObdService(adapter);

        while (transport.IsConnected)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]OBD Command:[/]")
                    .AddChoices(
                        "Send ATZ (Reset)",
                        "Send ATI (Version)",
                        "Send 0100 (Supported PIDs)",
                        "Get RPM",
                        "Get Speed",
                        "Get Coolant Temp",
                        "Send custom command",
                        "Read DTCs",
                        "Back to main menu"
                    ));

            try
            {
                switch (choice)
                {
                    case "Send ATZ (Reset)":
                        await SendAndDisplayAsync(adapter, "ATZ", TimeSpan.FromSeconds(5));
                        break;

                    case "Send ATI (Version)":
                        await SendAndDisplayAsync(adapter, "ATI", TimeSpan.FromSeconds(2));
                        break;

                    case "Send 0100 (Supported PIDs)":
                        await SendAndDisplayAsync(adapter, "0100", TimeSpan.FromSeconds(5));
                        break;

                    case "Get RPM":
                        var rpm = await service.GetRpmAsync();
                        DisplayResult("RPM", rpm, "rpm");
                        break;

                    case "Get Speed":
                        var speed = await service.GetSpeedKphAsync();
                        DisplayResult("Speed", speed, "km/h");
                        break;

                    case "Get Coolant Temp":
                        var temp = await service.GetCoolantTempCelsiusAsync();
                        DisplayResult("Coolant Temp", temp, "°C");
                        break;

                    case "Send custom command":
                        var cmd = AnsiConsole.Ask<string>("Enter command:");
                        await SendAndDisplayAsync(adapter, cmd, TimeSpan.FromSeconds(5));
                        break;

                    case "Read DTCs":
                        var dtcs = await service.GetDtcCodesAsync();
                        if (dtcs.Count > 0)
                        {
                            AnsiConsole.MarkupLine($"[red]Found {dtcs.Count} DTC(s):[/]");
                            foreach (var dtc in dtcs)
                                AnsiConsole.MarkupLine($"  - {dtc}");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[green]No DTCs stored[/]");
                        }
                        break;

                    case "Back to main menu":
                        return;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            }

            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Run vehicle detection and vehicle-specific command loop.
    /// </summary>
    public static async Task RunWithVehicleDetectionAsync(DevToolsSession session)
    {
        if (!session.IsConnected || session.Adapter == null)
        {
            if (!await session.ConnectAndInitializeAdapterAsync())
                return;
        }

        var detector = new VehicleDetectorService();
        VehicleProfileRegistry.RegisterAllProfiles(detector);

        var vehicleService = new VehicleObdService(session.Adapter!, detector: detector);

        var options = new VehicleServiceOptions
        {
            AutoDetectVehicle = true,
            DetectionTimeout = TimeSpan.FromSeconds(30)
        };

        var initialized = await AnsiConsole.Status()
            .StartAsync("Detecting vehicle...", async ctx =>
            {
                return await vehicleService.ConnectAsync(session.Transport!, options);
            });

        if (!initialized)
        {
            AnsiConsole.MarkupLine("[yellow]Failed to initialize vehicle service[/]");
            return;
        }

        var vehicleProfile = vehicleService.VehicleProfile;
        AnsiConsole.MarkupLine($"[green]Detected:[/] {vehicleProfile.Name}");
        AnsiConsole.MarkupLine($"[grey]Protocol:[/] {vehicleProfile.Protocol}");
        AnsiConsole.MarkupLine($"[grey]EV:[/] {(vehicleProfile.IsElectric ? "Yes" : "No")}");

        await RunVehicleCommandLoopAsync(session, vehicleService);
    }

    /// <summary>
    /// Test Nissan Leaf battery data using proprietary CAN addresses.
    /// </summary>
    public static async Task TestNissanLeafBatteryAsync(DevToolsSession session)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Nissan Leaf Battery Test[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Panel(
            """
            [yellow]IMPORTANT: Vehicle Must Be Awake![/]

            The Nissan Leaf must be in one of these states:
            [green]• READY mode[/] - Foot on brake + press start button
            [green]• Actively charging[/] - Plugged in with charge session active

            If the car is off or in ACC mode, the ECUs are asleep
            and will return NO DATA.
            """)
            .Header("[cyan]Prerequisites[/]")
            .Border(BoxBorder.Rounded));

        if (!AnsiConsole.Confirm("[yellow]Is your Leaf in READY mode or charging?[/]"))
        {
            AnsiConsole.MarkupLine("[yellow]Please wake up the vehicle and try again.[/]");
            return;
        }

        if (!session.IsConnected)
        {
            if (!await session.ConnectAsync())
                return;
        }

        var transport = session.Transport!;
        await Task.Delay(1500);

        async Task<string> SendCommandAsync(string cmd, TimeSpan timeout)
        {
            transport.DrainBuffer();
            await transport.WriteAsync(cmd + "\r");
            var response = await transport.ReadUntilAsync(">", timeout);
            return response.Replace(cmd, "").Replace(">", "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        try
        {
            AnsiConsole.MarkupLine("[cyan]Initializing adapter for Leaf BMS...[/]");

            var initCommands = new[]
            {
                ("ATZ", TimeSpan.FromSeconds(5)),
                ("ATE0", TimeSpan.FromSeconds(2)),
                ("ATL0", TimeSpan.FromSeconds(2)),
                ("ATS0", TimeSpan.FromSeconds(2)),
                ("ATH1", TimeSpan.FromSeconds(2)),
                ("ATSP6", TimeSpan.FromSeconds(3)),
                ("ATCAF0", TimeSpan.FromSeconds(2)),
                ("ATFCSH79B", TimeSpan.FromSeconds(2)),
                ("ATFCSD300000", TimeSpan.FromSeconds(2)),
                ("ATFCSM1", TimeSpan.FromSeconds(2)),
                ("ATSH79B", TimeSpan.FromSeconds(2)),
                ("ATCRA7BB", TimeSpan.FromSeconds(2)),
            };

            foreach (var (cmd, timeout) in initCommands)
            {
                var resp = await SendCommandAsync(cmd, timeout);
                AnsiConsole.MarkupLine($"[grey]   {cmd}: {resp.EscapeMarkup()}[/]");
                await Task.Delay(300);
            }

            AnsiConsole.MarkupLine("[green]?[/] BMS communication configured");
            AnsiConsole.WriteLine();

            var batteryCommands = new[]
            {
                ("2101", "BMS Group 01: SOC, Capacity, Current, Voltage"),
                ("2102", "BMS Group 02: Cell Voltages"),
                ("2104", "BMS Group 04: Pack Temperatures"),
            };

            var results = new List<(string Cmd, string Desc, string Response, bool Success)>();

            foreach (var (cmd, desc) in batteryCommands)
            {
                AnsiConsole.MarkupLine($"[cyan]Sending {cmd}[/] ({desc})...");

                try
                {
                    var response = await SendCommandAsync(cmd, TimeSpan.FromSeconds(10));
                    var hasData = !string.IsNullOrWhiteSpace(response) &&
                                  !response.Contains("NO DATA") &&
                                  !response.Contains("ERROR") &&
                                  response.Length > 10;

                    results.Add((cmd, desc, response, hasData));

                    if (hasData)
                    {
                        AnsiConsole.MarkupLine($"[green]? Got {response.Length} chars![/]");
                        var preview = response.Length > 200 ? response[..200] + "..." : response;
                        AnsiConsole.MarkupLine($"[grey]   {preview.EscapeMarkup()}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]? No data: {response.EscapeMarkup()}[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]? Error: {ex.Message.EscapeMarkup()}[/]");
                    results.Add((cmd, desc, ex.Message, false));
                }

                AnsiConsole.WriteLine();
                await Task.Delay(500);
            }

            // Summary
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Command")
                .AddColumn("Description")
                .AddColumn("Status");

            foreach (var (cmd, desc, _, success) in results)
            {
                table.AddRow(cmd, desc, success ? "[green]Success[/]" : "[red]Failed[/]");
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// List all supported vehicle profiles.
    /// </summary>
    public static void ListSupportedVehicles()
    {
        var detector = new VehicleDetectorService();
        VehicleProfileRegistry.RegisterAllProfiles(detector);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Vehicle")
            .AddColumn("Manufacturer")
            .AddColumn("Years")
            .AddColumn("Type")
            .AddColumn("Protocol");

        foreach (var profile in detector.RegisteredProfiles.OrderBy(p => p.Manufacturer).ThenBy(p => p.Model))
        {
            var vehicleType = profile.IsElectric ? "[green]EV[/]" : "[blue]ICE[/]";
            table.AddRow(
                profile.Name,
                profile.Manufacturer,
                profile.SupportedYears.ToString(),
                vehicleType,
                profile.Protocol.ToString()
            );
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Total: {detector.RegisteredProfiles.Count} vehicle profiles[/]");
    }

    private static async Task RunVehicleCommandLoopAsync(DevToolsSession session, VehicleObdService service)
    {
        var profile = service.VehicleProfile;

        while (session.IsConnected)
        {
            var choices = new List<string> { "Get VIN", "Get Speed", "Read DTCs" };

            if (profile.IsElectric)
            {
                choices.InsertRange(1, new[]
                {
                    "Get Battery SOC",
                    "Get Battery SOH",
                    "Get Battery Voltage",
                    "Get Full Battery Info",
                    "Get Range Remaining",
                    "Get Charging Status"
                });
            }
            else
            {
                choices.InsertRange(1, new[]
                {
                    "Get RPM",
                    "Get Coolant Temp",
                    "Get Throttle Position",
                    "Get Fuel Level"
                });
            }

            choices.Add("Back to main menu");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[cyan]{profile.Name} - Command:[/]")
                    .AddChoices(choices));

            try
            {
                switch (choice)
                {
                    case "Get VIN":
                        var vin = await service.GetVinAsync();
                        DisplayResult("VIN", vin);
                        if (vin != null)
                        {
                            var vinInfo = VinInfo.Parse(vin);
                            if (vinInfo != null)
                            {
                                AnsiConsole.MarkupLine($"  [grey]Manufacturer:[/] {vinInfo.Manufacturer ?? "Unknown"}");
                                AnsiConsole.MarkupLine($"  [grey]Country:[/] {vinInfo.Country ?? "Unknown"}");
                            }
                        }
                        break;

                    case "Get Speed":
                        DisplayResult("Speed", await service.GetSpeedKphAsync(), "km/h");
                        break;

                    case "Get RPM":
                        DisplayResult("RPM", await service.GetRpmAsync(), "rpm");
                        break;

                    case "Get Coolant Temp":
                        DisplayResult("Coolant Temp", await service.GetCoolantTempCelsiusAsync(), "°C");
                        break;

                    case "Get Throttle Position":
                        DisplayResult("Throttle", await service.GetThrottlePositionPercentAsync(), "%");
                        break;

                    case "Get Fuel Level":
                        DisplayResult("Fuel Level", await service.GetFuelLevelPercentAsync(), "%");
                        break;

                    case "Get Battery SOC":
                        DisplayResult("Battery SOC", await service.GetBatterySocAsync(), "%");
                        break;

                    case "Get Battery SOH":
                        DisplayResult("Battery SOH", await service.GetBatterySohAsync(), "%");
                        break;

                    case "Get Battery Voltage":
                        DisplayResult("Battery Voltage", await service.GetBatteryVoltageAsync(), "V");
                        break;

                    case "Get Range Remaining":
                        DisplayResult("Range", await service.GetRangeRemainingAsync(), "km");
                        break;

                    case "Get Charging Status":
                        DisplayResult("Charging Status", await service.GetChargingStatusAsync());
                        break;

                    case "Get Full Battery Info":
                        var info = await service.GetBatteryInfoAsync();
                        if (info != null)
                        {
                            var table = new Table()
                                .Border(TableBorder.Rounded)
                                .AddColumn("Property")
                                .AddColumn("Value");

                            table.AddRow("State of Charge", $"{info.StateOfCharge:F1}%");
                            table.AddRow("State of Health", $"{info.StateOfHealth:F1}%");
                            table.AddRow("Voltage", $"{info.Voltage:F1} V");
                            table.AddRow("Current", $"{info.Current:F1} A");
                            table.AddRow("Power", $"{info.PowerKw:F2} kW");
                            table.AddRow("Temperature", $"{info.Temperature:F1} °C");
                            table.AddRow("Is Charging", info.IsCharging ? "[green]Yes[/]" : "[grey]No[/]");

                            AnsiConsole.Write(table);
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[yellow]Could not retrieve battery info[/]");
                        }
                        break;

                    case "Read DTCs":
                        var dtcs = await service.GetDtcCodesAsync();
                        if (dtcs.Count > 0)
                        {
                            AnsiConsole.MarkupLine($"[red]Found {dtcs.Count} DTC(s):[/]");
                            foreach (var dtc in dtcs)
                                AnsiConsole.MarkupLine($"  - {dtc}");
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[green]No DTCs stored[/]");
                        }
                        break;

                    case "Back to main menu":
                        return;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            }

            AnsiConsole.WriteLine();
        }
    }

    private static async Task SendAndDisplayAsync(Elm327Adapter adapter, string command, TimeSpan timeout)
    {
        var response = await adapter.SendCommandAsync(new ObdCommand(command, timeout));

        var panel = new Panel(response.RawResponse?.EscapeMarkup() ?? "[grey]<empty>[/]")
            .Header(response.Success ? "[green]Response[/]" : "[red]Error[/]")
            .Border(BoxBorder.Rounded);

        AnsiConsole.Write(panel);

        if (!response.Success && response.Error != null)
        {
            AnsiConsole.MarkupLine($"[red]Error: {response.Error}[/]");
        }
    }

    private static void DisplayResult<T>(string label, T? value, string unit = "")
    {
        if (value != null)
        {
            var unitStr = string.IsNullOrEmpty(unit) ? "" : $" {unit}";
            AnsiConsole.MarkupLine($"[green]{label}:[/] {value}{unitStr}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Could not read {label}[/]");
        }
    }
}
