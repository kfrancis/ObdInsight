using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ObdTestApp.Vehicles;
using Serilog;
using Spectre.Console;

namespace ObdTestApp
{
    internal class Program
    {
        private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(10);

        private sealed record BmsGroup01Data(
            int ByteCount,
            double? CurrentAmps,
            double? VoltageVolts,
            double? CapacityAh,
            double? HxPercent,
            double? SocPercent);

        private sealed record BmsGroup02Data(
            int[] CellVoltagesMv,
            int MinVoltageMv,
            int MaxVoltageMv,
            int AvgVoltageMv,
            int DeltaVoltageMv)
        {
            public int CellCount => CellVoltagesMv.Length;
        }

        private static async Task Main(string[] args)
        {
            // Handle --test flag for running unit tests
            if (args.Contains("--test"))
            {
                var passed = LeafBmsParsingTests.RunAllTests();
                Environment.Exit(passed ? 0 : 1);
                return;
            }

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
            var failureCount = 0;
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
                AnsiConsole.MarkupLine($"[cyan]Scanning for BLE devices ({ScanDuration.TotalSeconds:0}s)...[/]");

                await scanner.StartScanAsync(cancellationToken: ct);
                try
                {
                    await Task.Delay(ScanDuration, ct);
                }
                finally
                {
                    await scanner.StopScanAsync();
                }

                Log.Information("BLE scan completed. Found {DeviceCount} devices", devices.Count);
                AnsiConsole.MarkupLine($"[green]✓[/] Scan complete. Found {devices.Count} device(s).");
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
            var monitoringFrameCount = 0;
            var monitoringUniqueCanIds = 0;
            var monitoringDuration = TimeSpan.Zero;

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
                AnsiConsole.MarkupLine("[cyan]Establishing BLE connection...[/]");
                await transport.OpenAsync(ct);

                Log.Information("BLE transport opened successfully");
                AnsiConsole.MarkupLine("[green]✓[/] Bluetooth connected.");
                AnsiConsole.WriteLine();

                var framer = new ElmFramer(transport)
                {
                    //EnableDebugLogging = true
                };

                var session = new ElmSession(framer)
                {
                    CommandTimeout = TimeSpan.FromSeconds(5),
                    MaxConsecutiveFailures = 3,
                    //EnableDebugLogging = true
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

                Log.Information("ECU wakeup complete - contexts configured for Nissan Leaf");
                AnsiConsole.MarkupLine("[green]✓[/] ECU wakeup complete");

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[cyan]Testing Nissan Leaf data collection (monitoring + queries)...[/]");
                AnsiConsole.MarkupLine("[grey]Phase 1: Passive monitoring, Phase 2: Active queries[/]");
                AnsiConsole.WriteLine();

                Log.Information("Starting Nissan Leaf data collection test");

                var leaf = new NissanLeaf();
                var commands = leaf.GetCommands(new VehicleVariantId("AZE0-2-2016-2017"), session);

                if (commands.TryGet<IBatteryManagementSystem>(out var bms))
                {
                    // Acceptance criteria: 5 consecutive stable reads
                    const int RequiredReads = 5;
                    const double VoltageStabilityThreshold = 2.0; // V
                    const double CurrentStabilityThreshold = 3.0; // A
                    const double HxStabilityThreshold = 0.5; // %

                    AnsiConsole.MarkupLine($"[cyan]Running {RequiredReads} consecutive BMS reads for stability verification...[/]");
                    Log.Information("Starting {Count} consecutive BMS reads for acceptance criteria", RequiredReads);

                    var voltageReadings = new List<double>();
                    var currentReadings = new List<double>();
                    var hxReadings = new List<double>();
                    var ahrReadings = new List<double>();
                    var successCount = 0;

                    for (int i = 1; i <= RequiredReads; i++)
                    {
                        try
                        {
                            AnsiConsole.MarkupLine($"[cyan]Read {i}/{RequiredReads}...[/]");
                            var battery = await bms.GetStatusAsync(ct);

                            var parts = new List<string>();
                            if (battery.VoltageVolts is double voltage)
                            {
                                parts.Add($"V: {voltage:F2}V");
                                voltageReadings.Add(voltage);
                            }
                            if (battery.CurrentAmps is double current)
                            {
                                var dir = current > 0 ? "dis" : (current < 0 ? "chg" : "idle");
                                parts.Add($"I: {current:F3}A ({dir})");
                                currentReadings.Add(current);
                            }
                            if (battery.SocPercent is double soc)
                                parts.Add($"SOC: {soc:F1}%");
                            if (battery.HealthPercent is double health)
                            {
                                parts.Add($"Hx: {health:F2}%");
                                hxReadings.Add(health);
                            }
                            if (battery.CapacityAh is double capacity)
                            {
                                parts.Add($"AHR: {capacity:F2}Ah");
                                ahrReadings.Add(capacity);
                            }

                            AnsiConsole.MarkupLine($"  [green]✓[/] {string.Join(", ", parts)}");
                            Log.Information("Read {Index}/{Total}: {Status}", i, RequiredReads, string.Join(", ", parts));
                            successCount++;
                            successfulQueries++; // Track for session stats

                            // Small delay between reads0
                            if (i < RequiredReads)
                                await Task.Delay(500, ct);
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"  [red]✗[/] Read {i} failed: {ex.Message}");
                            Log.Warning(ex, "Read {Index} failed: {Message}", i, ex.Message);
                            failedQueries++; // Track for session stats
                        }
                    }

                    // Analyze stability
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[yellow]═══ STABILITY ANALYSIS ═══[/]");

                    var allStable = true;

                    if (voltageReadings.Count >= 2)
                    {
                        var vMin = voltageReadings.Min();
                        var vMax = voltageReadings.Max();
                        var vDelta = vMax - vMin;
                        var vStable = vDelta <= VoltageStabilityThreshold;
                        allStable &= vStable;
                        var status = vStable ? "[green]✓ STABLE[/]" : "[red]✗ UNSTABLE[/]";
                        AnsiConsole.MarkupLine($"  Voltage: {vMin:F2}V - {vMax:F2}V (Δ{vDelta:F2}V) {status}");
                        Log.Information("Voltage stability: Min={Min:F2}V, Max={Max:F2}V, Delta={Delta:F2}V, Stable={Stable}",
                            vMin, vMax, vDelta, vStable);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("  Voltage: [red]Insufficient data[/]");
                        allStable = false;
                    }

                    if (currentReadings.Count >= 2)
                    {
                        var iMin = currentReadings.Min();
                        var iMax = currentReadings.Max();
                        var iDelta = iMax - iMin;
                        var iStable = iDelta <= CurrentStabilityThreshold;
                        allStable &= iStable;
                        var status = iStable ? "[green]✓ STABLE[/]" : "[yellow]⚠ VARIABLE[/]";
                        AnsiConsole.MarkupLine($"  Current: {iMin:F3}A - {iMax:F3}A (Δ{iDelta:F3}A) {status}");
                        Log.Information("Current stability: Min={Min:F3}A, Max={Max:F3}A, Delta={Delta:F3}A, Stable={Stable}",
                            iMin, iMax, iDelta, iStable);
                    }

                    if (hxReadings.Count >= 2)
                    {
                        var hMin = hxReadings.Min();
                        var hMax = hxReadings.Max();
                        var hDelta = hMax - hMin;
                        var hStable = hDelta <= HxStabilityThreshold;
                        allStable &= hStable;
                        var status = hStable ? "[green]✓ STABLE[/]" : "[red]✗ UNSTABLE[/]";
                        AnsiConsole.MarkupLine($"  Hx (Health): {hMin:F2}% - {hMax:F2}% (Δ{hDelta:F2}%) {status}");
                        Log.Information("Hx stability: Min={Min:F2}%, Max={Max:F2}%, Delta={Delta:F2}%, Stable={Stable}",
                            hMin, hMax, hDelta, hStable);
                    }

                    if (ahrReadings.Count >= 2)
                    {
                        var aMin = ahrReadings.Min();
                        var aMax = ahrReadings.Max();
                        var aDelta = aMax - aMin;
                        var aStable = aDelta <= 0.1; // 0.1 Ah tolerance
                        allStable &= aStable;
                        var status = aStable ? "[green]✓ STABLE[/]" : "[red]✗ UNSTABLE[/]";
                        AnsiConsole.MarkupLine($"  AHR (Capacity): {aMin:F2}Ah - {aMax:F2}Ah (Δ{aDelta:F2}Ah) {status}");
                        Log.Information("AHR stability: Min={Min:F2}Ah, Max={Max:F2}Ah, Delta={Delta:F2}Ah, Stable={Stable}",
                            aMin, aMax, aDelta, aStable);
                    }

                    AnsiConsole.WriteLine();
                    if (successCount == RequiredReads && allStable)
                    {
                        AnsiConsole.MarkupLine($"[green]═══ ACCEPTANCE CRITERIA: PASSED ═══[/]");
                        Log.Information("Acceptance criteria PASSED: {SuccessCount}/{RequiredReads} reads, all stable",
                            successCount, RequiredReads);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]═══ ACCEPTANCE CRITERIA: {(successCount < RequiredReads ? "INCOMPLETE" : "UNSTABLE")} ═══[/]");
                        Log.Warning("Acceptance criteria not fully met: {SuccessCount}/{RequiredReads} reads, stable={AllStable}",
                            successCount, RequiredReads, allStable);
                    }

                    // Query cell voltages once at the end
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[cyan]Querying cell voltages...[/]");
                    try
                    {
                        var cells = await bms.GetCellVoltagesAsync(ct);
                        if (cells != null)
                        {
                            AnsiConsole.MarkupLine($"[green]✓[/] Cells: {cells.CellCount} cells, " +
                                $"Min: {cells.MinVoltageMv}mV, Max: {cells.MaxVoltageMv}mV, Delta: {cells.DeltaVoltageMv}mV");

                            Log.Information("Cell voltages: Count={CellCount}, Min={Min}mV, Max={Max}mV, Avg={Avg}mV, Delta={Delta}mV",
                                cells.CellCount,
                                cells.MinVoltageMv,
                                cells.MaxVoltageMv,
                                cells.AvgVoltageMv,
                                cells.DeltaVoltageMv);

                            successfulQueries++; // Track for session stats

                            // Note: 21 cells is partial - Leaf has 96 cell pairs, may need multiple Group 02 queries
                            if (cells.CellCount < 96)
                            {
                                AnsiConsole.MarkupLine($"[yellow]⚠[/] Note: Only {cells.CellCount}/96 cells returned (partial response)");
                                Log.Warning("Partial cell data: {CellCount}/96 cells", cells.CellCount);
                            }
                        }
                        else
                        {
                            AnsiConsole.MarkupLine("[yellow]⚠[/] Cell voltages not available");
                            invalidResponseQueries++; // Track for session stats
                        }
                    }
                    catch (Exception ex)
                    {
                        AnsiConsole.MarkupLine($"[yellow]⚠[/] Cell voltage query failed: {ex.Message}");
                        Log.Warning(ex, "Cell voltage query failed: {Message}", ex.Message);
                        failedQueries++; // Track for session stats
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]⚠[/] BMS commands not available for this vehicle variant");
                    Log.Warning("BMS commands not available for vehicle variant: {VariantId}",
                        "AZE0-2-2016-2017");
                }

