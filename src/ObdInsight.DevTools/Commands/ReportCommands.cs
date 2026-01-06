using ObdInsight.Drivers.Adapters.Elm327;
using ObdInsight.Core.Diagnostics;
using ObdInsight.Core.Transports.Ble;
using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Commands for generating vehicle support reports.
/// </summary>
public static class ReportCommands
{
    /// <summary>
    /// Generate a comprehensive vehicle support report for submitting to GitHub.
    /// </summary>
    public static async Task GenerateVehicleSupportReportAsync(DevToolsSession session)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Vehicle Support Report Generator[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[yellow]This tool collects diagnostic data to help add support for new vehicles.[/]");
        AnsiConsole.MarkupLine("[grey]The report will be saved as a markdown file suitable for GitHub issues.[/]");
        AnsiConsole.WriteLine();

        if (string.IsNullOrEmpty(session.DeviceAddress))
        {
            AnsiConsole.MarkupLine("[red]No device selected. Please scan or set a device first.[/]");
            return;
        }

        // Step 1: Get user vehicle info
        var userInfo = CollectUserVehicleInfo();

        var isEv = userInfo.EngineType?.Contains("Electric") == true ||
                   userInfo.EngineType?.Contains("BEV") == true ||
                   userInfo.EngineType?.Contains("Hybrid") == true;

        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("[yellow]Ready to start diagnostic collection. Continue?[/]"))
            return;

