using ObdInsight.Core;
using ObdInsight.Core.Adapters;
using ObdInsight.Core.Adapters.Elm327;
using ObdInsight.Core.Diagnostics;
using ObdInsight.Core.Transports.Ble;
using ObdInsight.Core.Transports.Tracing;
using ObdInsight.Core.Vehicles;
using ObdInsight.Drivers;
using Spectre.Console;

namespace ObdInsight.DevTools;

internal class Program
{
    private const string TargetMacAddress = "66:1e:87:02:c2:db";

    private static async Task Main(string[] args)
    {
        AnsiConsole.Write(new FigletText("OBD DevTools").Color(Color.Cyan1));
        AnsiConsole.MarkupLine("[grey]BLE OBD-II Development Tool[/]");
        AnsiConsole.WriteLine();

        while (true)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an option:[/]")
                    .AddChoices(
                        "Scan for BLE devices",
                        "Connect to Veepeak (66:1e:87:02:c2:db)",
                        "Connect to custom device",
                        "Connect with vehicle detection",
                        "Test Binary Protocol (Service 6287)",
                        "Test Nissan Leaf Battery Data",
                        "Record OBD session",
                        "Generate Vehicle Support Report",
                        "List supported vehicles",
                        "Discover device services",
                        "Exit"
                    ));

            switch (choice)
            {
                case "Scan for BLE devices":
                    await ScanForDevicesAsync();
                    break;

                case "Connect to Veepeak (66:1e:87:02:c2:db)":
                    await ConnectAndTestAsync(TargetMacAddress);
                    break;

                case "Connect to custom device":
                    var address = AnsiConsole.Ask<string>("Enter MAC address (e.g., 66:1e:87:02:c2:db):");
                    await ConnectAndTestAsync(address);
                    break;

                case "Connect with vehicle detection":
                    await ConnectWithVehicleDetectionAsync(TargetMacAddress);
                    break;

                case "Test Binary Protocol (Service 6287)":
                    await BinaryProtocolTest.RunAsync(TargetMacAddress);
                    break;

                case "Test Nissan Leaf Battery Data":
                    await TestNissanLeafBatteryAsync(TargetMacAddress);
                    break;

                case "Record OBD session":
                    await RecordSessionAsync();
                    break;

                case "Generate Vehicle Support Report":
                    await GenerateVehicleSupportReportAsync();
                    break;

                case "List supported vehicles":
                    ListSupportedVehicles();
                    break;

                case "Discover device services":
                    await DiscoverServicesAsync(TargetMacAddress);
                    break;

                case "Exit":
                    return;
            }