                //// =====================================================================
                //// PHASE 1: PASSIVE MONITORING MODE
                //// =====================================================================
                //AnsiConsole.MarkupLine("[yellow]═══ PHASE 1: PASSIVE MONITORING ═══[/]");
                //Log.Information("Entering passive monitoring mode for HVBAT broadcast data");

                //await session.EnterMonitoringModeAsync(EcuContext.NissanLeafHvbatMonitor, ct);
                //AnsiConsole.MarkupLine("[green]✓[/] Monitoring mode active (AT MA)");

                //// Monitor for 5 seconds
                //var monitorDuration = TimeSpan.FromSeconds(5);
                //var monitorStart = DateTime.UtcNow;
                //var frameCount = 0;
                //var uniqueCanIds = new HashSet<string>();

                //AnsiConsole.MarkupLine($"[cyan]Monitoring CAN bus for {monitorDuration.TotalSeconds}s...[/]");

                //// Use a timeout-based cancellation token for monitoring
                //using var monitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                //monitorCts.CancelAfter(monitorDuration);

                //try
                //{
                //    await foreach (var frame in session.MonitorFramesAsync(monitorCts.Token))
                //    {
                //        frameCount++;
                //        uniqueCanIds.Add(frame.CanIdHex);

                //        // Parse and log the frame data
                //        ParseAndLogCanFrame(frame.CanIdHex, frame.Data.ToArray());