        if (isEv)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(
                """
                [yellow]IMPORTANT for Electric Vehicles:[/]

                The vehicle must be:
                [green]• Ignition ON (READY mode)[/] - Press start button with foot on brake
                [green]• OR Actively charging[/] - Plugged in and charge session active

                ECUs are asleep when the car is off or in ACC mode.
                """)
                .Header("[cyan]Vehicle Wake State[/]")
                .Border(BoxBorder.Rounded));

            if (!AnsiConsole.Confirm("[yellow]Is your vehicle in READY mode or actively charging?[/]"))
            {
                AnsiConsole.MarkupLine("[yellow]Please turn on the vehicle and try again.[/]");
                return;
            }
        }

        var collector = new DiagnosticDataCollector();
        BleAdapterInfo? bleInfo = null;
        ObdAdapterInfo? obdAdapterInfo = null;
        VehicleIdentification? vehicleId = null;
        SupportedPidsInfo? supportedPids = null;
        WindowsBleTransport? transport = null;
        Elm327Adapter? adapter = null;

        var progress = new Progress<DiagnosticProgress>(p =>
        {
            var statusColor = p.LastOperationSuccess switch
            {
                true => "green",
                false => "red",
                null => "grey"
            };

            var phaseIcon = p.Phase switch
            {
                DiagnosticPhase.Complete => "[green]?[/]",
                DiagnosticPhase.Failed => "[red]?[/]",
                _ => "[cyan]>[/]"
            };

            var escapedMessage = p.Message?.EscapeMarkup() ?? "";
            var progressPct = (p.OverallProgress * 100).ToString("F0");
            var itemProgress = p.ItemsTotal > 0 ? $"({p.ItemsCompleted}/{p.ItemsTotal})" : "";

            try
            {
                AnsiConsole.MarkupLine($"{phaseIcon} [{statusColor}]{escapedMessage}[/] [grey]{itemProgress} ({progressPct}%)[/]");

                if (!string.IsNullOrEmpty(p.LastResponse) && p.CurrentItem != null && p.LastOperationSuccess == true)
                {
                    var truncated = p.LastResponse.Length > 60 ? p.LastResponse[..57] + "..." : p.LastResponse;
                    var escaped = truncated.Replace("\r", "").Replace("\n", " ").EscapeMarkup();
                    AnsiConsole.MarkupLine($"   [grey]? {escaped}[/]");
                }
            }
            catch
            {
                Console.WriteLine($"> {p.Message} {itemProgress} ({progressPct}%)");
            }
        });

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Starting Collection[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var profile = session.Profile ?? BleDeviceProfile.VeepeakBle;
        var macAddress = session.DeviceAddress!;

        // Helper to ensure connection
        async Task<bool> EnsureConnectedAsync()
        {
            if (transport?.IsConnected == true)
            {
                if (await ValidateConnectionAsync())
                    return true;
            }

            AnsiConsole.MarkupLine("[yellow]Reconnecting...[/]");

            if (transport != null)
            {
                try { await transport.DisconnectAsync(); } catch { }
                transport.Dispose();
            }

            await Task.Delay(3000);

            transport = new WindowsBleTransport(profile);
            transport.DataSent += (_, data) => LogTraffic("TX", data);
            transport.DataReceived += (_, data) => LogTraffic("RX", data);

            var connected = await transport.ConnectAsync(macAddress);
            if (!connected)
            {
                AnsiConsole.MarkupLine("[red]Failed to reconnect![/]");
                return false;
            }

            await Task.Delay(1500);

            var reinitOk = await MinimalAdapterInitAsync();
            if (!reinitOk)
            {
                AnsiConsole.MarkupLine("[red]Adapter init failed[/]");
                return false;
            }

            AnsiConsole.MarkupLine("[green]Reconnected![/]");
            return transport.IsConnected;
        }

        async Task<bool> ValidateConnectionAsync()
        {
            if (transport == null || !transport.IsConnected)
                return false;

            try
            {
                transport.DrainBuffer();
                await transport.WriteAsync("ATI\r");
                var response = await transport.ReadUntilAsync(">", TimeSpan.FromSeconds(6));
                return !string.IsNullOrWhiteSpace(response) && response.Contains("ELM", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        async Task<bool> MinimalAdapterInitAsync()
        {
            if (transport == null || !transport.IsConnected)
                return false;

            try
            {
                async Task<(bool Success, string Response)> SendAsync(string cmd, TimeSpan timeout)
                {
                    try
                    {
                        transport.DrainBuffer();
                        await transport.WriteAsync(cmd + "\r");
                        var response = await transport.ReadUntilAsync(">", timeout);
                        response = response.Replace(cmd, "").Replace(">", "").Replace("\r", "").Trim();
                        var success = !string.IsNullOrWhiteSpace(response) &&
                                     !response.Contains("?") &&
                                     !response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
                        return (success, response);
                    }
                    catch
                    {
                        return (false, "");
                    }
                }

                await SendAsync("ATZ", TimeSpan.FromSeconds(8));
                await Task.Delay(800);

                var (atiOk, _) = await SendAsync("ATI", TimeSpan.FromSeconds(8));
                if (!atiOk) return false;

                foreach (var cmd in new[] { "ATE0", "ATL0", "ATS0", "ATH0" })
                {
                    var (ok, _) = await SendAsync(cmd, TimeSpan.FromSeconds(6));
                    if (!ok) return false;
                    await Task.Delay(300);
                }

                await SendAsync("ATSP6", TimeSpan.FromSeconds(8));
                await Task.Delay(300);

                adapter = new Elm327Adapter();
                adapter.Log += (_, e) => LogAdapter(e);
                adapter.SetTransport(transport, markAsInitialized: true);

                return true;
            }
            catch
            {
                return false;
            }
        }

        try
        {
            // Phase 1: Connect
            AnsiConsole.MarkupLine("[cyan]Connecting to OBD adapter...[/]");

            transport = new WindowsBleTransport(profile);
            transport.DataSent += (_, data) => LogTraffic("TX", data);
            transport.DataReceived += (_, data) => LogTraffic("RX", data);

            var connected = await transport.ConnectAsync(macAddress);
            if (!connected)
            {
                AnsiConsole.MarkupLine("[red]? Failed to connect![/]");
                collector.AddError("Connection", "Failed to establish BLE connection");
                goto GenerateReport;
            }

            AnsiConsole.MarkupLine("[green]?[/] Connected");

            bleInfo = new BleAdapterInfo
            {
                DeviceName = session.DeviceName ?? profile.Name,
                MacAddress = macAddress,
                Services = []
            };

            await Task.Delay(2000);

            // Phase 2: Initialize adapter
            AnsiConsole.MarkupLine("[cyan]Initializing adapter...[/]");

            if (isEv)
            {
                var minimalInit = await MinimalAdapterInitAsync();
                if (!minimalInit && !await EnsureConnectedAsync())
                    goto GenerateReport;
            }
            else
            {
                adapter = new Elm327Adapter();
                adapter.Log += (_, e) => LogAdapter(e);
                await adapter.InitializeAsync(transport);
            }

            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 3: Collect adapter info
            AnsiConsole.MarkupLine("[cyan]Collecting adapter info...[/]");
            obdAdapterInfo = await collector.CollectObdAdapterInfoAsync(adapter!, progress);

            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 4: Probe protocols
            AnsiConsole.MarkupLine("[cyan]Probing protocols...[/]");
            await collector.ProbeProtocolsAsync(adapter!, progress);

            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 5: Vehicle ID
            AnsiConsole.MarkupLine("[cyan]Reading vehicle ID...[/]");
            vehicleId = await collector.CollectVehicleIdAsync(adapter!, progress);

            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 6: Supported PIDs
            AnsiConsole.MarkupLine("[cyan]Querying supported PIDs...[/]");
            supportedPids = await collector.CollectSupportedPidsAsync(adapter!, progress);

            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 7: Probe standard PIDs
            AnsiConsole.MarkupLine("[cyan]Probing standard PIDs...[/]");
            await collector.ProbeStandardPidsAsync(adapter!, supportedPids, progress);

            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 8: Extended PIDs
            AnsiConsole.MarkupLine("[cyan]Probing extended PIDs...[/]");
            await collector.ProbeExtendedPidsAsync(adapter!, progress);

            // Phase 9: EV CAN probing
            if (isEv)
            {
                if (!await EnsureConnectedAsync())
                    goto GenerateReport;

                AnsiConsole.MarkupLine("[cyan]Probing EV CAN addresses...[/]");
                await collector.ProbeEvCanAddressesAsync(adapter!, userInfo.Make, progress);
            }

            if (transport?.IsConnected == true)
                await transport.DisconnectAsync();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
            collector.AddError("Collection", ex.Message, ex.ToString());
        }
        finally
        {
            if (transport != null)
            {
                try { await transport.DisconnectAsync(); } catch { }
                transport.Dispose();
            }
        }

    GenerateReport:
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Generating Report[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var report = collector.BuildReport(userInfo, bleInfo, obdAdapterInfo, vehicleId, supportedPids);
        var markdown = MarkdownReportGenerator.Generate(report);

        var reportsDir = Path.Combine(Environment.CurrentDirectory, "Reports");
        Directory.CreateDirectory(reportsDir);

        var fileName = $"vehicle_report_{userInfo.Year}_{userInfo.Make}_{userInfo.Model}_{DateTime.Now:yyyyMMdd_HHmmss}.md"
            .Replace(" ", "_")
            .Replace("/", "-");

        var filePath = Path.Combine(reportsDir, fileName);
        await File.WriteAllTextAsync(filePath, markdown);

        AnsiConsole.MarkupLine($"[green]?[/] Report saved to: [cyan]{filePath.EscapeMarkup()}[/]");

        if (AnsiConsole.Confirm("Open the report file now?"))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                AnsiConsole.MarkupLine("[yellow]Could not open file automatically.[/]");
            }
        }
    }

    private static UserVehicleInfo CollectUserVehicleInfo()
    {
        AnsiConsole.MarkupLine("[cyan]Please enter your vehicle information:[/]");
        AnsiConsole.WriteLine();

        var year = AnsiConsole.Prompt(
            new TextPrompt<int>("[cyan]Vehicle Year:[/]")
                .DefaultValue(DateTime.Now.Year)
                .Validate(y => y >= 1996 && y <= DateTime.Now.Year + 1
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Year must be between 1996 and current year")));

        var make = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Make (e.g., Honda, Toyota, Nissan):[/]")
                .Validate(m => !string.IsNullOrWhiteSpace(m)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Make is required")));

        var model = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Model (e.g., CR-V, Camry, Leaf):[/]")
                .Validate(m => !string.IsNullOrWhiteSpace(m)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Model is required")));

        var trim = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Trim (optional):[/]")
                .AllowEmpty());

        var engineType = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Engine/Powertrain Type:[/]")
                .AddChoices(
                    "Gasoline",
                    "Diesel",
                    "Hybrid",
                    "Plug-in Hybrid (PHEV)",
                    "Electric (BEV)",
                    "Other/Unknown"));

        var transmission = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Transmission Type:[/]")
                .AddChoices(
                    "Automatic",
                    "CVT",
                    "Manual",
                    "Dual-Clutch (DCT)",
                    "Single-Speed (EV)",
                    "Other/Unknown"));

        var notes = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Additional Notes (optional):[/]")
                .AllowEmpty());

        return new UserVehicleInfo
        {
            Year = year,
            Make = make.Trim(),
            Model = model.Trim(),
            Trim = string.IsNullOrWhiteSpace(trim) ? null : trim.Trim(),
            EngineType = engineType,
            TransmissionType = transmission,
            AdditionalNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        };
    }

    private static void LogTraffic(string direction, string data)
    {
        var escaped = data.Replace("\r", "\\r").Replace("\n", "\\n").Replace(">", ">");
        var color = direction == "TX" ? "blue" : "green";
        AnsiConsole.MarkupLine($"[grey]BLE[/] [{color}]{direction}[/]: [white]{escaped.EscapeMarkup()}[/]");
    }

    private static void LogAdapter(Elm327LogEventArgs e)
    {
        var color = e.Level switch
        {
            Elm327LogLevel.Debug => "grey",
            Elm327LogLevel.Info => "cyan",
            Elm327LogLevel.Warning => "yellow",
            Elm327LogLevel.Error => "red",
            _ => "white"
        };
        AnsiConsole.MarkupLine($"[grey]ELM[/] [{color}]{e.Level}[/]: {e.Message.EscapeMarkup()}");
    }
}
