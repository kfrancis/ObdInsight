using Serilog;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ObdTestApp
{
    internal class Program
    {
        private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(10);

        private sealed record BmsGroup01Data(
            int ByteCount,
            double? CurrentAmps,
            double? CapacityAh,
            double? HxPercent,
            double? SocPercent);

        private static async Task Main(string[] args)
        {
            // Configure Serilog for file logging - create unique log file per run
            // Use the application's directory (bin\Debug\...\Logs) for easier debugging
            var logDir = Path.Combine(AppContext.BaseDirectory, "Logs");
            
            try
            {
                Directory.CreateDirectory(logDir);
                
                // Create unique log filename with date and time for this run
                var runTimestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var logFilePath = Path.Combine(logDir, $"obdtest-{runTimestamp}.log");
                
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.File(
                        logFilePath,
                        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                        flushToDiskInterval: TimeSpan.FromSeconds(1))
                    .CreateLogger();

                AnsiConsole.MarkupLine($"[grey]Log file: {Path.GetFileName(logFilePath).EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine($"[grey]Log directory: {logDir.EscapeMarkup()}[/]");
                
                Log.Information("=== ObdTestApp Started ===");
                Log.Information("Log file: {LogFile}", logFilePath);
                Log.Information("Arguments: {Args}", string.Join(" ", args));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to initialize logging: {ex.Message.EscapeMarkup()}[/]");
                throw;
            }

            // Parse command-line arguments
            var autoConnect = args.Contains("--auto") || !Console.IsInputRedirected && Environment.UserInteractive;
            var targetAddress = args.FirstOrDefault(a => a.StartsWith("--device="))?.Substring("--device=".Length);

            var preferences = DevicePreferences.Load();
            using var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                AnsiConsole.MarkupLine("\n[yellow]Cancelling... Please wait...[/]");
                cts.Cancel();
            };

            try
            {
                // Check for favorite device and auto-connect WITHOUT scanning
                BleDeviceInfo? selectedDevice = null;

                // If specific device address provided via command line
                if (!string.IsNullOrEmpty(targetAddress))
                {
                    selectedDevice = new BleDeviceInfo(
                        "Command-line Device",
                        targetAddress,
                        0,
                        Array.Empty<Guid>());
                    Log.Information("Using device from command line: {Address}", targetAddress);
                    AnsiConsole.MarkupLine($"[green]✓[/] Using device from command line: [cyan]{targetAddress.EscapeMarkup()}[/]");
                }
                else
                {
                    Log.Information("No device address provided via command line");
                }

                // Check for favorite device
                if (selectedDevice == null)
                {
                    var favorite = preferences.GetFavoriteDevice();
                    if (favorite != null)
                    {
                        Log.Information("Found favorite device: {DeviceName} ({Address})", favorite.Name, favorite.Address);
                        AnsiConsole.MarkupLine($"[yellow]★[/] Found favorite device: [cyan]{favorite.Address.EscapeMarkup()}[/]");

                        // Auto-connect without prompting in non-interactive mode or with --auto flag
                        if (!Console.IsInputRedirected || args.Contains("--auto"))
                        {
                            selectedDevice = favorite;
                            Log.Information("Auto-connecting to favorite device (non-interactive or --auto flag)");
                            AnsiConsole.MarkupLine($"[green]✓[/] Auto-connecting to favorite device (no scan required)");
                        }
                        else if (AnsiConsole.Confirm("Auto-connect to favorite?", defaultValue: true))
                        {
                            selectedDevice = favorite;
                            Log.Information("User confirmed auto-connect to favorite device");
                            AnsiConsole.MarkupLine($"[green]✓[/] Auto-connecting to favorite device (no scan required)");
                        }
                        else
                        {
                            Log.Information("User declined auto-connect to favorite device");
                        }
                    }
                    else
                    {
                        Log.Information("No favorite device found in preferences");
                    }
                }

                // If no favorite or user declined, do normal scan and select
                if (selectedDevice == null)
                {
                    Log.Information("Starting device scan and selection");
                    selectedDevice = await ScanAndSelectDeviceAsync(preferences, cts.Token);
                    if (selectedDevice == null)
                    {
                        Log.Information("No device selected by user. Exiting.");
                        AnsiConsole.MarkupLine("[yellow]No device selected. Exiting.[/]");
                        return;
                    }
                    Log.Information("Device selected: {DeviceName} ({Address})", selectedDevice.Name, selectedDevice.Address);
                }

                // Run with automatic retry on failure
                Log.Information("Starting session with device: {DeviceName} ({Address})", selectedDevice.Name, selectedDevice.Address);
                await RunWithRetryAsync(selectedDevice, preferences, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Information("Operation cancelled by user");
                AnsiConsole.MarkupLine("[yellow]Operation cancelled by user.[/]");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Fatal error: {Message}", ex.Message);
                AnsiConsole.MarkupLine($"[red]Fatal error:[/] {ex.Message.EscapeMarkup()}");
                if (ex.InnerException != null)
                {
                    AnsiConsole.MarkupLine($"[grey]  Inner: {ex.InnerException.Message.EscapeMarkup()}[/]");
                }
            }
            finally
            {
                Log.Information("=== ObdTestApp Exiting ===");
                await Log.CloseAndFlushAsync();
            }
        }

        /// <summary>
        /// Runs the ELM327 session with automatic retry on connection failure
        /// </summary>
        private static async Task RunWithRetryAsync(BleDeviceInfo selectedDevice, DevicePreferences preferences, CancellationToken ct)
        {
            int failureCount = 0;
            const int maxFailures = 5;
            
            while (!ct.IsCancellationRequested && failureCount < maxFailures)
            {
                try
                {
                    await RunElm327SessionAsync(selectedDevice, ct);
                    
                    // If we get here, session ended normally
                    break;
                }
                catch (IOException ex) when (!ct.IsCancellationRequested)
                {
                    failureCount++;
                    Log.Warning(ex, "Connection failure #{FailureCount}/{MaxFailures} - {Message}", failureCount, maxFailures, ex.Message);
                    AnsiConsole.MarkupLine($"[red]Connection failure #{failureCount}/{maxFailures}:[/] {ex.Message.EscapeMarkup()}");
                    
                    if (failureCount < maxFailures)
                    {
                        var retryDelay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failureCount)));
                        Log.Information("Retrying in {RetryDelay} seconds (attempt {NextAttempt})", retryDelay.TotalSeconds, failureCount + 1);
                        AnsiConsole.MarkupLine($"[yellow]Retrying in {retryDelay.TotalSeconds:F0}s...[/]");
                        
                        await Task.Delay(retryDelay, ct);
                        Log.Information("Starting retry attempt {Attempt}", failureCount + 1);
                        AnsiConsole.MarkupLine($"[cyan]Retry attempt {failureCount + 1}...[/]");
                    }
                    else
                    {
                        Log.Error("Max retry attempts ({MaxFailures}) reached. Prompting for rescan.", maxFailures);
                        AnsiConsole.MarkupLine($"[red]Max retry attempts ({maxFailures}) reached. Giving up.[/]");
                        
                        // Ask if user wants to rescan
                        if (AnsiConsole.Confirm("Scan for devices again?", defaultValue: true))
                        {
                            Log.Information("User requested rescan");
                            var newDevice = await ScanAndSelectDeviceAsync(preferences, ct);
                            if (newDevice != null)
                            {
                                Log.Information("New device selected: {DeviceName} ({Address})", newDevice.Name, newDevice.Address);
                                selectedDevice = newDevice;
                                failureCount = 0; // Reset counter for new device
                                continue;
                            }
                        }
                        else
                        {
                            Log.Information("User declined rescan");
                        }
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Log.Error(ex, "Unexpected error during session: {Message}", ex.Message);
                    AnsiConsole.MarkupLine($"[red]Unexpected error:[/] {ex.Message.EscapeMarkup()}");
                    throw;
                }
            }
        }

        private static async Task<BleDeviceInfo?> ScanAndSelectDeviceAsync(
            DevicePreferences preferences,
            CancellationToken ct)
        {
            using var scanner = new BleScanner();

            while (true)
            {
                var devices = await PerformScanAsync(scanner, ct);

                if (devices.Count == 0)
                {
                    Log.Warning("No BLE devices found during scan");
                    if (AnsiConsole.Confirm("No BLE devices found. Rescan?", defaultValue: true))
                    {
                        Log.Information("User requested rescan");
                        continue;
                    }

                    Log.Information("User declined rescan. Returning null.");
                    return null;
                }

                var orderedDevices = devices.Values
                    .OrderByDescending(d => d.Rssi)
                    .ToList();

                Log.Information("Found {DeviceCount} BLE devices", orderedDevices.Count);
                RenderDeviceTable(orderedDevices, preferences);

                var favorite = preferences.GetPreferredDevice(orderedDevices);

                var actions = new List<string>();
                if (favorite != null)
                    actions.Add($"Connect to favorite ({favorite.Name})");

                actions.Add("Choose device from list");
                actions.Add("Rescan");
                actions.Add("Cancel");

                var action = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[cyan]Select an action:[/]")
                        .AddChoices(actions));

                if (action.StartsWith("Connect to favorite", StringComparison.OrdinalIgnoreCase) && favorite != null)
                {
                    Log.Information("User selected favorite device: {DeviceName} ({Address})", favorite.Name, favorite.Address);
                    preferences.RememberDevice(favorite, markAsFavorite: true);
                    AnsiConsole.MarkupLine($"[green]✓[/] Selected: [cyan]{favorite.Name}[/] ({favorite.Address})");
                    return favorite;
                }

                if (action.Equals("Rescan", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("User requested rescan");
                    continue;
                }

                if (action.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("User cancelled device selection");
                    return null;
                }

                var defaultSelection = favorite != null
                    ? orderedDevices.IndexOf(favorite) + 1
                    : 1;

                var selection = AnsiConsole.Prompt(
                    new TextPrompt<int>("[cyan]Enter device number:[/]")
                        .DefaultValue(defaultSelection)
                        .Validate(n => n >= 1 && n <= orderedDevices.Count
                            ? ValidationResult.Success()
                            : ValidationResult.Error($"Enter a number between 1 and {orderedDevices.Count}")));

                var selectedDevice = orderedDevices[selection - 1];

                var shouldFavorite = preferences.IsFavorite(selectedDevice) ||
                    AnsiConsole.Confirm($"Mark {selectedDevice.Name} as a favorite?", defaultValue: false);

                Log.Information("User selected device #{Number}: {DeviceName} ({Address}), Favorite={IsFavorite}", 
                    selection, selectedDevice.Name, selectedDevice.Address, shouldFavorite);
                preferences.RememberDevice(selectedDevice, shouldFavorite);

                AnsiConsole.MarkupLine($"[green]✓[/] Selected: [cyan]{selectedDevice.Name}[/] ({selectedDevice.Address})");
                return selectedDevice;
            }
        }

        private static async Task<Dictionary<string, BleDeviceInfo>> PerformScanAsync(BleScanner scanner, CancellationToken ct)
        {
            var devices = new Dictionary<string, BleDeviceInfo>(StringComparer.OrdinalIgnoreCase);

            void OnDevice(object? _, BleDeviceDiscoveredEventArgs args)
            {
                devices[args.Device.Address] = args.Device;
            }

            scanner.DeviceDiscovered += OnDevice;

            try
            {
                Log.Information("Starting BLE scan for {Duration} seconds", ScanDuration.TotalSeconds);
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"Scanning for BLE devices ({ScanDuration.TotalSeconds:0}s)...", async _ =>
                    {
                        await scanner.StartScanAsync(cancellationToken: ct);
                        try
                        {
                            await Task.Delay(ScanDuration, ct);
                        }
                        finally
                        {
                            await scanner.StopScanAsync();
                        }
                    });

                Log.Information("BLE scan completed. Found {DeviceCount} devices", devices.Count);
                return devices;
            }
            finally
            {
                scanner.DeviceDiscovered -= OnDevice;
            }
        }

        private static void RenderDeviceTable(IReadOnlyList<BleDeviceInfo> devices, DevicePreferences preferences)
        {
            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("#")
                .AddColumn("Name")
                .AddColumn("Address")
                .AddColumn("RSSI")
                .AddColumn("Tags");

            for (var i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                var tags = string.Concat(
                    preferences.IsFavorite(device) ? "[yellow]★[/]" : string.Empty,
                    preferences.IsSaved(device) ? "[green]✔[/]" : string.Empty);

                if (string.IsNullOrEmpty(tags))
                    tags = "-";

                table.AddRow(
                    (i + 1).ToString(),
                    device.Name.EscapeMarkup(),
                    $"[cyan]{device.Address}[/]",
                    $"[{GetRssiColor(device.Rssi)}]{device.Rssi} dBm[/]",
                    tags);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[grey]Found {devices.Count} devices ([yellow]★[/]=favorite, [green]✔[/]=saved)[/]");
            AnsiConsole.WriteLine();
        }

        private static string GetRssiColor(int rssi) => rssi switch
        {
            > -50 => "green",
            > -70 => "yellow",
            _ => "red"
        };

        /// <summary>
        /// Safely writes text to Spectre.Console by escaping markup characters.
        /// </summary>
        private static void SafeWrite(string text)
        {
            AnsiConsole.Write(text.EscapeMarkup());
        }

        /// <summary>
        /// Safely writes a line to Spectre.Console by escaping markup characters.
        /// </summary>
        private static void SafeWriteLine(string text)
        {
            AnsiConsole.WriteLine(text.EscapeMarkup());
        }

        private static async Task RunElm327SessionAsync(BleDeviceInfo selectedDevice, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(selectedDevice);

            var sessionStart = DateTime.UtcNow;
            var successfulQueries = 0;
            var failedQueries = 0;
            var invalidResponseQueries = 0; // Queries that completed but returned invalid/empty data

            Log.Information("=== Starting ELM327 session ===");
            Log.Information("Connecting to device: {DeviceName} ({Address}), RSSI={Rssi}", 
                selectedDevice.Name, selectedDevice.Address, selectedDevice.Rssi);
            
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[cyan]Connecting to:[/] {selectedDevice.Name.EscapeMarkup()} [grey]({selectedDevice.Address.EscapeMarkup()})[/]");

            await using var transport = new BleElmTransport(selectedDevice.Address);
            
            // Enable debug logging
            //transport.EnableDebugLogging = true;
            
            try
            {
                Log.Information("Opening BLE transport");
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Establishing BLE connection...", async ctx =>
                    {
                        await transport.OpenAsync(ct);
                    });
                
                Log.Information("BLE transport opened successfully");
                AnsiConsole.MarkupLine("[green]✓[/] Bluetooth connected.");
                AnsiConsole.WriteLine();

                var framer = new ElmFramer(transport);
                
                var session = new ElmSession(framer)
                {
                    CommandTimeout = TimeSpan.FromSeconds(5),
                    MaxConsecutiveFailures = 3
                };

                Log.Information("Initializing ELM327 session (Timeout={CommandTimeout}s, MaxFailures={MaxFailures})", 
                    session.CommandTimeout.TotalSeconds, session.MaxConsecutiveFailures);
                AnsiConsole.MarkupLine("[yellow]Initializing ELM327 session...[/]");
                await session.InitializeAndLockAsync(ct);
                Log.Information("ELM327 session initialized and protocol locked");
                AnsiConsole.MarkupLine("[green]✓[/] Session initialized and protocol locked.");
                
                // Display connection info
                var infoPanel = new Panel(new Markup(
                    $"[cyan]Device:[/] {selectedDevice.Name.EscapeMarkup()}\n" +
                    $"[cyan]Address:[/] {selectedDevice.Address.EscapeMarkup()}\n" +
                    $"[cyan]RSSI:[/] {selectedDevice.Rssi} dBm\n" +
                    $"[cyan]Debug Logging:[/] Enabled\n" +
                    $"[cyan]Command Timeout:[/] {session.CommandTimeout.TotalSeconds}s\n" +
                    $"[cyan]Session Start:[/] {sessionStart:HH:mm:ss}"))
                {
                    Header = new PanelHeader("[green]Connection Established[/]"),
                    Border = BoxBorder.Rounded
                };
                AnsiConsole.Write(infoPanel);

                // Configure ELM327 for Nissan Leaf BMS communication
                // BMS uses addresses: TX=0x79B, RX=0x7BB
                Log.Information("Configuring ELM327 for Nissan Leaf BMS (79B/7BB)");
                
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Panel(
                    "[yellow]IMPORTANT: Vehicle must be in one of these states:[/]\n" +
                    "[green]1. READY mode[/] (Press brake + Start button) - [cyan]BEST[/]\n" +
                    "[green]2. Charging[/] (Plugged in and charging)\n" +
                    "[green]3. ACC mode[/] (Start button without brake) - [yellow]May work[/]\n\n" +
                    "[red]If car is completely OFF, you will get NO DATA.[/]")
                {
                    Header = new PanelHeader("[yellow]⚠ Nissan Leaf Communication Requirements[/]"),
                    Border = BoxBorder.Rounded
                });
                AnsiConsole.WriteLine();
                
                AnsiConsole.MarkupLine("[yellow]Waking up ECUs and configuring BMS...[/]");

                //var wakeupAttempts = new (string Cmd, string Desc)[]
                //{
                //    // Try sending to VCM wakeup address
                //    ($"}", "Set header to VCM wakeup (0x679)"),
                //    ("00", "Send empty wakeup byte"),

                //    // Try battery heater spoof
                //    ($"ATSH{BATTERY_HEATER_WAKEUP_ID:X3}", "Set header to battery heater (0x5C0)"),
                //    ("0000000000000000", "Send 8-byte empty message"),

                //    // Try broadcast
                //    ($"ATSH{BROADCAST_TXID:X3}", "Set header to broadcast (0x7DF)"),
                //    ("0100", "Send Mode 01 PID 00 (supported PIDs)"),
                //};

                // Try sending to VCM wakeup address
                Log.Information("Sending VCM wakeup (679)");
                await framer.SendAndReadFrameAsync("ATSH679", session.CommandTimeout, ct);
                var vcmWakeupResponse = await framer.SendAndReadFrameAsync("00", session.CommandTimeout, ct);

                // Try battery heater spoof
                Log.Information("Sending battery heater spoof (5C0)");
                await framer.SendAndReadFrameAsync("ATSH5C0", session.CommandTimeout, ct);
                var battHeaterWakeupResponse = await framer.SendAndReadFrameAsync("00000000", session.CommandTimeout, ct);

                // CRITICAL: First wake up the ECUs by sending to broadcast address
                // This helps when ECUs are sleeping (car OFF but accessory on)
                Log.Information("Sending wakeup to broadcast address (7DF)");
                await framer.SendAndReadFrameAsync("ATSH7DF", session.CommandTimeout, ct);
                var wakeupResponse = await framer.SendAndReadFrameAsync("0100", session.CommandTimeout, ct);
                
                if (wakeupResponse.Contains("NO DATA", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("Wakeup query returned NO DATA - ECUs may be sleeping");
                    AnsiConsole.MarkupLine("[red]⚠[/] [yellow]Wakeup query returned NO DATA - ECUs appear to be sleeping![/]");
                    AnsiConsole.MarkupLine("[yellow]  → Make sure car is in READY mode or charging[/]");
                    AnsiConsole.WriteLine();
                }
                else
                {
                    Log.Information("Wakeup query succeeded - ECUs responding");
                    AnsiConsole.MarkupLine("[green]✓[/] ECUs responded to wakeup");
                }
                
                await Task.Delay(200, ct); // Wait for ECUs to wake up
                
                // Now configure for BMS - CRITICAL: Disable CAN auto-formatting
                Log.Information("Configuring ISO-TP for communication");
                AnsiConsole.MarkupLine("[grey]  Configuring ISO-TP and headers...[/]");

                // Turn on headers
                await framer.SendAndReadFrameAsync("ATH1", session.CommandTimeout, ct);

                // Set automatic formatting on
                await framer.SendAndReadFrameAsync("ATCAF1", session.CommandTimeout, ct);
                
                // Configure ISO-TP flow control for multi-frame responses                
                await framer.SendAndReadFrameAsync("ATFCSD300000", session.CommandTimeout, ct);
                await framer.SendAndReadFrameAsync("ATFCSM1", session.CommandTimeout, ct);
                
                Log.Information("Nissan Leaf BMS configuration complete");
                AnsiConsole.MarkupLine("[green]✓[/] BMS headers configured (79B/7BB) with ISO-TP flow control");

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[cyan]Querying Nissan Leaf battery data (Ctrl+C to exit)...[/]");
                AnsiConsole.MarkupLine("[grey]Using Mode 21 queries to Li-ion Battery Controller (LBC)[/]");
                AnsiConsole.WriteLine();

                Log.Information("Starting Nissan Leaf battery data query loop (Mode 21)");
                var lastStatsDisplay = DateTime.UtcNow;

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Check cancellation before each query
                        ct.ThrowIfCancellationRequested();

                        // Set BMS addresses
                        await framer.SendAndReadFrameAsync("ATSH79B", session.CommandTimeout, ct);
                        await framer.SendAndReadFrameAsync("ATCRA7BB", session.CommandTimeout, ct);
                        await framer.SendAndReadFrameAsync("ATFCSH79B", session.CommandTimeout, ct);

                        // Query Nissan Leaf BMS using Mode 21 Group 1 (2101)
                        // Response: 61 01 followed by battery data (SOC, voltage, current, temps, etc.)
                        Log.Debug("Querying Nissan Leaf BMS Group 1 (2101) - SOC, Voltage, Current, Temps");
                        var group1Lines = await session.QueryAsync("2101", ct);
                        Log.Debug("Group 1 query returned {LineCount} lines: {Lines}", group1Lines.Length, string.Join(", ", group1Lines));
                        
                        // Look for response starting with "61 01" or containing "7BB" (BMS response ID)
                        var group1Line = group1Lines.FirstOrDefault(l =>
                            l.Contains("61 01", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("7BB", StringComparison.OrdinalIgnoreCase));

                        if (group1Line != null)
                        {
                            if (TryParseBmsGroup01(group1Line, out var group01))
                            {
                                var parts = new List<string>();

                                if (group01?.CurrentAmps is double currentAmps)
                                {
                                    var currentDir = currentAmps > 0 ? "discharging" : (currentAmps < 0 ? "charging" : "idle");
                                    parts.Add($"BMS I: {Math.Abs(currentAmps):F1}A ({currentDir})");
                                }

                                if (group01?.SocPercent is double soc)
                                    parts.Add($"SOC: {soc:F1}%");

                                if (group01?.CapacityAh is double capacityAh)
                                    parts.Add($"CAC: {capacityAh:F2}Ah");

                                if (group01?.HxPercent is double hx)
                                    parts.Add($"HX: {hx:F1}%");

                                if (parts.Count == 0)
                                {
                                    SafeWrite($"BMS G01: {group01?.ByteCount ?? 0}B  ");
                                }
                                else
                                {
                                    SafeWrite(string.Join("  ", parts) + "  ");
                                }

                                successfulQueries++;
                            }
                            else
                            {
                                Log.Debug("BMS Group 1 query returned invalid/unexpected response");
                                invalidResponseQueries++;
                            }
                        }
                        else
                        {
                            Log.Debug("BMS Group 1 query returned no valid response");
                            invalidResponseQueries++;
                        }

                        ct.ThrowIfCancellationRequested();
                        
                        // Query Mode 21 Group 2 (2102) - Additional battery data
                        Log.Debug("Querying Nissan Leaf BMS Group 2 (2102)");
                        var group2Lines = await session.QueryAsync("2102", ct);
                        Log.Debug("Group 2 query returned {LineCount} lines: {Lines}", group2Lines.Length, string.Join(", ", group2Lines));
                        
                        var group2Line = group2Lines.FirstOrDefault(l =>
                            l.Contains("61 02", StringComparison.OrdinalIgnoreCase) ||
                            l.Contains("7BB", StringComparison.OrdinalIgnoreCase));

                        if (group2Line != null)
                        {
                            SafeWrite($"Group2: {group2Line.Substring(0, Math.Min(30, group2Line.Length))}  ");
                            successfulQueries++;
                        }
                        else
                        {
                            Log.Debug("BMS Group 2 query returned no valid response");
                            invalidResponseQueries++;
                        }

                        ct.ThrowIfCancellationRequested();

                        // Switch to charger
                        await framer.SendAndReadFrameAsync($"ATSH797", session.CommandTimeout, ct);
                        await framer.SendAndReadFrameAsync($"ATCRA79A", session.CommandTimeout, ct);
                        await framer.SendAndReadFrameAsync($"ATFCSH797", session.CommandTimeout, ct);
                        await framer.SendAndReadFrameAsync("ATFCSD300000", session.CommandTimeout, ct);
                        await framer.SendAndReadFrameAsync("ATFCSM1", session.CommandTimeout, ct);

                        // Try standard OBD-II VIN query (Mode 09 PID 02) as fallback to verify communication
                        Log.Debug("Querying VIN (2181)");
                        var vinLines = await session.QueryAsync("2181", ct);
                        
                        Log.Debug("VIN query returned {LineCount} lines: {Lines}", vinLines.Length, string.Join(", ", vinLines));
                        
                        var vinResponse = string.Join("\n", vinLines);
                        if (vinLines.Length > 0 && TryParseVin(vinResponse, out var vin))
                        {
                            SafeWrite($"VIN: {vin}");
                            successfulQueries++;
                        }
                        else
                        {
                            Log.Debug("VIN query returned no valid response");
                            invalidResponseQueries++;
                        }

                        AnsiConsole.WriteLine();
                        
                        // Display statistics every 30 seconds
                        if (DateTime.UtcNow - lastStatsDisplay > TimeSpan.FromSeconds(30))
                        {
                            lastStatsDisplay = DateTime.UtcNow;
                            var uptime = DateTime.UtcNow - sessionStart;
                            var totalAttempts = successfulQueries + failedQueries + invalidResponseQueries;
                            var successRate = totalAttempts > 0 
                                ? (double)successfulQueries / totalAttempts * 100 
                                : 0;
                            
                            AnsiConsole.MarkupLine($"[grey]Stats: Uptime={uptime:hh\\:mm\\:ss}, Success={successfulQueries}, Invalid={invalidResponseQueries}, Failed={failedQueries}, Rate={successRate:F1}%[/]");
                            Log.Information("Session stats - Uptime={Uptime}, Success={Success}, Invalid={Invalid}, Failed={Failed}, Rate={Rate:F1}%", 
                                uptime, successfulQueries, invalidResponseQueries, failedQueries, successRate);
                        }
                        
                        // Check cancellation before delay
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(250, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // User pressed Ctrl+C - exit cleanly
                        Log.Information("Data collection cancelled by user");
                        AnsiConsole.MarkupLine("\n[yellow]Stopping data collection...[/]");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failedQueries++;
                        AnsiConsole.MarkupLine($"[red]Error reading data:[/] {ex.Message.EscapeMarkup()}");
                        
                        // Log error using Serilog
                        LogError(ex, successfulQueries, failedQueries, DateTime.UtcNow - sessionStart);
                        
                        // Check cancellation before retry delay
                        if (!ct.IsCancellationRequested)
                            await Task.Delay(1000, ct);
                    }
                }
            }
            finally
            {
                // Final statistics
                var totalUptime = DateTime.UtcNow - sessionStart;
                var totalQueries = successfulQueries + failedQueries + invalidResponseQueries;
                var finalSuccessRate = totalQueries > 0 ? (double)successfulQueries / totalQueries * 100 : 0;
                
                AnsiConsole.WriteLine();
                var statsPanel = new Panel(new Markup(
                    $"[cyan]Total Uptime:[/] {totalUptime:hh\\:mm\\:ss}\n" +
                    $"[cyan]Successful Queries:[/] {successfulQueries}\n" +
                    $"[cyan]Invalid Response Queries:[/] {invalidResponseQueries}\n" +
                    $"[cyan]Failed Queries:[/] {failedQueries}\n" +
                    $"[cyan]Success Rate:[/] {finalSuccessRate:F1}%\n" +
                    $"[cyan]Queries/Min:[/] {(totalQueries / totalUptime.TotalMinutes):F1}"))
                {
                    Header = new PanelHeader("[yellow]Session Statistics[/]"),
                    Border = BoxBorder.Rounded
                };
                AnsiConsole.Write(statsPanel);
                
                // Log session summary
                LogSessionSummary(selectedDevice, sessionStart, totalUptime, successfulQueries, invalidResponseQueries, failedQueries);
            }
        }

        /// <summary>
        /// Logs errors using Serilog for proper file logging and diagnostics
        /// </summary>
        private static void LogError(Exception ex, int successCount, int failCount, TimeSpan uptime)
        {
            Log.Error(ex, "Query error - Uptime={Uptime}, SuccessCount={SuccessCount}, FailCount={FailCount}", 
                uptime, successCount, failCount);
        }

        private static bool TryParseBmsGroup01(string response, out BmsGroup01Data? data)
        {
            data = null;
            try
            {
                var bytes = ParseIsoTpResponse(response);

                if (bytes.Count < 2)
                {
                    AnsiConsole.MarkupLine("[yellow]   Parse: Not enough bytes for Group 01[/]");
                    return false;
                }

                // First 2 bytes should be 61 01 (positive response to 21 01)
                if (bytes[0] != 0x61 || bytes[1] != 0x01)
                {
                    AnsiConsole.MarkupLine($"[yellow]   Parse: Unexpected response header: {bytes[0]:X2} {bytes[1]:X2}[/]");
                    return false;
                }

                // NOTE: Offsets derived from OVMS Nissan Leaf implementation (`vehicle_nissanleaf.cpp`):
                // - ZE0/AZE0: hx=@[26..27]/100, ah10000=@[33..35]/10000
                // - ZE1:      hx=@[28..29]/102.4, soc10000=@[31..33]/10000, ah10000=@[35..37]/10000

                double? currentAmps = null;
                double? capacityAh = null;
                double? hxPercent = null;
                double? socPercent = null;

                // Current is the first 4 bytes after the 61 01 header on both variants.
                if (bytes.Count >= 6)
                {
                    uint currentUnsigned = ((uint)bytes[2] << 24) | ((uint)bytes[3] << 16) | ((uint)bytes[4] << 8) | bytes[5];
                    int currentRaw = unchecked((int)currentUnsigned);
                    var candidateCurrentAmps = currentRaw / 2.0;
                    if (Math.Abs(candidateCurrentAmps) < 1000 && currentRaw != -1)
                        currentAmps = candidateCurrentAmps;
                }

                if (bytes.Count >= 41)
                {
                    // ZE1 (51 bytes typical, but can be shorter early)
                    if (bytes.Count >= 30)
                    {
                        var hxRaw = (bytes[28] << 8) | bytes[29];
                        hxPercent = hxRaw / 102.4;
                    }

                    if (bytes.Count >= 34)
                    {
                        var socRaw = (bytes[31] << 16) | (bytes[32] << 8) | bytes[33];
                        socPercent = socRaw / 10000.0;
                    }

                    if (bytes.Count >= 38)
                    {
                        var ah10000 = (bytes[35] << 16) | (bytes[36] << 8) | bytes[37];
                        capacityAh = ah10000 / 10000.0;
                    }
                }
                else
                {
                    // ZE0/AZE0
                    if (bytes.Count >= 28)
                    {
                        var hxRaw = (bytes[26] << 8) | bytes[27];
                        hxPercent = hxRaw / 100.0;
                    }

                    if (bytes.Count >= 36)
                    {
                        var ah10000 = (bytes[33] << 16) | (bytes[34] << 8) | bytes[35];
                        capacityAh = ah10000 / 10000.0;
                    }
                }

                data = new BmsGroup01Data(bytes.Count, currentAmps, capacityAh, hxPercent, socPercent);

                // Structured logging (avoid spamming console output):
                Log.Debug(
                    "Parsed BMS Group 01 - Bytes={ByteCount}, CurrentA={CurrentAmps}, SocPct={SocPercent}, CapacityAh={CapacityAh}, HxPct={HxPercent}",
                    data.ByteCount, data.CurrentAmps, data.SocPercent, data.CapacityAh, data.HxPercent);
                return true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
                return false;
            }
        }

        /// <summary>
        /// Logs session summary using Serilog for proper file logging and analysis
        /// </summary>
        private static void LogSessionSummary(BleDeviceInfo device, DateTime start, TimeSpan duration, int success, int invalid, int failed)
        {
            var total = success + invalid + failed;
            var successRate = total > 0 ? (double)success / total * 100 : 0;
            Log.Information("Session completed - Device={DeviceName}({DeviceAddress}), Start={StartTime}, Duration={Duration}, SuccessCount={SuccessCount}, InvalidCount={InvalidCount}, FailCount={FailCount}, SuccessRate={SuccessRate:F1}%",
                device.Name, device.Address, start, duration, success, invalid, failed, successRate);
        }

        /// <summary>
        /// Parse ISO-TP response, handling multi-frame messages.
        /// Handles both spaced and concatenated hex formats from ELM327.
        /// </summary>
        private static List<byte> ParseIsoTpResponse(string response)
        {
            var bytes = new List<byte>();

            if (string.IsNullOrWhiteSpace(response))
                return bytes;

            var cleaned = response
                .Replace("\r", "\n")
                .Replace(">", "")
                .Trim();

            var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var frameSequence = new List<(int Type, int Seq, byte[] Data)>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length < 6) continue;

                if (!IsCanIdPrefix(trimmed))
                    continue;

                var frameHex = trimmed[3..];

                if (frameHex.Length < 2) continue;

                if (!byte.TryParse(frameHex[..2], System.Globalization.NumberStyles.HexNumber, null, out var frameTypeByte))
                    continue;

                var frameType = (frameTypeByte & 0xF0) >> 4;
                var frameInfo = frameTypeByte & 0x0F;

                byte[] frameData;

                switch (frameType)
                {
                    case 0:
                        var sfLen = frameInfo;
                        var sfDataHex = frameHex[2..];
                        frameData = ParseHexString(sfDataHex);
                        if (frameData.Length > sfLen)
                            frameData = frameData[..sfLen];
                        frameSequence.Add((0, 0, frameData));
                        break;

                    case 1:
                        if (frameHex.Length < 4) continue;
                        if (!byte.TryParse(frameHex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var lenLowByte))
                            continue;
                        var ffDataHex = frameHex[4..];
                        frameData = ParseHexString(ffDataHex);
                        frameSequence.Add((1, 0, frameData));
                        break;

                    case 2:
                        var seqNum = frameInfo;
                        var cfDataHex = frameHex[2..];
                        frameData = ParseHexString(cfDataHex);
                        frameSequence.Add((2, seqNum, frameData));
                        break;

                    default:
                        frameData = ParseHexString(frameHex);
                        if (frameData.Length > 0)
                            frameSequence.Add((-1, 0, frameData));
                        break;
                }
            }

            var firstFrame = frameSequence.FirstOrDefault(f => f.Type == 0 || f.Type == 1);
            if (firstFrame.Data != null)
            {
                bytes.AddRange(firstFrame.Data);
            }

            var consecutiveFrames = frameSequence
                .Where(f => f.Type == 2)
                .OrderBy(f => f.Seq)
                .ToList();

            foreach (var cf in consecutiveFrames)
            {
                bytes.AddRange(cf.Data);
            }

            if (bytes.Count == 0)
            {
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.All(c => Uri.IsHexDigit(c)))
                    {
                        bytes.AddRange(ParseHexString(trimmed));
                    }
                }
            }

            return bytes;
        }

        private static bool IsCanIdPrefix(string s)
        {
            if (s.Length < 3) return false;
            var prefix = s[..3];
            return prefix.All(c => Uri.IsHexDigit(c)) &&
                   int.TryParse(prefix, System.Globalization.NumberStyles.HexNumber, null, out var id) &&
                   id >= 0x700 && id <= 0x7FF;
        }

        private static byte[] ParseHexString(string hex)
        {
            var result = new List<byte>();
            for (int i = 0; i + 1 < hex.Length; i += 2)
            {
                if (byte.TryParse(hex.Substring(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    result.Add(b);
                }
                else
                {
                    break;
                }
            }
            return result.ToArray();
        }

        /// <summary>
        /// Parse VIN from charger response.
        /// From 2017 Leaf: 79A10156181314E3442\r79A215A304350334843\r79A2233313034303800
        /// Decoded: 61 81 31 4E 34 42 5A 30 43 50 33 48 43 33 31 30 34 30 38 00
        ///        = "1N4BZ0CP3HC310408" (example)
        /// </summary>
        private static bool TryParseVin(string response, out string? vin)
        {
            vin = null;
            try
            {
                var bytes = ParseIsoTpResponse(response);

                AnsiConsole.MarkupLine($"[grey]   Parsed {bytes.Count} bytes[/]");

                if (bytes.Count < 5)
                {
                    AnsiConsole.MarkupLine("[yellow]   Not enough data for VIN[/]");
                    return false;
                }

                // Show raw for debugging
                if (bytes.Count <= 25)
                {
                    AnsiConsole.MarkupLine($"[grey]   Raw: {BitConverter.ToString(bytes.ToArray())}[/]");
                }

                // Find response header 61 81 (positive response to 21 81)
                int vinStart = -1;
                for (int i = 0; i < bytes.Count - 1; i++)
                {
                    if (bytes[i] == 0x61 && bytes[i + 1] == 0x81)
                    {
                        vinStart = i + 2; // VIN starts after header
                        break;
                    }
                }

                if (vinStart >= 0)
                {
                    // Extract up to 17 characters for VIN
                    var vinBytes = bytes.Skip(vinStart).Take(17).ToArray();

                    // Convert to ASCII, filtering out non-printable
                    var vinChars = vinBytes
                        .Where(b => b >= 0x20 && b < 0x7F)
                        .Select(b => (char)b)
                        .ToArray();

                    var candidateVin = new string(vinChars).Trim('\0', ' ');

                    if (candidateVin.Length >= 10)
                    {
                        vin = candidateVin;
                        AnsiConsole.MarkupLine($"   [green]VIN: {vin}[/]");
                        DecodeVin(vin);
                        return true;
                    }
                }

                // Alternative: try to find ASCII printable characters
                var allPrintable = bytes
                    .Where(b => b >= 0x30 && b <= 0x5A) // 0-9, A-Z
                    .Select(b => (char)b)
                    .ToArray();

                if (allPrintable.Length >= 10)
                {
                    var rawVin = new string(allPrintable);
                    // Take first 17 VIN characters
                    if (rawVin.Length > 17)
                        rawVin = rawVin[..17];

                    vin = rawVin;
                    AnsiConsole.MarkupLine($"   [green]VIN: {vin}[/]");
                    DecodeVin(vin);
                    return true;
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]   Could not extract VIN[/]");
                    return false;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
                return false;
            }
        }

        /// <summary>
        /// Decode VIN information for Nissan Leaf.
        /// </summary>
        private static void DecodeVin(string vin)
        {
            if (string.IsNullOrEmpty(vin) || vin.Length < 10)
                return;

            // World Manufacturer Identifier (first 3 chars)
            var wmi = vin[..3];
            var manufacturer = wmi switch
            {
                "1N4" => "Nissan (USA - Smyrna, TN)",
                "JN1" => "Nissan (Japan)",
                "SJN" => "Nissan (UK - Sunderland)",
                "VNK" => "Nissan (France)",
                _ => $"Unknown ({wmi})"
            };
            AnsiConsole.MarkupLine($"   [grey]   Manufacturer: {manufacturer}[/]");

            // Vehicle attributes (chars 4-8)
            if (vin.Length >= 5)
            {
                var modelCode = vin.Substring(3, 2);
                var model = modelCode switch
                {
                    "BZ" => "Leaf (BEV)",
                    "AZ" => "Leaf (BEV)",
                    _ => $"Model code: {modelCode}"
                };
                AnsiConsole.MarkupLine($"   [grey]   Model: {model}[/]");
            }

            // Model year (10th character)
            if (vin.Length >= 10)
            {
                var yearChar = vin[9];
                var year = yearChar switch
                {
                    'A' => 2010,
                    'B' => 2011,
                    'C' => 2012,
                    'D' => 2013,
                    'E' => 2014,
                    'F' => 2015,
                    'G' => 2016,
                    'H' => 2017,
                    'J' => 2018,
                    'K' => 2019,
                    'L' => 2020,
                    'M' => 2021,
                    'N' => 2022,
                    'P' => 2023,
                    'R' => 2024,
                    'S' => 2025,
                    _ => 0
                };
                if (year > 0)
                {
                    AnsiConsole.MarkupLine($"   [grey]   Model Year: {year}[/]");

                    // Determine battery type based on year
                    string battery;
                    if (year <= 2015)
                        battery = "24 kWh (ZE0)";
                    else if (year == 2016)
                        battery = "24/30 kWh (AZE0)";
                    else if (year == 2017)
                        battery = "30 kWh (AZE0)";
                    else if (year >= 2018 && year <= 2021)
                        battery = "40/62 kWh (ZE1)";
                    else
                        battery = "40/60 kWh (ZE1)";

                    AnsiConsole.MarkupLine($"   [grey]   Battery Type: {battery}[/]");
                }
            }

            // Assembly plant (11th character)
            if (vin.Length >= 11)
            {
                var plantChar = vin[10];
                var plant = plantChar switch
                {
                    'C' => "Smyrna, Tennessee, USA",
                    'A' => "Oppama, Japan",
                    'K' => "Sunderland, UK",
                    _ => $"Plant code: {plantChar}"
                };
                AnsiConsole.MarkupLine($"   [grey]   Assembly Plant: {plant}[/]");
            }

            // Serial number (chars 12-17)
            if (vin.Length >= 17)
            {
                var serial = vin[11..17];
                AnsiConsole.MarkupLine($"   [grey]   Serial: {serial}[/]");
            }
        }
    }
}