                //        // Display interesting frames
                //        var description = frame.CanIdHex switch
                //        {
                //            "1DB" => "LB_STATUS (Current/Voltage/SOC)",
                //            "1DC" => "LB_LIMITS (Power limits)",
                //            "55B" => "LB_SOC (High-res SOC)",
                //            "5BC" => "LB_GIDS (Capacity/SOH)",
                //            "5C0" => "LB_TEMPS (Temperatures)",
                //            "1DA" => "INVERTER (Motor data)",
                //            "59E" => "QC_CAPACITY",
                //            _ => null
                //        };

                //        if (description != null)
                //        {
                //            AnsiConsole.MarkupLine($"[grey]  {frame.CanIdHex}: {description} - {frame.Data.Length} bytes[/]");
                //        }
                //    }

                //    // Monitoring ended - could be normal timeout, user cancellation, or BUFFER FULL
                //    Log.Debug("Monitoring loop completed");
                //}
                //catch (OperationCanceledException) when (monitorCts.IsCancellationRequested && !ct.IsCancellationRequested)
                //{
                //    // Expected - monitoring duration elapsed
                //    Log.Debug("Monitoring duration elapsed normally");
                //}
                //catch (OperationCanceledException) when (ct.IsCancellationRequested)
                //{
                //    // User cancelled
                //    Log.Information("Monitoring cancelled by user");
                //    AnsiConsole.MarkupLine("[yellow]Monitoring cancelled by user[/]");
                //}
                //finally
                //{
                //    // Always try to exit monitoring mode cleanly
                //    Log.Debug("Ensuring monitoring mode is exited");
                //}

                //// Capture monitoring stats for final summary
                //monitoringFrameCount = frameCount;
                //monitoringUniqueCanIds = uniqueCanIds.Count;
                //monitoringDuration = DateTime.UtcNow - monitorStart;

