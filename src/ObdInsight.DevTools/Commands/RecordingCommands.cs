using ObdInsight.Core;
using ObdInsight.Core.Adapters;
using ObdInsight.Drivers.Adapters.Elm327;
using ObdInsight.Core.Transports.Ble;
using ObdInsight.Core.Transports.Tracing;
using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Commands for recording OBD sessions for replay in tests.
/// </summary>
public static class RecordingCommands
{
    /// <summary>
    /// Record an OBD session for later replay in unit tests.
    /// </summary>
    public static async Task RecordSessionAsync(DevToolsSession session)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Record OBD Session[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[yellow]This tool records all OBD communication for replay in unit tests.[/]");
        AnsiConsole.MarkupLine("[grey]The trace file will be saved in JSONL format.[/]");
        AnsiConsole.WriteLine();

        if (string.IsNullOrEmpty(session.DeviceAddress))
        {
            AnsiConsole.MarkupLine("[red]No device selected. Please scan or set a device first.[/]");
            return;
        }

        // Get session description
        var description = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Session description (optional):[/]")
                .AllowEmpty());

        AnsiConsole.WriteLine();

        // Disconnect any existing connection to use fresh transport with tracing
        await session.DisconnectAsync();

        var profile = session.Profile ?? BleDeviceProfile.VeepeakBle;
        
        // Create transport with recording
        using var baseTransport = new WindowsBleTransport(profile);
        var tracer = new TransportTracer();
        using var transport = new RecordingTransportDecorator(baseTransport, tracer);

        // Setup live traffic logging
        transport.DataSent += (_, data) => LogTraffic("TX", data);
        transport.DataReceived += (_, data) => LogTraffic("RX", data);

        // Connect
        var connected = await AnsiConsole.Status()
            .StartAsync($"Connecting to {session.DeviceName}...", async ctx =>
            {
                return await baseTransport.ConnectAsync(session.DeviceAddress!);
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
            DeviceAddress = session.DeviceAddress,
            DeviceName = session.DeviceName ?? profile.Name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description
        };
        transport.StartRecording(sessionMetadata);

        AnsiConsole.MarkupLine("[yellow]Recording started. All commands will be traced.[/]");
        AnsiConsole.WriteLine();

        // Initialize adapter
        var adapter = new Elm327Adapter();
        adapter.Log += (_, e) => LogAdapter(e);

        var initialized = await AnsiConsole.Status()
            .StartAsync("Initializing ELM327 adapter...", async ctx =>
            {
                return await adapter.InitializeAsync(transport);
            });

        if (initialized)
        {
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
        await RunRecordingLoopAsync(transport, adapter);

        // Stop recording
        var traceSession = transport.StopRecording();

        // Disconnect
        await baseTransport.DisconnectAsync();
        AnsiConsole.MarkupLine("[grey]Disconnected[/]");

        // Save the recording
        await SaveSessionAsync(traceSession);
    }

    private static async Task RunRecordingLoopAsync(RecordingTransportDecorator transport, Elm327Adapter adapter)
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

    private static async Task SaveSessionAsync(TransportSession traceSession)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Recording Complete[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Property")
            .AddColumn("Value");

        summaryTable.AddRow("Session ID", traceSession.SessionId);
        summaryTable.AddRow("Duration", traceSession.Duration.ToString(@"mm\:ss\.fff"));
        summaryTable.AddRow("Total Entries", traceSession.EntryCount.ToString());
        summaryTable.AddRow("Bytes TX", traceSession.TotalBytesTx.ToString());
        summaryTable.AddRow("Bytes RX", traceSession.TotalBytesRx.ToString());
        summaryTable.AddRow("Protocol", traceSession.Metadata.Protocol ?? "[grey]Unknown[/]");
        summaryTable.AddRow("Adapter", traceSession.Metadata.AdapterVersion ?? "[grey]Unknown[/]");

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var defaultName = $"obd_session_{timestamp}.jsonl";

        var fileName = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Save as:[/]")
                .DefaultValue(defaultName));

        if (!fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            fileName += ".jsonl";

        var filePath = Path.Combine(Environment.CurrentDirectory, fileName);

        var serializer = new JsonLTransportSessionSerializer();
        await serializer.SaveAsync(traceSession, filePath);

        AnsiConsole.MarkupLine($"[green]?[/] Session saved to: [cyan]{filePath.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Panel(
            $"""
            [yellow]Usage in Unit Tests:[/]

            ```csharp
            var transport = await ReplayTransportFactory.FromFileAsync("{fileName}");
            var adapter = new Elm327Adapter();
            await adapter.InitializeAsync(transport);
            // Your test assertions here...
            ```
            """)
            .Header("[cyan]Next Steps[/]")
            .Border(BoxBorder.Rounded));

        if (AnsiConsole.Confirm("Open file location?", defaultValue: false))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                AnsiConsole.MarkupLine("[yellow]Could not open file location.[/]");
            }
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