            AnsiConsole.WriteLine();
        }
    }

    /// <summary>
    /// Test Nissan Leaf battery data using proprietary CAN addresses.
    /// Uses Mode 21 (manufacturer-specific group) with BMS header 0x79B.
    /// </summary>
    private static async Task TestNissanLeafBatteryAsync(string macAddress)
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

        var profile = BleDeviceProfile.VeepeakBle;
        using var transport = new WindowsBleTransport(profile);

        // Setup logging
        transport.DataSent += (_, data) => LogBleTraffic("TX", data);
        transport.DataReceived += (_, data) => LogBleTraffic("RX", data);

        // Connect
        var connected = await AnsiConsole.Status()
            .StartAsync($"Connecting to {macAddress}...", async ctx =>
            {
                return await transport.ConnectAsync(macAddress);
            });

        if (!connected)
        {
            AnsiConsole.MarkupLine("[red]Failed to connect![/]");
            return;
        }

        AnsiConsole.MarkupLine("[green]✓[/] BLE Connected");
        await Task.Delay(1500);

        // Helper to send command directly
        async Task<string> SendCommandAsync(string cmd, TimeSpan timeout)
        {
            transport.DrainBuffer();
            await transport.WriteAsync(cmd + "\r");
            var response = await transport.ReadUntilAsync(">", timeout);
            return response.Replace(cmd, "").Replace(">", "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        try
        {
            // Initialize adapter
            AnsiConsole.MarkupLine("[cyan]Initializing adapter...[/]");

            var commands = new[]
            {
                ("ATZ", "Reset adapter", TimeSpan.FromSeconds(5)),
                ("ATE0", "Disable echo", TimeSpan.FromSeconds(2)),
                ("ATL0", "Disable linefeeds", TimeSpan.FromSeconds(2)),
                ("ATS0", "Disable spaces", TimeSpan.FromSeconds(2)),
                ("ATH1", "Enable headers", TimeSpan.FromSeconds(2)),
                ("ATSP6", "Set protocol to CAN 11-bit 500k", TimeSpan.FromSeconds(3)),
                ("ATCAF0", "Disable CAN auto-formatting", TimeSpan.FromSeconds(2)),
            };

            foreach (var (cmd, desc, timeout) in commands)
            {
                var resp = await SendCommandAsync(cmd, timeout);
                AnsiConsole.MarkupLine($"[grey]   {cmd}: {resp.EscapeMarkup()}[/]");
                await Task.Delay(300);
            }

            AnsiConsole.MarkupLine("[green]✓[/] Adapter initialized");
            AnsiConsole.WriteLine();

            // Set up flow control for multi-frame responses
            AnsiConsole.MarkupLine("[cyan]Setting up flow control...[/]");
            
            var fcCommands = new[]
            {
                ("ATFCSH79B", "Set flow control header to BMS", TimeSpan.FromSeconds(2)),
                ("ATFCSD300000", "Set flow control data", TimeSpan.FromSeconds(2)),
                ("ATFCSM1", "Enable flow control mode", TimeSpan.FromSeconds(2)),
            };

            foreach (var (cmd, desc, timeout) in fcCommands)
            {
                var resp = await SendCommandAsync(cmd, timeout);
                AnsiConsole.MarkupLine($"[grey]   {cmd}: {resp.EscapeMarkup()}[/]");
                await Task.Delay(300);
            }

            AnsiConsole.MarkupLine("[green]✓[/] Flow control configured");
            AnsiConsole.WriteLine();

            // Set header to BMS (0x79B) and filter for responses from 0x7BB
            AnsiConsole.MarkupLine("[cyan]Configuring BMS communication...[/]");
            
            var bmsSetup = new[]
            {
                ("ATSH79B", "Set TX header to BMS", TimeSpan.FromSeconds(2)),
                ("ATCRA7BB", "Set RX filter to BMS response", TimeSpan.FromSeconds(2)),
            };

            foreach (var (cmd, desc, timeout) in bmsSetup)
            {
                var resp = await SendCommandAsync(cmd, timeout);
                AnsiConsole.MarkupLine($"[grey]   {cmd}: {resp.EscapeMarkup()}[/]");
                await Task.Delay(300);
            }

            AnsiConsole.MarkupLine("[green]✓[/] BMS communication configured (TX: 79B -> RX: 7BB)");
            AnsiConsole.WriteLine();

            // Now send the actual battery data requests
            AnsiConsole.Write(new Rule("[yellow]Battery Data Requests[/]").RuleStyle("grey"));
            AnsiConsole.WriteLine();

            var batteryCommands = new[]
            {
                ("2101", "BMS Group 01: SOC, Capacity, Current, Voltage"),
                ("2102", "BMS Group 02: Cell Voltages (96 cells)"),
                ("2104", "BMS Group 04: Pack Temperatures"),
            };

            var results = new List<(string Command, string Description, string Response, bool Success)>();

            foreach (var (cmd, desc) in batteryCommands)
            {
                AnsiConsole.MarkupLine($"[cyan]Sending {cmd}[/] ({desc})...");
                
                try
                {
                    var response = await SendCommandAsync(cmd, TimeSpan.FromSeconds(10));
                    
                    var hasData = !string.IsNullOrWhiteSpace(response) &&
                                  !response.Contains("NO DATA") &&
                                  !response.Contains("ERROR") &&
                                  !response.Contains("?") &&
                                  response.Length > 10;

                    results.Add((cmd, desc, response, hasData));

                    if (hasData)
                    {
                        AnsiConsole.MarkupLine($"[green]✓ Got {response.Length} chars of data![/]");
                        
                        // Show first 200 chars of response
                        var preview = response.Length > 200 ? response[..200] + "..." : response;
                        AnsiConsole.MarkupLine($"[grey]   {preview.EscapeMarkup()}[/]");
                        
                        // Try to parse some basic info from 2101 response
                        if (cmd == "2101" && response.Length > 20)
                        {
                            TryParseBmsGroup01(response);
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]✗ No data: {response.EscapeMarkup()}[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]✗ Error: {ex.Message.EscapeMarkup()}[/]");
                    results.Add((cmd, desc, ex.Message, false));
                }

                AnsiConsole.WriteLine();
                await Task.Delay(500);
            }

            // Summary
            AnsiConsole.Write(new Rule("[green]Results Summary[/]").RuleStyle("grey"));
            AnsiConsole.WriteLine();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Command")
                .AddColumn("Description")
                .AddColumn("Status")
                .AddColumn("Data Length");

            foreach (var (cmd, desc, response, success) in results)
            {
                table.AddRow(
                    cmd,
                    desc,
                    success ? "[green]Success[/]" : "[red]Failed[/]",
                    success ? $"{response.Length} chars" : "-"
                );
            }

            AnsiConsole.Write(table);

            var successCount = results.Count(r => r.Success);
            if (successCount > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[green]✓ Successfully read {successCount}/{results.Count} battery data groups![/]");
                AnsiConsole.MarkupLine("[grey]The Nissan Leaf is responding to proprietary Mode 21 requests.[/]");
            }
            else
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[red]✗ No battery data received.[/]");
                AnsiConsole.MarkupLine("[yellow]Possible causes:[/]");
                AnsiConsole.MarkupLine("  • Vehicle not in READY mode (foot on brake + start button)");
                AnsiConsole.MarkupLine("  • Vehicle not actively charging");
                AnsiConsole.MarkupLine("  • BLE connection unstable");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
        }
        finally
        {
            await transport.DisconnectAsync();
            AnsiConsole.MarkupLine("[grey]Disconnected[/]");
        }
    }

    /// <summary>
    /// Try to parse basic info from BMS Group 01 response.
    /// Based on OVMS Nissan Leaf implementation.
    /// </summary>
    private static void TryParseBmsGroup01(string response)
    {
        try
        {
            // Remove spaces and get hex bytes
            var hex = response.Replace(" ", "").Replace("\r", "").Replace("\n", "");
            
            // Look for the response header (61 01 for Mode 21 response to 2101)
            var dataStart = hex.IndexOf("6101", StringComparison.OrdinalIgnoreCase);
            if (dataStart < 0)
            {
                // Try without the mode prefix
                dataStart = 0;
            }
            else
            {
                dataStart += 4; // Skip past "6101"
            }

            // Extract hex bytes after header
            var dataHex = hex[dataStart..];
            if (dataHex.Length < 20)
            {
                AnsiConsole.MarkupLine("[yellow]   Response too short to parse[/]");
                return;
            }

            // Convert to bytes
            var bytes = new List<byte>();
            for (var i = 0; i < dataHex.Length - 1; i += 2)
            {
                if (byte.TryParse(dataHex.Substring(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    bytes.Add(b);
                }
            }

            if (bytes.Count < 10)
            {
                AnsiConsole.MarkupLine("[yellow]   Not enough bytes to parse[/]");
                return;
            }

            // Try to extract some values (byte positions based on OVMS)
            // Note: These positions may vary by model year
            AnsiConsole.MarkupLine("[cyan]   Parsed data (approximate):[/]");
            AnsiConsole.MarkupLine($"[grey]   Raw bytes: {string.Join(" ", bytes.Take(20).Select(b => b.ToString("X2")))}[/]");
            
            // SOC is usually around byte 5-6 as a 10-bit value
            if (bytes.Count > 6)
            {
                var socRaw = ((bytes[5] << 2) | (bytes[6] >> 6)) & 0x3FF;
                var soc = socRaw / 10.0;
                if (soc is > 0 and <= 100)
                {
                    AnsiConsole.MarkupLine($"[green]   Estimated SOC: {soc:F1}%[/]");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private static async Task RecordSessionAsync()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Record OBD Session[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[yellow]This tool records all OBD communication for replay in unit tests.[/]");
        AnsiConsole.MarkupLine("[grey]The trace file will be saved in JSONL format.[/]");
        AnsiConsole.WriteLine();

        // Get MAC address
        var macAddress = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Enter OBD adapter MAC address:[/]")
                .DefaultValue(TargetMacAddress)
                .Validate(mac =>
                {
                    var clean = mac.Replace(":", "").Replace("-", "");
                    return clean.Length == 12 && clean.All(c => Uri.IsHexDigit(c))
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Invalid MAC address format");
                }));

        // Get session description
        var description = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Session description (optional):[/]")
                .AllowEmpty());

        // Select BLE profile
        var bleProfile = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select BLE adapter profile:[/]")
                .AddChoices(
                    "Veepeak BLE+ (FFF0/FFF1/FFF2)",
                    "Veepeak BLE+ Alt (FFE0/FFE1)",
                    "Nordic UART Service"
                ));

        var profile = bleProfile switch
        {
            "Veepeak BLE+ (FFF0/FFF1/FFF2)" => BleDeviceProfile.VeepeakBle,
            "Veepeak BLE+ Alt (FFE0/FFE1)" => BleDeviceProfile.VeepeakBleAlt,
            "Nordic UART Service" => BleDeviceProfile.NordicUart,
            _ => BleDeviceProfile.VeepeakBle
        };

        AnsiConsole.WriteLine();

        // Create transport with recording
        using var baseTransport = new WindowsBleTransport(profile);
        var tracer = new TransportTracer();
        using var transport = new RecordingTransportDecorator(baseTransport, tracer);

        // Setup live traffic logging
        transport.DataSent += (_, data) => LogBleTraffic("TX", data);
        transport.DataReceived += (_, data) => LogBleTraffic("RX", data);

        // Connect
        var connected = await AnsiConsole.Status()
            .StartAsync($"Connecting to {macAddress}...", async ctx =>
            {
                return await baseTransport.ConnectAsync(macAddress);
            });

        if (!connected)
        {
            AnsiConsole.MarkupLine("[red]Failed to connect![/]");
            return;
        }

        AnsiConsole.MarkupLine("[green]Connected![/]");

        // Start recording
        var sessionMetadata = new TraceSessionMetadata
        {
            StartedAt = DateTimeOffset.UtcNow,
            TransportType = baseTransport.GetType().Name,
            DeviceAddress = macAddress,
            DeviceName = profile.Name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description
        };
        transport.StartRecording(sessionMetadata);

        AnsiConsole.MarkupLine("[yellow]Recording started. All commands will be traced.[/]");
        AnsiConsole.WriteLine();

        // Run command loop with recording
        var adapter = new Elm327Adapter();
        adapter.Log += (_, e) => LogAdapter(e);

        var initialized = await AnsiConsole.Status()
            .StartAsync("Initializing ELM327 adapter...", async ctx =>
            {
                return await adapter.InitializeAsync(transport);
            });

        if (initialized)
        {
            // Update metadata with detected protocol
            transport.Tracer.UpdateMetadata(m => m with
            {
                Protocol = adapter.ProtocolDescription,
                AdapterVersion = adapter.DeviceVersion,
                EchoEnabled = false,
                HeadersEnabled = false
            });

            AnsiConsole.MarkupLine("[green]Adapter ready![/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Adapter initialization completed with warnings[/]");
        }

        // Interactive session
        await RunRecordingCommandLoopAsync(transport, adapter);

        // Stop recording
        var session = transport.StopRecording();

        // Disconnect
        await baseTransport.DisconnectAsync();
        AnsiConsole.MarkupLine("[grey]Disconnected[/]");

        // Save the recording
        await SaveRecordedSessionAsync(session);
    }

    private static async Task SaveRecordedSessionAsync(TransportSession session)
    {

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Recording Complete[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        // Display summary
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Property")
            .AddColumn("Value");

        summaryTable.AddRow("Session ID", session.SessionId);
        summaryTable.AddRow("Duration", session.Duration.ToString(@"mm\:ss\.fff"));
        summaryTable.AddRow("Total Entries", session.EntryCount.ToString());
        summaryTable.AddRow("Bytes TX", session.TotalBytesTx.ToString());
        summaryTable.AddRow("Bytes RX", session.TotalBytesRx.ToString());
        summaryTable.AddRow("Protocol", session.Metadata.Protocol ?? "[grey]Unknown[/]");
        summaryTable.AddRow("Adapter", session.Metadata.AdapterVersion ?? "[grey]Unknown[/]");

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        // Generate filename
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var defaultName = $"obd_session_{timestamp}.jsonl";

        var fileName = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Save as:[/]")
                .DefaultValue(defaultName));

        if (!fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            fileName += ".jsonl";

        var filePath = Path.Combine(Environment.CurrentDirectory, fileName);

        // Save
        var serializer = new JsonLTransportSessionSerializer();
        await serializer.SaveAsync(session, filePath);

        AnsiConsole.MarkupLine($"[green]✓[/] Session saved to: [cyan]{filePath.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();

        // Show usage instructions
        AnsiConsole.Write(new Panel(
            $"""
            [yellow]Usage in Unit Tests:[/]

            ```csharp
            // Load and replay the session
            var transport = await ReplayTransportFactory.FromFileAsync("{fileName}");
            var adapter = new Elm327Adapter();
            await adapter.InitializeAsync(transport);

            // Your test assertions here...
            ```

            [grey]The trace file can be embedded as a test resource or loaded from disk.[/]
            """)
            .Header("[cyan]Next Steps[/]")
            .Border(BoxBorder.Rounded));

        // Offer to open file location
        if (AnsiConsole.Confirm("Open file location?", defaultValue: false))
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{filePath}\"",
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
            }
            catch
            {
                AnsiConsole.MarkupLine("[yellow]Could not open file location.[/]");
            }
        }
    }

    private static async Task RunRecordingCommandLoopAsync(RecordingTransportDecorator transport, Elm327Adapter adapter)
    {
        var service = new ObdService(adapter);

        while (transport.InnerTransport.IsConnected)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Command (recording):[/]")
                    .AddChoices(
                        "Send ATZ (Reset)",
                        "Send ATI (Version)",
                        "Send 0100 (Supported PIDs)",
                        "Get RPM",
                        "Get Speed",
                        "Get Coolant Temp",
                        "Get VIN",
                        "Send custom command",
                        "Read DTCs",
                        "Stop recording"
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
                        if (rpm.HasValue)
                            AnsiConsole.MarkupLine($"[green]RPM:[/] {rpm} rpm");
                        else
                            AnsiConsole.MarkupLine("[yellow]Could not read RPM (is vehicle running?)[/]");
                        break;

                    case "Get Speed":
                        var speed = await service.GetSpeedKphAsync();
                        if (speed.HasValue)
                            AnsiConsole.MarkupLine($"[green]Speed:[/] {speed} km/h");
                        else
                            AnsiConsole.MarkupLine("[yellow]Could not read speed[/]");
                        break;

                    case "Get Coolant Temp":
                        var temp = await service.GetCoolantTempCelsiusAsync();
                        if (temp.HasValue)
                            AnsiConsole.MarkupLine($"[green]Coolant Temp:[/] {temp:F1} °C");
                        else
                            AnsiConsole.MarkupLine("[yellow]Could not read coolant temp[/]");
                        break;

                    case "Get VIN":
                        await SendAndDisplayAsync(adapter, "0902", TimeSpan.FromSeconds(10));
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

                    case "Stop recording":
                        return;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            }

            AnsiConsole.WriteLine();





            // Show recording stats
            var currentSession = transport.Tracer.CurrentSession;
            if (currentSession != null)
            {
                AnsiConsole.MarkupLine($"[grey]Recorded: {currentSession.EntryCount} entries, {currentSession.TotalBytesTx} TX / {currentSession.TotalBytesRx} RX bytes[/]");
            }












        }
    }

    private static void ListSupportedVehicles()
    {
        var detector = new VehicleDetectorService();

        // Register additional profiles from Drivers package
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

    private static async Task ConnectWithVehicleDetectionAsync(string macAddress)
    {
        var profile = BleDeviceProfile.VeepeakBle;
        using var transport = new WindowsBleTransport(profile);

        // Create vehicle-aware service with detection
        var detector = new VehicleDetectorService();
        VehicleProfileRegistry.RegisterAllProfiles(detector);

        var vehicleService = new VehicleObdService(detector: detector);

        // Setup logging
        transport.DataSent += (_, data) => LogBleTraffic("TX", data);
        transport.DataReceived += (_, data) => LogBleTraffic("RX", data);

        // Connect transport
        var connected = await AnsiConsole.Status()
            .StartAsync($"Connecting to {macAddress}...", async ctx =>
            {
                return await transport.ConnectAsync(macAddress);
            });

        if (!connected)
        {
            AnsiConsole.MarkupLine("[red]Failed to connect![/]");
            return;
        }

        AnsiConsole.MarkupLine("[green]Connected![/]");

        // Connect with vehicle detection
        var options = new VehicleServiceOptions
        {
            AutoDetectVehicle = true,
            DetectionTimeout = TimeSpan.FromSeconds(30)
        };

        var initialized = await AnsiConsole.Status()
            .StartAsync("Detecting vehicle...", async ctx =>
            {
                return await vehicleService.ConnectAsync(transport, options);
            });

        if (initialized)
        {
            var vehicleProfile = vehicleService.VehicleProfile;
            AnsiConsole.MarkupLine($"[green]Detected:[/] {vehicleProfile.Name}");
            AnsiConsole.MarkupLine($"[grey]Protocol:[/] {vehicleProfile.Protocol}");
            AnsiConsole.MarkupLine($"[grey]EV:[/] {(vehicleProfile.IsElectric ? "Yes" : "No")}");

            // Run vehicle-specific command loop
            await RunVehicleCommandLoopAsync(transport, vehicleService);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Failed to initialize vehicle service[/]");
        }

        await transport.DisconnectAsync();
        AnsiConsole.MarkupLine("[grey]Disconnected[/]");
    }

    private static async Task RunVehicleCommandLoopAsync(WindowsBleTransport transport, VehicleObdService service)
    {
        var profile = service.VehicleProfile;

        while (transport.IsConnected)
        {
            var choices = new List<string>
            {
                "Get VIN",
                "Get Speed",
                "Read DTCs"
            };

            // Add EV-specific options if supported
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
                // ICE-specific options
                choices.InsertRange(1, new[]
                {
                    "Get RPM",
                    "Get Coolant Temp",
                    "Get Throttle Position",
                    "Get Fuel Level"
                });
            }

            choices.Add("Query custom data point");
            choices.Add("Back to main menu");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[cyan]{profile.Name} - Select command:[/]")
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
                                AnsiConsole.MarkupLine($"  [grey]Model Year:[/] {vinInfo.ModelYear?.ToString() ?? "Unknown"}");
                            }
                        }
                        break;

                    case "Get Speed":
                        var speed = await service.GetSpeedKphAsync();
                        DisplayResult("Speed", speed, "km/h");
                        break;

                    case "Get RPM":
                        var rpm = await service.GetRpmAsync();
                        DisplayResult("RPM", rpm, "rpm");
                        break;

                    case "Get Coolant Temp":
                        var coolant = await service.GetCoolantTempCelsiusAsync();
                        DisplayResult("Coolant Temp", coolant, "°C");
                        break;

                    case "Get Throttle Position":
                        var throttle = await service.GetThrottlePositionPercentAsync();
                        DisplayResult("Throttle", throttle, "%");
                        break;

                    case "Get Fuel Level":
                        var fuel = await service.GetFuelLevelPercentAsync();
                        DisplayResult("Fuel Level", fuel, "%");
                        break;

                    case "Get Battery SOC":
                        var soc = await service.GetBatterySocAsync();
                        DisplayResult("Battery SOC", soc, "%");
                        break;

                    case "Get Battery SOH":
                        var soh = await service.GetBatterySohAsync();
                        DisplayResult("Battery SOH", soh, "%");
                        break;

                    case "Get Battery Voltage":
                        var voltage = await service.GetBatteryVoltageAsync();
                        DisplayResult("Battery Voltage", voltage, "V");
                        break;

                    case "Get Range Remaining":
                        var range = await service.GetRangeRemainingAsync();
                        DisplayResult("Range", range, "km");
                        break;

                    case "Get Charging Status":
                        var chargingStatus = await service.GetChargingStatusAsync();
                        DisplayResult("Charging Status", chargingStatus);
                        break;

                    case "Get Full Battery Info":
                        await DisplayBatteryInfoAsync(service);
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

                    case "Query custom data point":
                        await QueryCustomDataPointAsync(service);
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

    private static async Task DisplayBatteryInfoAsync(VehicleObdService service)
    {
        var info = await service.GetBatteryInfoAsync();

        if (info == null)
        {
            AnsiConsole.MarkupLine("[yellow]Could not retrieve battery info[/]");
            return;
        }

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
        table.AddRow("Capacity", $"{info.Capacity:F1} Ah");
        table.AddRow("Range Remaining", $"{info.RangeRemaining:F1} km");
        table.AddRow("Charging Status", info.ChargingStatus);
        table.AddRow("Is Charging", info.IsCharging ? "[green]Yes[/]" : "[grey]No[/]");

        AnsiConsole.Write(table);
    }

    private static async Task QueryCustomDataPointAsync(IVehicleObdService service)
    {
        var supportedPoints = Enum.GetValues<VehicleDataPoint>()
            .Where(dp => service.IsDataPointSupported(dp))
            .Select(dp => dp.ToString())
            .ToList();

        if (supportedPoints.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No supported data points found[/]");
            return;
        }

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select data point:[/]")
                .AddChoices(supportedPoints));

        if (Enum.TryParse<VehicleDataPoint>(choice, out var dataPoint))
        {
            var result = await service.GetDataAsync(dataPoint);

            if (result.Success)
            {
                AnsiConsole.MarkupLine($"[green]{result.DataPoint}:[/] {result.Value} {result.Unit}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Failed:[/] {result.Error}");
            }
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

    private static async Task ScanForDevicesAsync()
    {
        using var scanner = new WindowsBleScanner();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Name")
            .AddColumn("Address")
            .AddColumn("RSSI")
            .AddColumn("Services");

        var devices = new Dictionary<string, BleDeviceInfo>();

        scanner.DeviceDiscovered += (_, e) =>
        {
            devices[e.Device.Address] = e.Device;
        };

        await AnsiConsole.Status()
            .StartAsync("Scanning for BLE devices...", async ctx =>
            {
                await scanner.StartScanAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
                await scanner.StopScanAsync();
            });

        foreach (var device in devices.Values.OrderByDescending(d => d.Rssi))
        {
            var services = device.AdvertisedServices.Count > 0
                ? string.Join(", ", device.AdvertisedServices.Select(s => s.ToString()[..8] + "..."))
                : "[grey]none[/]";

            table.AddRow(
                device.Name.EscapeMarkup(),
                $"[cyan]{device.Address}[/]",
                GetRssiDisplay(device.Rssi),
                services
            );
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Found {devices.Count} devices[/]");
    }

    private static async Task DiscoverServicesAsync(string macAddress)
    {
        AnsiConsole.MarkupLine($"[cyan]Discovering services on {macAddress.EscapeMarkup()}...[/]");

        try
        {
            var mac = ParseMacAddress(macAddress);
            using var device = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromBluetoothAddressAsync(mac);

            if (device == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to connect to device[/]");
                return;
            }

            var servicesResult = await device.GetGattServicesAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);

            if (servicesResult.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
            {
                AnsiConsole.MarkupLine($"[red]Failed to get services: {servicesResult.Status}[/]");
                return;
            }

            var deviceName = device.Name?.EscapeMarkup() ?? "Unknown";
            var tree = new Tree($"[cyan]{deviceName}[/] ({macAddress.EscapeMarkup()})");

            foreach (var service in servicesResult.Services)
            {
                var serviceNode = tree.AddNode($"[yellow]Service:[/] {service.Uuid}");

                var charsResult = await service.GetCharacteristicsAsync(Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
                if (charsResult.Status == Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
                {
                    foreach (var characteristic in charsResult.Characteristics)
                    {
                        var props = characteristic.CharacteristicProperties;
                        var propsStr = string.Join(", ", GetPropertyStrings(props));
                        serviceNode.AddNode($"[green]Char:[/] {characteristic.Uuid} [grey]({propsStr})[/]");
                    }
                }

                service.Dispose();
            }

            AnsiConsole.Write(tree);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private static async Task ConnectAndTestAsync(string macAddress)
    {
        var profile = BleDeviceProfile.VeepeakBle;
        using var transport = new WindowsBleTransport(profile);
        var adapter = new Elm327Adapter();

        // Setup logging
        transport.DataSent += (_, data) => LogBleTraffic("TX", data);
        transport.DataReceived += (_, data) => LogBleTraffic("RX", data);
        adapter.Log += (_, e) => LogAdapter(e);

        // Connect
        var connected = await AnsiConsole.Status()
            .StartAsync($"Connecting to {macAddress}...", async ctx =>
            {
                return await transport.ConnectAsync(macAddress);
            });

        if (!connected)
        {
            AnsiConsole.MarkupLine("[red]Failed to connect![/]");
            AnsiConsole.MarkupLine("[yellow]Tip: Try running 'Discover device services' to check available UUIDs[/]");
            return;
        }

        AnsiConsole.MarkupLine("[green]Connected![/]");

        // Initialize adapter
        var initialized = await AnsiConsole.Status()
            .StartAsync("Initializing ELM327 adapter...", async ctx =>
            {
                return await adapter.InitializeAsync(transport);
            });

        if (!initialized)
        {
            AnsiConsole.MarkupLine("[yellow]Adapter initialization completed with warnings[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[green]Adapter ready![/]");
        }

        // Interactive command loop
        await RunCommandLoopAsync(transport, adapter);

        // Disconnect
        await transport.DisconnectAsync();
        AnsiConsole.MarkupLine("[grey]Disconnected[/]");
    }

    private static async Task RunCommandLoopAsync(WindowsBleTransport transport, Elm327Adapter adapter)
    {
        var service = new ObdService(adapter);

        while (transport.IsConnected)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Command:[/]")
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
                        if (rpm.HasValue)
                            AnsiConsole.MarkupLine($"[green]RPM:[/] {rpm} rpm");
                        else
                            AnsiConsole.MarkupLine("[yellow]Could not read RPM (is vehicle running?)[/]");
                        break;

                    case "Get Speed":
                        var speed = await service.GetSpeedKphAsync();
                        if (speed.HasValue)
                            AnsiConsole.MarkupLine($"[green]Speed:[/] {speed} km/h");
                        else
                            AnsiConsole.MarkupLine("[yellow]Could not read speed[/]");
                        break;

                    case "Get Coolant Temp":
                        var temp = await service.GetCoolantTempCelsiusAsync();
                        if (temp.HasValue)
                            AnsiConsole.MarkupLine($"[green]Coolant Temp:[/] {temp:F1} °C");
                        else
                            AnsiConsole.MarkupLine("[yellow]Could not read coolant temp[/]");
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

    private static void LogBleTraffic(string direction, string data)
    {
        var escaped = data
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace(">", ">");

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

    private static string GetRssiDisplay(int rssi)
    {
        var color = rssi switch
        {
            > -50 => "green",
            > -70 => "yellow",
            _ => "red"
        };
        return $"[{color}]{rssi} dBm[/]";
    }

    private static IEnumerable<string> GetPropertyStrings(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties props)
    {
        if (props.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Read))
            yield return "Read";
        if (props.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Write))
            yield return "Write";
        if (props.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.WriteWithoutResponse))
            yield return "WriteNoResp";
        if (props.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Notify))
            yield return "Notify";
        if (props.HasFlag(Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties.Indicate))
            yield return "Indicate";
    }

    private static ulong ParseMacAddress(string mac)
    {
        var cleanMac = mac.Replace(":", "").Replace("-", "");
        return Convert.ToUInt64(cleanMac, 16);
    }

    private static async Task GenerateVehicleSupportReportAsync()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Vehicle Support Report Generator[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[yellow]This tool collects diagnostic data to help add support for new vehicles and OBD adapters.[/]");
        AnsiConsole.MarkupLine("[grey]The report will be saved as a markdown file suitable for GitHub issues.[/]");
        AnsiConsole.WriteLine();

        // Step 1: Get user vehicle info
        var userInfo = CollectUserVehicleInfo();

        // Step 2: Get adapter MAC address
        var macAddress = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Enter OBD adapter MAC address:[/]")
                .DefaultValue(TargetMacAddress)
                .Validate(mac =>
                {
                    var clean = mac.Replace(":", "").Replace("-", "");
                    return clean.Length == 12 && clean.All(c => Uri.IsHexDigit(c))
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Invalid MAC address format");
                }));

        // Step 3: Select BLE profile
        var bleProfile = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select BLE adapter profile:[/]")
                .AddChoices(
                    "Veepeak BLE+ (FFF0/FFF1/FFF2)",
                    "Veepeak BLE+ Alt (FFE0/FFE1)",
                    "Nordic UART Service",
                    "Auto-detect (try all)"
                ));

        var profile = bleProfile switch
        {
            "Veepeak BLE+ (FFF0/FFF1/FFF2)" => BleDeviceProfile.VeepeakBle,
            "Veepeak BLE+ Alt (FFE0/FFE1)" => BleDeviceProfile.VeepeakBleAlt,
            "Nordic UART Service" => BleDeviceProfile.NordicUart,
            _ => BleDeviceProfile.VeepeakBle
        };

        // Determine if EV probing should be done
        var isEv = userInfo.EngineType?.Contains("Electric") == true ||
                   userInfo.EngineType?.Contains("BEV") == true ||
                   userInfo.EngineType?.Contains("Hybrid") == true;

        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("[yellow]Ready to start diagnostic collection. Continue?[/]"))
        {
            return;
        }

        // Add vehicle state guidance for EVs
        if (isEv)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(
                """
                [yellow]IMPORTANT for Electric Vehicles:[/]

                The Nissan Leaf (and many EVs) will [red]NOT respond[/] to OBD queries
                when the car is in a sleep state. The vehicle must be:

                [green]• Ignition ON (READY mode)[/] - Press start button with foot on brake
                [green]• OR Actively charging[/] - Plugged in and charge session active

                If ignition is just in ACC mode, or the car is off, the ECUs are asleep
                and will not respond to any commands.

                [grey]This is normal EV behavior documented in OVMS and LeafSpy.[/]
                """)
                .Header("[cyan]Vehicle Wake State[/]")
                .Border(BoxBorder.Rounded));

            if (!AnsiConsole.Confirm("[yellow]Is your vehicle in READY mode or actively charging?[/]"))
            {
                AnsiConsole.MarkupLine("[yellow]Please turn on the vehicle (READY mode) and try again.[/]");
                return;
            }
        }

        // Create collector using Core library
        var collector = new Core.Diagnostics.DiagnosticDataCollector();
        BleAdapterInfo? bleInfo = null;
        ObdAdapterInfo? obdAdapterInfo = null;
        VehicleIdentification? vehicleId = null;
        SupportedPidsInfo? supportedPids = null;
        WindowsBleTransport? transport = null;
        Elm327Adapter? adapter = null;

        // Create progress reporter that writes to console in real-time
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
                DiagnosticPhase.Complete => "[green]✓[/]",
                DiagnosticPhase.Failed => "[red]✗[/]",
                _ => "[cyan]>[/]"
            };

            // Escape ALL user data to prevent Spectre.Console markup parsing errors
            var escapedMessage = p.Message?.EscapeMarkup() ?? "";
            var progressPct = (p.OverallProgress * 100).ToString("F0");
            var itemProgress = p.ItemsTotal > 0 ? $"({p.ItemsCompleted}/{p.ItemsTotal})" : "";

            try
            {
                AnsiConsole.MarkupLine($"{phaseIcon} [{statusColor}]{escapedMessage}[/] [grey]{itemProgress} ({progressPct}%)[/]");

                // Show response for probes (but not for every message)
                if (!string.IsNullOrEmpty(p.LastResponse) && p.CurrentItem != null && p.LastOperationSuccess == true)
                {
                    var truncated = p.LastResponse.Length > 60 ? p.LastResponse[..57] + "..." : p.LastResponse;
                    // IMPORTANT: Escape markup to avoid "[" and "]" being interpreted as Spectre markup
                    var escaped = truncated.Replace("\r", "").Replace("\n", " ").EscapeMarkup();
                    AnsiConsole.MarkupLine($"   [grey]→ {escaped}[/]");
                }
            }
            catch (Exception ex)
            {
                // Fallback to plain console output if markup parsing fails
                Console.WriteLine($"> {p.Message} {itemProgress} ({progressPct}%)");
                if (!string.IsNullOrEmpty(p.LastResponse) && p.CurrentItem != null && p.LastOperationSuccess == true)
                {
                    var truncated = p.LastResponse.Length > 60 ? p.LastResponse[..57] + "..." : p.LastResponse;
                    Console.WriteLine($"   → {truncated.Replace("\r", "").Replace("\n", " ")}");
                }
            }
        });

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Starting Comprehensive Collection[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        // Helper function to ensure we're connected
        async Task<bool> EnsureConnectedAsync()
        {
            if (transport?.IsConnected == true)
            {
                // Validate link is actually usable (not connected-but-stale)
                var ok = await TryValidateAdapterLinkAsync();
                if (ok)
                    return true;
            }

            AnsiConsole.MarkupLine("[yellow]Reconnecting to adapter...[/]");

            // Dispose old transport if it exists
            if (transport != null)
            {
                try { await transport.DisconnectAsync(); } catch { }
                transport.Dispose();
                transport = null;
            }

            // Wait longer for BLE stack to settle
            await Task.Delay(3000);

            // Create new transport and reconnect
            transport = new WindowsBleTransport(profile);
            transport.DataSent += (_, data) => LogBleTraffic("TX", data);
            transport.DataReceived += (_, data) => LogBleTraffic("RX", data);

            var connected = await transport.ConnectAsync(macAddress);
            if (!connected)
            {
                AnsiConsole.MarkupLine("[red]Failed to reconnect![/]");
                return false;
            }

            // Wait for connection to stabilize
            await Task.Delay(1500);

            // Do a full minimal init after reconnect (more reliable than sending just ATZ/ATE0)
            AnsiConsole.MarkupLine("[grey]Resetting adapter after reconnect...[/]");
            var reinitOk = await MinimalAdapterInitAsync();
            if (!reinitOk)
            {
                AnsiConsole.MarkupLine("[red]Reconnect succeeded but adapter init failed[/]");
                return false;
            }

            // Validate we can still round-trip at least one simple AT command
            if (!await TryValidateAdapterLinkAsync())
            {
                AnsiConsole.MarkupLine("[red]Reconnect succeeded but adapter did not respond to validation command[/]");
                return false;
            }

            AnsiConsole.MarkupLine("[green]Reconnected![/]");
            return transport.IsConnected;
        }

        // Helper to validate adapter link by sending a command directly through transport
        async Task<bool> TryValidateAdapterLinkAsync()
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

        // Helper to do minimal adapter init (no protocol search) - sends commands directly through transport
        async Task<bool> MinimalAdapterInitAsync()
        {
            if (transport == null || !transport.IsConnected)
                return false;

            try
            {
                // Helper to send a command directly through transport and get response
                async Task<(bool Success, string Response)> SendDirectAsync(string cmd, TimeSpan timeout)
                {
                    AnsiConsole.MarkupLine($"[grey]   Init: {cmd}[/]");
                    
                    try
                    {
                        // Clear any pending data in buffer
                        transport.DrainBuffer();
                        
                        // Send command with CR
                        await transport.WriteAsync(cmd + "\r");
                        
                        // Wait for response ending with '>'
                        var response = await transport.ReadUntilAsync(">", timeout);
                        
                        // Clean up response
                        response = response
                            .Replace(cmd, "") // Remove echo
                            .Replace(">", "")
                            .Replace("\r", "")
                            .Replace("\n", " ")
                            .Trim();
                        
                        var success = !string.IsNullOrWhiteSpace(response) && 
                                     !response.Contains("?") &&
                                     !response.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
                        
                        if (!success)
                            AnsiConsole.MarkupLine($"[yellow]{cmd} response: {response}[/]");
                        
                        return (success, response);
                    }
                    catch (TimeoutException)
                    {
                        AnsiConsole.MarkupLine($"[yellow]{cmd} timed out[/]");
                        return (false, "");
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]{cmd} error: {ex.Message}[/]");
                        return (false, "");
                    }
                }

                // ATZ can be flaky on some clones; attempt it, but don't make it a hard requirement.
                var (atzOk, atzResp) = await SendDirectAsync("ATZ", TimeSpan.FromSeconds(8));
                if (!atzOk)
                    AnsiConsole.MarkupLine("[yellow]ATZ did not respond properly (continuing anyway)[/]");
                else
                    AnsiConsole.MarkupLine($"[grey]   → {atzResp}[/]");

                await Task.Delay(800);

                // Hard requirement: we must be able to talk to the adapter.
                var (atiOk, atiResp) = await SendDirectAsync("ATI", TimeSpan.FromSeconds(8));
                if (!atiOk || string.IsNullOrWhiteSpace(atiResp))
                {
                    AnsiConsole.MarkupLine("[red]ATI did not respond - adapter not ready[/]");
                    return false;
                }
                AnsiConsole.MarkupLine($"[grey]   → {atiResp}[/]");

                // Send the rest of the init commands
                var commands = new[] { "ATE0", "ATL0", "ATS0", "ATH0" };
                foreach (var cmd in commands)
                {
                    var (ok, resp) = await SendDirectAsync(cmd, TimeSpan.FromSeconds(6));
                    if (!ok)
                    {
                        AnsiConsole.MarkupLine($"[yellow]{cmd} failed[/]");
                        return false;
                    }
                    await Task.Delay(300);
                }

                // Set protocol to CAN 11-bit 500k (protocol 6 - common for most vehicles)
                var (sp6Ok, sp6Resp) = await SendDirectAsync("ATSP6", TimeSpan.FromSeconds(8));
                if (!sp6Ok)
                {
                    AnsiConsole.MarkupLine("[yellow]ATSP6 did not respond[/]");
                    return false;
                }

                await Task.Delay(300);
                
                // Now set up the adapter object so it can be used by DiagnosticDataCollector
                // Use SetTransport to avoid the full initialization sequence (which includes
                // the slow protocol search that doesn't work well on EVs)
                adapter = new Elm327Adapter();
                adapter.Log += (_, e) => LogAdapter(e);
                adapter.SetTransport(transport, markAsInitialized: true);
                
                AnsiConsole.MarkupLine("[green]✓[/] Direct init complete");
                
                return true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Init error: {ex.Message.EscapeMarkup()}[/]");
                return false;
            }
        }

        try
        {
            // Phase 1: Skip separate BLE discovery - it can conflict with the main connection
            // We'll collect service info during the main connection instead
            AnsiConsole.MarkupLine("[cyan]Phase 1: Preparing to connect...[/]");
            AnsiConsole.MarkupLine("[grey]   (BLE service info will be collected during connection)[/]");

            // Phase 2: Connect transport
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Phase 2: Connecting to OBD adapter...[/]");

            transport = new WindowsBleTransport(profile);
            transport.DataSent += (_, data) => LogBleTraffic("TX", data);
            transport.DataReceived += (_, data) => LogBleTraffic("RX", data);

            var connected = await transport.ConnectAsync(macAddress);
            if (!connected)
            {
                AnsiConsole.MarkupLine("[red]✗ Failed to connect to BLE device![/]");
                collector.AddError("Connection", "Failed to establish BLE connection");
                goto GenerateReport;
            }

            AnsiConsole.MarkupLine("[green]✓[/] BLE connection established");

            // Collect minimal BLE info from successful connection
            bleInfo = new BleAdapterInfo
            {
                DeviceName = profile.Name,
                MacAddress = macAddress,
                Services = [] // We connected successfully, but won't enumerate services separately
            };
            collector.AddNote($"Connected to {bleInfo.DeviceName} ({bleInfo.MacAddress})");

            // Wait for connection to fully stabilize
            AnsiConsole.MarkupLine("[grey]   Waiting for connection to stabilize...[/]");
            await Task.Delay(2000);

            // Phase 3: Initialize ELM327 adapter
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Phase 3: Initializing ELM327 adapter...[/]");

            adapter = new Elm327Adapter();
            adapter.Log += (_, e) => LogAdapter(e);

            if (isEv)
            {
                // For EVs, use minimal init to avoid the protocol search timeout
                AnsiConsole.MarkupLine("[grey]   (Using minimal initialization for EV - skipping protocol search)[/]");
                var minimalInit = await MinimalAdapterInitAsync();
                if (minimalInit)
                {
                    AnsiConsole.MarkupLine("[green]✓[/] Adapter initialized (minimal mode for EV)");
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]⚠[/] Minimal initialization had issues");
                    if (!await EnsureConnectedAsync())
                        goto GenerateReport;
                }
            }
            else
            {
                // For regular vehicles, use full initialization
                AnsiConsole.MarkupLine("[grey]   (This may take up to 45 seconds for protocol search)[/]");
                var initialized = await adapter.InitializeAsync(transport);
                if (!initialized)
                {
                    AnsiConsole.MarkupLine("[yellow]⚠[/] Adapter initialization completed with warnings");
                }
                else
                {
                    AnsiConsole.MarkupLine("[green]✓[/] Adapter initialized successfully");
                }
            }

            // Check connection and reconnect if needed
            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 4: Collect adapter info
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Phase 4: Collecting OBD adapter info...[/]");

            obdAdapterInfo = await collector.CollectObdAdapterInfoAsync(adapter, progress);
            AnsiConsole.MarkupLine($"[green]✓[/] Adapter: {obdAdapterInfo.VersionResponse?.Trim() ?? "Unknown"}");

            // Reconnect if needed before protocol probe
            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 5: Probe multiple protocols
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Phase 5: Probing OBD protocols...[/]");
            AnsiConsole.MarkupLine("[grey]   (Testing which protocols get responses from the vehicle)[/]");

            await collector.ProbeProtocolsAsync(adapter, progress);

            var workingProtocols = collector.BuildReport(userInfo, bleInfo, obdAdapterInfo, vehicleId, supportedPids)
                .ProtocolProbeResults.Count(p => p.GotResponse);
            AnsiConsole.MarkupLine($"[green]✓[/] Found {workingProtocols} working protocol(s)");

            // Reconnect if needed
            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 6: Collect vehicle ID
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Phase 6: Reading vehicle identification...[/]");
            vehicleId = await collector.CollectVehicleIdAsync(adapter, progress);
            if (!string.IsNullOrEmpty(vehicleId.Vin))
            {
                AnsiConsole.MarkupLine($"[green]✓[/] VIN: {MaskVin(vehicleId.Vin)}");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]⚠[/] VIN not available (normal for many EVs)");
            }

            // Reconnect if needed
            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 7: Collect supported PIDs
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Phase 7: Querying supported PIDs...[/]");
            supportedPids = await collector.CollectSupportedPidsAsync(adapter, progress);
            AnsiConsole.MarkupLine($"[green]✓[/] Found {supportedPids.Mode01Pids.Count} Mode 01 PIDs, {supportedPids.Mode09Pids.Count} Mode 09 PIDs");

            // Reconnect if needed
            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 8: Probe standard PIDs
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Phase 8: Probing standard PIDs...[/]");
            AnsiConsole.MarkupLine("[grey]   (This may take a few minutes)[/]");
            await collector.ProbeStandardPidsAsync(adapter, supportedPids, progress);

            // Reconnect if needed
            if (!await EnsureConnectedAsync())
                goto GenerateReport;

            // Phase 9: Probe extended PIDs
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Phase 9: Probing extended/manufacturer PIDs...[/]");
            await collector.ProbeExtendedPidsAsync(adapter, progress);

            // Phase 10: EV-specific CAN probing (if applicable)
            if (isEv)
            {
                // Reconnect if needed
                if (!await EnsureConnectedAsync())
                    goto GenerateReport;

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[cyan]Phase 10: Probing EV-specific CAN addresses...[/]");
                AnsiConsole.MarkupLine($"[grey]   (Probing {userInfo.Make} EV-specific addresses)[/]");
                await collector.ProbeEvCanAddressesAsync(adapter, userInfo.Make, progress);
            }

            if (transport?.IsConnected == true)
            {
                await transport.DisconnectAsync();
                AnsiConsole.MarkupLine("[grey]Disconnected from adapter[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during collection: {ex.Message.EscapeMarkup()}[/]");
            collector.AddError("Collection", ex.Message, ex.ToString());
        }
        finally
        {
            // Ensure transport is disposed
            if (transport != null)
            {
                try
                {
                    await transport.DisconnectAsync();
                }
                catch { /* ignore */ }
                transport.Dispose();
            }
        }