                //AnsiConsole.MarkupLine($"[green]✓[/] Monitoring complete: {frameCount} frames, {uniqueCanIds.Count} unique CAN IDs");
                //Log.Information("Monitoring complete - FrameCount={FrameCount}, UniqueCanIds={UniqueCanIds}",
                //    frameCount, string.Join(", ", uniqueCanIds.OrderBy(id => id)));

                //// Exit monitoring mode
                //Log.Information("Exiting monitoring mode");
                //await session.ExitMonitoringModeAsync(ct);
                //AnsiConsole.MarkupLine("[green]✓[/] Exited monitoring mode");

                //// Pause to let the device settle
                //AnsiConsole.MarkupLine("[yellow]Pausing 2s to let device settle...[/]");
                //await Task.Delay(2000, ct);

                //AnsiConsole.WriteLine();

                //// =====================================================================
                //// PHASE 2: ACTIVE QUERY MODE
                //// =====================================================================
                //AnsiConsole.MarkupLine("[yellow]═══ PHASE 2: ACTIVE QUERIES ═══[/]");
                //Log.Information("Starting active query mode");

                //// Query BMS Group 1
                //AnsiConsole.MarkupLine("[cyan]Querying BMS Group 1 (2101)...[/]");
                //Log.Debug("Querying Nissan Leaf BMS Group 1 (2101) - SOC, Voltage, Current, Temps");
                //var group1Lines = await session.QueryAsync("2101", EcuContext.NissanLeafBms, ct);
                //Log.Debug("Group 1 query returned {LineCount} lines: {Lines}", group1Lines.Length, string.Join(", ", group1Lines));

                //// Join all response lines for ISO-TP reassembly - the parser handles multi-frame responses
                //var group1Response = string.Join("\r", group1Lines);

                //if (TryParseBmsGroup01(group1Response, out var group01))
                //{
                //    var parts = new List<string>();

                //    if (group01?.VoltageVolts is double voltage)
                //        parts.Add($"Voltage: {voltage:F1}V");

                //    if (group01?.CurrentAmps is double currentAmps)
                //    {
                //        var currentDir = currentAmps > 0 ? "discharging" : (currentAmps < 0 ? "charging" : "idle");
                //        parts.Add($"Current: {Math.Abs(currentAmps):F1}A ({currentDir})");
                //    }

                //    if (group01?.SocPercent is double soc)
                //        parts.Add($"SOC: {soc:F1}%");

                //    if (group01?.CapacityAh is double capacityAh)
                //        parts.Add($"Capacity: {capacityAh:F2}Ah");

                //    if (group01?.HxPercent is double hx)
                //        parts.Add($"Health: {hx:F1}%");

                //    AnsiConsole.MarkupLine($"[green]✓[/] BMS Group 1: {string.Join(", ", parts)}");
                //    successfulQueries++;
                //}
                //else
                //{
                //    AnsiConsole.MarkupLine("[yellow]⚠[/] BMS Group 1: No valid response");
                //    invalidResponseQueries++;
                //}

                //await Task.Delay(500, ct); // Pause between queries

                //// Query BMS Group 2
                //AnsiConsole.MarkupLine("[cyan]Querying BMS Group 2 (2102)...[/]");
                //Log.Debug("Querying Nissan Leaf BMS Group 2 (2102)");
                //var group2Lines = await session.QueryAsync("2102", EcuContext.NissanLeafBms, ct);
                //Log.Debug("Group 2 query returned {LineCount} lines: {Lines}", group2Lines.Length, string.Join(", ", group2Lines));

                //var group2Response = string.Join("\n", group2Lines);
                //if (TryParseBmsGroup02(group2Response, out var group02) && group02 != null)
                //{
                //    AnsiConsole.MarkupLine($"[green]✓[/] BMS Group 2: {group02.CellCount} cells, " +
                //        $"Min: {group02.MinVoltageMv}mV, Max: {group02.MaxVoltageMv}mV, " +
                //        $"Avg: {group02.AvgVoltageMv}mV, Delta: {group02.DeltaVoltageMv}mV");
                //    successfulQueries++;
                //}
                //else
                //{
                //    AnsiConsole.MarkupLine("[yellow]⚠[/] BMS Group 2: No valid response");
                //    invalidResponseQueries++;
                //}

                //await Task.Delay(500, ct); // Pause between queries

                // Query VIN from charger
                AnsiConsole.MarkupLine("[cyan]Querying VIN from charger (2181)...[/]");
                Log.Debug("Querying VIN (2181)");
                var vinLines = await session.QueryAsync("2181", EcuContext.NissanLeafCharger, ct);
                Log.Debug("VIN query returned {LineCount} lines: {Lines}", vinLines.Length, string.Join(", ", vinLines));

                var vinResponse = string.Join("\n", vinLines);
                if (vinLines.Length > 0 && TryParseVin(vinResponse, out var vin))
                {
                    AnsiConsole.MarkupLine($"[green]✓[/] VIN: {vin}");
                    successfulQueries++;
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]⚠[/] VIN: No valid response");
                    invalidResponseQueries++;
                }

                //AnsiConsole.WriteLine();
                //AnsiConsole.MarkupLine("[green]═══ TEST COMPLETE ═══[/]");

                var totalQueries = successfulQueries + invalidResponseQueries;
                var successRate = totalQueries > 0 ? (double)successfulQueries / totalQueries * 100 : 0;

                //AnsiConsole.MarkupLine($"[cyan]Monitoring:[/] {frameCount} frames from {uniqueCanIds.Count} CAN IDs");
                AnsiConsole.MarkupLine($"[cyan]Queries:[/] {successfulQueries}/{totalQueries} successful ({successRate:F0}%)");
                Log.Information("Test complete - MonitorFrames={FrameCount}, QuerySuccess={Success}/{Total}",
                    0, successfulQueries, totalQueries);
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
                    $"[cyan]Monitoring Frames:[/] {monitoringFrameCount} ({monitoringUniqueCanIds} unique CAN IDs)\n" +
                    $"[cyan]Monitoring Duration:[/] {monitoringDuration.TotalSeconds:F1}s\n" +
                    $"[cyan]Successful Queries:[/] {successfulQueries}\n" +
                    $"[cyan]Invalid Response Queries:[/] {invalidResponseQueries}\n" +
                    $"[cyan]Failed Queries:[/] {failedQueries}\n" +
                    $"[cyan]Query Success Rate:[/] {finalSuccessRate:F1}%\n" +
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

                // Log raw bytes for debugging
                Log.Debug("Group 01 raw bytes ({Count}): {Hex}",
                    bytes.Count,
                    BitConverter.ToString(bytes.ToArray()));

                // Byte layout based on Leaf2018-CAN.md documentation:
                // Bytes 0-1:   61 01 (response header)
                // Bytes 2-5:   HV Bat Current 1 (signed 32-bit, /1024)
                // Bytes 6-12:  CF1 data (includes Current 2 at bytes 9-12)
                // Bytes 13-19: CF2 data
                // Bytes 20-21: HV Bat Voltage (/100)
                // Bytes 22-26: CF3 rest
                // Bytes 27-29: CF4 start
                // Bytes 30-31: Hx (/102.4)
                // Bytes 32:    CF4 end
                // Bytes 33-35: SOC (24-bit, /10000)
                // Bytes 36:    CF5 start
                // Bytes 37-39: AHR (24-bit, /10000)
                // Bytes 40+:   Rest of data

                double? currentAmps = null;
                double? voltageVolts = null;
                double? capacityAh = null;
                double? hxPercent = null;
                double? socPercent = null;

                // Current 1: bytes 2-5, signed 32-bit, divide by 1024
                if (bytes.Count >= 6)
                {
                    var currentUnsigned = ((uint)bytes[2] << 24) | ((uint)bytes[3] << 16) | ((uint)bytes[4] << 8) | bytes[5];
                    var currentRaw = unchecked((int)currentUnsigned);
                    // Positive = discharging, Negative = charging/regen
                    currentAmps = currentRaw / 1024.0;
                    Log.Debug("Current raw: 0x{Raw:X8} = {Amps:F2}A", currentUnsigned, currentAmps);
                }

                // Voltage: bytes 20-21, unsigned 16-bit, divide by 100
                if (bytes.Count >= 22)
                {
                    var voltageRaw = (bytes[20] << 8) | bytes[21];
                    if (voltageRaw > 0 && voltageRaw < 50000) // Sanity check: 0-500V
                    {
                        voltageVolts = voltageRaw / 100.0;
                        Log.Debug("Voltage raw: 0x{Raw:X4} = {Volts:F2}V", voltageRaw, voltageVolts);
                    }
                }

                // Based on Leaf2018-CAN.md, Frame 24 contains Hx at data[4-5]
                // Frame 24 is CF4 (seq=4), which starts at position 27 in reassembled data
                // So Hx is at positions 27+4=31 and 27+5=32
                // Hx (health): bytes 31-32, unsigned 16-bit, divide by 102.4
                if (bytes.Count >= 33)
                {
                    var hxRaw = (bytes[31] << 8) | bytes[32];
                    if (hxRaw > 0 && hxRaw < 15000) // Sanity check: 0-146%
                    {
                        hxPercent = hxRaw / 102.4;
                        Log.Debug("Hx raw: 0x{Raw:X4} = {Hx:F2}%", hxRaw, hxPercent);
                    }
                }