GenerateReport:
// Build and save report
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Generating Report[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var report = collector.BuildReport(userInfo, bleInfo, obdAdapterInfo, vehicleId, supportedPids);

        // Generate markdown using Core library
        var markdown = Core.Diagnostics.MarkdownReportGenerator.Generate(report);

        // Save to file - use Reports subdirectory
        var reportsDir = Path.Combine(Environment.CurrentDirectory, "Reports");
        Directory.CreateDirectory(reportsDir);

        var fileName = $"vehicle_report_{userInfo.Year}_{userInfo.Make}_{userInfo.Model}_{DateTime.Now:yyyyMMdd_HHmmss}.md"
            .Replace(" ", "_")
            .Replace("/", "-");

        var filePath = Path.Combine(reportsDir, fileName);
        await File.WriteAllTextAsync(filePath, markdown);

        // Display summary
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Report Generated Successfully[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        // Show summary table
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Item")
            .AddColumn("Value");

        summaryTable.AddRow("Vehicle", $"{userInfo.Year} {userInfo.Make} {userInfo.Model}");
        summaryTable.AddRow("VIN", vehicleId?.Vin != null ? MaskVin(vehicleId.Vin) : "[grey]Not available[/]");
        summaryTable.AddRow("Adapter", obdAdapterInfo?.VersionResponse?.Trim() ?? "[grey]Unknown[/]");
        summaryTable.AddRow("Working Protocols", $"{report.ProtocolProbeResults.Count(p => p.GotResponse)}/{report.ProtocolProbeResults.Count}");
        summaryTable.AddRow("Mode 01 PIDs", $"{supportedPids?.Mode01Pids.Count ?? 0} supported");
        summaryTable.AddRow("Standard PIDs", $"{report.StandardPidResults.Count(r => r.Success)}/{report.StandardPidResults.Count} responded");
        summaryTable.AddRow("Extended PIDs", $"{report.ExtendedPidResults.Count(r => r.Success)}/{report.ExtendedPidResults.Count} responded");
        if (isEv)
        {
            summaryTable.AddRow("EV CAN Probes", $"{report.CanProbeResults.Count(r => r.Success && !r.Command.StartsWith("ATSH"))}/{report.CanProbeResults.Count(r => !r.Command.StartsWith("ATSH"))} responded");
        }
        summaryTable.AddRow("Errors", report.Errors.Count > 0 ? $"[red]{report.Errors.Count}[/]" : "[green]0[/]");
        summaryTable.AddRow("Report File", fileName);

        AnsiConsole.Write(summaryTable);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]✓[/] Report saved to: [cyan]{filePath.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();

        // Show errors if any
        if (report.Errors.Count > 0)
        {
            AnsiConsole.MarkupLine("[yellow]Errors encountered during collection:[/]");
            foreach (var error in report.Errors)
            {
                AnsiConsole.MarkupLine($"  [red]•[/] {error.Phase}: {error.Message.EscapeMarkup()}");
            }
            AnsiConsole.WriteLine();
        }

        // Instructions
        AnsiConsole.Write(new Panel(
            """
            [yellow]Next Steps:[/]

            1. Open a new issue at: [link]https://github.com/kfrancis/ObdInsight/issues/new[/]
            2. Use the title format: [cyan]Vehicle Support: {Year} {Make} {Model}[/]
            3. Copy the contents of the generated markdown file into the issue
            4. Add any additional observations about your vehicle

            [grey]Thank you for helping improve ObdInsight![/]
            """)
            .Header("[cyan]Submit to GitHub[/]")
            .Border(BoxBorder.Rounded));

        // Offer to open file
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
                AnsiConsole.MarkupLine("[yellow]Could not open file automatically. Please open manually.[/]");
            }
        }
    }

    private static string MaskVin(string vin)
    {
        if (vin.Length <= 6)
            return new string('*', vin.Length);
        return vin[..^6] + "******";
    }

    /// <summary>
    /// Collects BLE adapter information (Windows-specific implementation)
    /// </summary>
    private static async Task<BleAdapterInfo?> CollectBleInfoAsync(string macAddress)
    {
        try
        {
            var mac = ParseMacAddress(macAddress);
            using var device = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromBluetoothAddressAsync(mac);

            if (device == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to connect to BLE device for service discovery[/]");
                return null;
            }

            var servicesResult = await device.GetGattServicesAsync(
                Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);

            if (servicesResult.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
            {
                AnsiConsole.MarkupLine($"[red]Failed to get services: {servicesResult.Status}[/]");
                return null;
            }

            var services = new List<BleServiceInfo>();

            foreach (var service in servicesResult.Services)
            {
                var characteristics = new List<BleCharacteristicInfo>();

                var charsResult = await service.GetCharacteristicsAsync(
                    Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);

                if (charsResult.Status == Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
                {
                    foreach (var characteristic in charsResult.Characteristics)
                    {
                        var props = GetPropertyStrings(characteristic.CharacteristicProperties).ToList();
                        characteristics.Add(new BleCharacteristicInfo
                        {
                            CharacteristicUuid = characteristic.Uuid,
                            Properties = props
                        });
                    }
                }

                services.Add(new BleServiceInfo
                {
                    ServiceUuid = service.Uuid,
                    Characteristics = characteristics
                });

                service.Dispose();
            }

            return new BleAdapterInfo
            {
                DeviceName = device.Name ?? "Unknown",
                MacAddress = macAddress,
                Services = services
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]BLE discovery error: {ex.Message}[/]");
            return null;
        }
    }

    private static UserVehicleInfo CollectUserVehicleInfo()
    {
        AnsiConsole.MarkupLine("[cyan]Please enter your vehicle information:[/]");
        AnsiConsole.WriteLine();

        var year = AnsiConsole.Prompt(
            new TextPrompt<int>("[cyan]Vehicle Year:[/]")
                .DefaultValue(2017)
                .Validate(y => y >= 1996 && y <= DateTime.Now.Year + 1
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Year must be between 1996 and current year")));

        var make = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Make (e.g., Honda, Toyota, Nissan):[/]")
                .DefaultValue("Nissan")
                .Validate(m => !string.IsNullOrWhiteSpace(m)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Make is required")));

        var model = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Model (e.g., CR-V, Camry, Leaf):[/]")
                .DefaultValue("Leaf")
                .Validate(m => !string.IsNullOrWhiteSpace(m)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Model is required")));

        var trim = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Trim (optional, e.g., EX-L, XLE):[/]")
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
            new TextPrompt<string>("[cyan]Additional Notes (optional, any relevant details):[/]")
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
}