                // SOC spans Frame 24 data[7] and Frame 25 data[1-2]
                // Frame 24 ends at position 33, Frame 25 starts at position 34
                // SOC = data_24[7] << 16 | data_25[1] << 8 | data_25[2]
                // Position 33 (24[7]) + positions 35-36 (25[1-2])
                // Actually, looking at: SOC = (data 24[7] << 16 | ((data 25[1] << 8) | data 25[2]))/10000
                // Frame 24 data[7] = position 33, Frame 25 data[1-2] = positions 35-36
                if (bytes.Count >= 37)
                {
                    var socRaw = (bytes[33] << 16) | (bytes[35] << 8) | bytes[36];
                    if (socRaw > 0 && socRaw < 1100000) // Sanity check: 0-110%
                    {
                        socPercent = socRaw / 10000.0;
                        Log.Debug("SOC raw: 0x{Raw:X6} = {Soc:F2}%", socRaw, socPercent);
                    }
                }

                // AHR (capacity): Frame 25 data[4-6]
                // Frame 25 starts at position 34, so AHR is at positions 34+4=38, 34+5=39, 34+6=40
                if (bytes.Count >= 41)
                {
                    var ahrRaw = (bytes[38] << 16) | (bytes[39] << 8) | bytes[40];
                    if (ahrRaw > 0 && ahrRaw < 1000000) // Sanity check: 0-100 Ah
                    {
                        capacityAh = ahrRaw / 10000.0;
                        Log.Debug("AHR raw: 0x{Raw:X6} = {Ah:F2}Ah", ahrRaw, capacityAh);
                    }
                }

                data = new BmsGroup01Data(bytes.Count, currentAmps, voltageVolts, capacityAh, hxPercent, socPercent);

                // Structured logging:
                Log.Debug(
                    "Parsed BMS Group 01 - Bytes={ByteCount}, CurrentA={CurrentAmps:F2}, VoltageV={VoltageVolts:F2}, SocPct={SocPercent:F2}, CapacityAh={CapacityAh:F2}, HxPct={HxPercent:F2}",
                    data.ByteCount, data.CurrentAmps, data.VoltageVolts, data.SocPercent, data.CapacityAh, data.HxPercent);
                return true;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
                Log.Warning(ex, "Error parsing BMS Group 01");
                return false;
            }
        }

        private static bool TryParseBmsGroup02(string response, out BmsGroup02Data? data)
        {
            data = null;
            try
            {
                var bytes = ParseIsoTpResponse(response);

                if (bytes.Count < 2)
                {
                    Log.Debug("Not enough bytes for Group 02");
                    return false;
                }

                // First 2 bytes should be 61 02 (positive response to 21 02)
                if (bytes[0] != 0x61 || bytes[1] != 0x02)
                {
                    Log.Debug("Unexpected response header: {Header1:X2} {Header2:X2}", bytes[0], bytes[1]);
                    return false;
                }

                // Log raw bytes for debugging
                Log.Debug("Group 02 raw bytes ({Count}): {Hex}",
                    bytes.Count,
                    bytes.Count <= 50 ? BitConverter.ToString(bytes.ToArray())
                        : BitConverter.ToString(bytes.Take(50).ToArray()) + "...");

                // Cell voltages start at byte 2, each cell is 2 bytes (big-endian millivolts)
                // Nissan Leaf has 96 cell pairs
                // According to Leaf2018-CAN.md:
                // CV array[0] = bytes[2] << 8 | bytes[3]
                // CV array[1] = bytes[4] << 8 | bytes[5]
                // etc.
                var cellVoltages = new List<int>();
                var allRawVoltages = new List<int>(); // For debugging

                for (var i = 2; i + 1 < bytes.Count && cellVoltages.Count < 96; i += 2)
                {
                    var voltage = (bytes[i] << 8) | bytes[i + 1];
                    allRawVoltages.Add(voltage);

                    // Valid cell voltages are typically 3000-4200 mV for lithium cells
                    // But accept wider range to not miss data
                    if (voltage >= 2500 && voltage <= 4500)
                    {
                        cellVoltages.Add(voltage);
                    }
                }

                // Log first few raw voltages for debugging
                if (allRawVoltages.Count > 0)
                {
                    var firstFew = string.Join(", ", allRawVoltages.Take(10).Select(v => $"{v}mV"));
                    Log.Debug("First 10 raw cell values: {Values}", firstFew);
                }

                if (cellVoltages.Count == 0)
                {
                    Log.Debug("No valid cell voltages found (checked {Count} values)", allRawVoltages.Count);
                    return false;
                }

                data = new BmsGroup02Data(
                    cellVoltages.ToArray(),
                    cellVoltages.Min(),
                    cellVoltages.Max(),
                    (int)cellVoltages.Average(),
                    cellVoltages.Max() - cellVoltages.Min()
                );

                Log.Debug("Parsed BMS Group 02 - Cells={CellCount}, Min={MinVoltage}mV, Max={MaxVoltage}mV, Avg={AvgVoltage}mV, Delta={DeltaVoltage}mV",
                    data.CellCount, data.MinVoltageMv, data.MaxVoltageMv, data.AvgVoltageMv, data.DeltaVoltageMv);

                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error parsing BMS Group 02");
                return false;
            }
        }

        private static void ParseAndLogCanFrame(string canIdHex, byte[] data)
        {
            try
            {
                // Parse interesting broadcast frames based on Leaf2018-CAN.md
                switch (canIdHex.ToUpper())
                {
                    case "1DB": // LB_STATUS (Current/Voltage/SOC)
                        if (data.Length >= 7)
                        {
                            // Battery current (signed 16-bit, 0.5A per bit)
                            var currentRaw = (short)((data[0] << 8) | data[1]);
                            var current = currentRaw * 0.5;

                            // Battery voltage (16-bit, 0.5V per bit)
                            var voltage = ((data[2] << 8) | data[3]) * 0.5;

                            // SOC (Gids - 10-bit from bytes 4-5)
                            var gids = ((data[4] & 0x03) << 8) | data[5];

                            Log.Debug("[CAN 1DB] Battery: {Current:F1}A, {Voltage:F1}V, {Gids} Gids", current, voltage, gids);
                        }
                        break;

                    case "55B": // LB_SOC (High-res SOC)
                        if (data.Length >= 2)
                        {
                            var soc = ((data[0] << 2) | (data[1] >> 6)) * 0.1;
                            Log.Debug("[CAN 55B] SOC: {Soc:F1}%", soc);
                        }
                        break;

                    case "5BC": // LB_GIDS (Capacity/SOH)
                        if (data.Length >= 5)
                        {
                            var gids = (data[0] << 2) | (data[1] >> 6);
                            var soh = ((data[4] & 0xFE) >> 1) * 0.5;
                            Log.Debug("[CAN 5BC] Capacity: {Gids} Gids, SOH: {Soh:F1}%", gids, soh);
                        }
                        break;

                    case "5C0": // LB_TEMPS (Temperatures)
                        if (data.Length >= 4)
                        {
                            var temp1 = (data[0] / 2.0) - 40;
                            var temp2 = (data[1] / 2.0) - 40;
                            var temp3 = (data[2] / 2.0) - 40;
                            var temp4 = (data[3] / 2.0) - 40;
                            Log.Debug("[CAN 5C0] Battery Temps: {T1:F1}°C, {T2:F1}°C, {T3:F1}°C, {T4:F1}°C", temp1, temp2, temp3, temp4);
                        }
                        break;

                    case "1DA": // INVERTER (Motor data)
                        if (data.Length >= 4)
                        {
                            var motorRpm = (short)((data[0] << 8) | data[1]);
                            var motorTemp = data[2] - 40;
                            Log.Debug("[CAN 1DA] Motor: {Rpm} RPM, {Temp}°C", motorRpm, motorTemp);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error parsing CAN frame {CanId}", canIdHex);
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
        /// Also handles frames concatenated together on a single line (e.g., "7BB25...7BB26...").
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

            // First, split any concatenated frames (e.g., "7BB25...7BB26..." becomes two separate frames)
            var allFrames = new List<string>();
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length < 6) continue;

                // Split concatenated frames by finding CAN ID patterns (3 hex chars followed by frame data)
                // CAN frames are typically 19-20 hex chars: 3 (CAN ID) + 2 (PCI) + 14 (7 bytes data)
                var remaining = trimmed;
                while (remaining.Length >= 6)
                {
                    // Check if this starts with a valid CAN ID
                    if (!IsCanIdPrefixForIsoTp(remaining))
                    {
                        break;
                    }

                    // Find the next CAN ID prefix in the string (if any)
                    // Start looking after the minimum frame length (CAN ID + at least 2 bytes = 7 chars)
                    var nextFrameStart = -1;
                    for (var i = 7; i <= remaining.Length - 6; i++)
                    {
                        // Only check positions where a new CAN frame could start
                        // A CAN ID is followed by the frame type byte (hex nibble 0-2 for SF/FF/CF)
                        var potentialCanId = remaining.Substring(i, 3);
                        if (!potentialCanId.All(c => Uri.IsHexDigit(c))) continue;
                        if (!int.TryParse(potentialCanId, System.Globalization.NumberStyles.HexNumber, null, out var id)) continue;

                        // Must be a valid CAN ID in the expected range
                        if (!((id >= 0x700 && id <= 0x7FF) || (id >= 0x790 && id <= 0x79F))) continue;

                        // Check if the 4th character is a valid ISO-TP frame type indicator (0, 1, 2, 3 nibble)
                        if (i + 3 < remaining.Length)
                        {
                            var frameTypeChar = remaining[i + 3];
                            // Valid frame types: 0x (SF), 1x (FF), 2x (CF), 3x (FC)
                            if (frameTypeChar == '0' || frameTypeChar == '1' || frameTypeChar == '2' || frameTypeChar == '3')
                            {
                                nextFrameStart = i;
                                break;
                            }
                        }
                    }

                    if (nextFrameStart > 0)
                    {
                        // Extract first frame and continue with the rest
                        allFrames.Add(remaining[..nextFrameStart]);
                        remaining = remaining[nextFrameStart..];
                    }
                    else
                    {
                        // No more frames, take the whole thing
                        allFrames.Add(remaining);
                        break;
                    }
                }
            }

            Log.Debug("ParseIsoTpResponse: Split into {FrameCount} raw frames from {LineCount} lines", allFrames.Count, lines.Length);

            var frameSequence = new List<(int Type, int Seq, byte[] Data, int TotalLen)>();
            var expectedTotalLength = 0;

            foreach (var frame in allFrames)
            {
                if (frame.Length < 6) continue;

                if (!IsCanIdPrefixForIsoTp(frame))
                    continue;

                var frameHex = frame[3..];

                if (frameHex.Length < 2) continue;

                if (!byte.TryParse(frameHex[..2], System.Globalization.NumberStyles.HexNumber, null, out var frameTypeByte))
                    continue;

                var frameType = (frameTypeByte & 0xF0) >> 4;
                var frameInfo = frameTypeByte & 0x0F;

                byte[] frameData;

                switch (frameType)
                {
                    case 0: // Single Frame - length in low nibble
                        var sfLen = frameInfo;
                        var sfDataHex = frameHex[2..];
                        frameData = ParseHexString(sfDataHex);
                        if (frameData.Length > sfLen)
                            frameData = frameData[..sfLen];
                        frameSequence.Add((0, 0, frameData, sfLen));
                        break;

                    case 1: // First Frame - 12-bit length in low nibble + next byte
                        if (frameHex.Length < 4) continue;
                        if (!byte.TryParse(frameHex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var lenLowByte))
                            continue;
                        expectedTotalLength = (frameInfo << 8) | lenLowByte;
                        var ffDataHex = frameHex[4..];
                        frameData = ParseHexString(ffDataHex);
                        // First frame contains up to 6 bytes of data (7 bytes total - 1 byte PCI - 1 byte length)
                        frameSequence.Add((1, 0, frameData, expectedTotalLength));
                        break;

                    case 2: // Consecutive Frame - 4-bit sequence number in low nibble
                        var seqNum = frameInfo;
                        var cfDataHex = frameHex[2..];
                        frameData = ParseHexString(cfDataHex);
                        // Consecutive frames contain up to 7 bytes of data
                        frameSequence.Add((2, seqNum, frameData, 0));
                        break;

                    default:
                        frameData = ParseHexString(frameHex);
                        if (frameData.Length > 0)
                            frameSequence.Add((-1, 0, frameData, 0));
                        break;
                }
            }

            // First, try to find Single Frame or First Frame
            var firstFrame = frameSequence.FirstOrDefault(f => f.Type == 0 || f.Type == 1);
            if (firstFrame.Data != null)
            {
                bytes.AddRange(firstFrame.Data);
                expectedTotalLength = firstFrame.TotalLen;
            }

            // Then add all consecutive frames in order
            // Handle sequence number wraparound (0-F)
            var consecutiveFrames = frameSequence
                .Where(f => f.Type == 2)
                .ToList();

            // Sort by sequence number, handling wraparound
            if (consecutiveFrames.Count > 0)
            {
                // Simple approach: just add them in order they appear (ELM327 should return them in order)
                foreach (var cf in consecutiveFrames)
                {
                    bytes.AddRange(cf.Data);
                }
            }

            // Trim to expected length if we know it
            if (expectedTotalLength > 0 && bytes.Count > expectedTotalLength)
            {
                bytes = bytes.Take(expectedTotalLength).ToList();
            }

            // Fallback: if no ISO-TP frames found, try parsing as raw hex
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

            Log.Debug("ParseIsoTpResponse: Parsed {ByteCount} bytes from {FrameCount} frames (expected {ExpectedLen})",
                bytes.Count, frameSequence.Count, expectedTotalLength);

            return bytes;
        }

        /// <summary>
        /// Checks if a string starts with a valid CAN ID prefix for ISO-TP frames.
        /// Accepts both standard OBD range (7xx) and extended Nissan Leaf ECU range (79x).
        /// </summary>
        private static bool IsCanIdPrefixForIsoTp(string s)
        {
            if (s.Length < 3) return false;
            var prefix = s[..3];
            if (!prefix.All(c => Uri.IsHexDigit(c))) return false;
            if (!int.TryParse(prefix, System.Globalization.NumberStyles.HexNumber, null, out var id)) return false;

            // Accept:
            // - 0x700-0x7FF: Standard OBD-II response range
            // - 0x79x: Nissan Leaf charger response (79A)
            return (id >= 0x700 && id <= 0x7FF) || (id >= 0x790 && id <= 0x79F);
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
            for (var i = 0; i + 1 < hex.Length; i += 2)
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
                var vinStart = -1;
                for (var i = 0; i < bytes.Count - 1; i++)
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
