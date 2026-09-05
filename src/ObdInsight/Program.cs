using System.Text.Json;
using Microsoft.Extensions.Logging;
using ObdInsight.Application;
using ObdInsight.Core.Communication.Bluetooth;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Transports.WindowsBle;
using ObdInsight.UI;
using Serilog;
using Serilog.Extensions.Logging;
using Spectre.Console;

namespace ObdInsight
{
    internal class Program
    {
        private static readonly TimeSpan s_scanDuration = TimeSpan.FromSeconds(10);

        /// <summary>
        ///     Decode VIN information for Nissan Leaf.
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
                    var battery = year switch
                    {
                        <= 2015 => "24 kWh (ZE0)",
                        2016 => "24/30 kWh (AZE0)",
                        2017 => "30 kWh (AZE0)",
                        >= 2018 and <= 2021 => "40/62 kWh (ZE1)",
                        _ => "40/60 kWh (ZE1)"
                    };

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

        /// <summary>
        ///     Logs errors using Serilog for proper file logging and diagnostics
        /// </summary>
        private static void LogError(Exception ex, int successCount, int failCount, TimeSpan uptime)
        {
            Log.Error(ex, "Query error - Uptime={Uptime}, SuccessCount={SuccessCount}, FailCount={FailCount}",
                uptime, successCount, failCount);
        }

        /// <summary>
        ///     Captures raw broadcast frames + decoded values for hardware verification of the
        ///     monitoring wire format and signal bit layouts. Logs the first samples per CAN ID
        ///     (raw hex + JSON decode), per-ID frame counts, and a 0x1DB voltage/current decode
        ///     for cross-checking against the BMS 2101 query values.
        /// </summary>
        private static async Task RunBroadcastDiagnosticAsync(CanMonitor monitor, CancellationToken ct)
        {
            await monitor.StartAsync(ct);

            var rawSamplesPerId = new Dictionary<int, int>();
            var countsPerId = new Dictionary<int, int>();
            var totalFrames = 0;

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(20));

            try
            {
                await foreach (var frame in monitor.Subscribe(ReadOnlyMemory<int>.Empty, window.Token))
                {
                    totalFrames++;
                    var id = frame.CanId;
                    countsPerId[id] = countsPerId.GetValueOrDefault(id) + 1;

                    var samples = rawSamplesPerId.GetValueOrDefault(id);
                    if (samples < 2)
                    {
                        rawSamplesPerId[id] = samples + 1;
                        var hex = Convert.ToHexString(frame.Data.ToArray());
                        var decoded = TryDecodeForDiagnostic(id, frame.Data.Span);
                        Log.Information("BROADCAST RAW {CanId:X3} [{Hex}] => {Decoded}", id, hex,
                            decoded ?? "(no decoder)");
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Diagnostic window elapsed.
            }

            Log.Information("Broadcast diagnostic: {Total} frames, {Ids} distinct IDs, monitor EndReason={EndReason}",
                totalFrames, countsPerId.Count, monitor.EndReason);
            foreach (var kvp in countsPerId.OrderByDescending(k => k.Value))
            {
                Log.Information("  ID {CanId:X3}: {Count} frames", kvp.Key, kvp.Value);
            }

            AnsiConsole.MarkupLine(
                $"[cyan]Broadcast diagnostic:[/] {totalFrames} frames across {countsPerId.Count} IDs (details in log)");

            // Cross-check: broadcast 0x1DB carries the same physical quantities as the BMS
            // 2101 query — matching values prove the bit-layout convention end-to-end.
            if (monitor.TryGetLatest<BatteryFrame_1DB_AZE0>(out var battery1db))
            {
                Log.Information(
                    "CROSS-CHECK 1DB: Voltage={Voltage:F2}V Current={Current:F2}A UsableSoc={Soc}% - compare against the BMS 2101 read above",
                    battery1db.Voltage, battery1db.Current, battery1db.UsableSoc);
                AnsiConsole.MarkupLine(
                    $"[cyan]1DB cross-check:[/] {battery1db.Voltage:F2}V / {battery1db.Current:F2}A (BMS said ~check log)");
            }
            else
            {
                Log.Information("CROSS-CHECK 1DB: no frame cached during diagnostic window");
            }

            await monitor.StopAsync(CancellationToken.None);
        }

        private static string? TryDecodeForDiagnostic(int canId, ReadOnlySpan<byte> data)
        {
            if (data.Length != 8)
            {
                return $"(len={data.Length}, decoders need 8 bytes)";
            }

            var decoded = CanFrameRouter.TryParseAny(canId, data);

            return decoded is null
                ? null
                : $"{decoded.GetType().Name} {JsonSerializer.Serialize(decoded, decoded.GetType())}";
        }

        /// <summary>
        ///     Logs session summary using Serilog for proper file logging and analysis
        /// </summary>
        private static void LogSessionSummary(BleDeviceInfo device, DateTime start, TimeSpan duration, int success,
            int invalid, int failed)
        {
            var total = success + invalid + failed;
            var successRate = total > 0 ? (double)success / total * 100 : 0;
            Log.Information(
                "Session completed - Device={DeviceName}({DeviceAddress}), Start={StartTime}, Duration={Duration}, SuccessCount={SuccessCount}, InvalidCount={InvalidCount}, FailCount={FailCount}, SuccessRate={SuccessRate:F1}%",
                device.Name, device.Address, start, duration, success, invalid, failed, successRate);
        }

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
                    .WriteTo.Console(
                        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .WriteTo.File(
                        logFilePath,
                        outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                        flushToDiskInterval: TimeSpan.FromSeconds(1))
                    .CreateLogger();

                AnsiConsole.MarkupLine($"[grey]Log file: {Path.GetFileName(logFilePath).EscapeMarkup()}[/]");
                AnsiConsole.MarkupLine($"[grey]Log directory: {logDir.EscapeMarkup()}[/]");

                Log.Information("=== ObdInsight Started ===");
                Log.Information("Log file: {LogFile}", logFilePath);
                Log.Information("Arguments: {Args}", string.Join(" ", args));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to initialize logging: {ex.Message.EscapeMarkup()}[/]");
                throw;
            }

            // Parse command-line arguments
            var autoConnect = args.Contains("--auto") || (!Console.IsInputRedirected && Environment.UserInteractive);
            var targetAddress = args.FirstOrDefault(a => a.StartsWith("--device="))?["--device=".Length..];

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
                // USB-CAN adapter on a COM port (CANable): raw broadcast capture, no BLE, no ELM327.
                if (SerialCanSession.IsRequested(args))
                {
                    await SerialCanSession.RunAsync(args, cts.Token);
                    return;
                }

                // Check for favorite device and auto-connect WITHOUT scanning
                BleDeviceInfo? selectedDevice = null;

                // If specific device address provided via command line
                if (!string.IsNullOrEmpty(targetAddress))
                {
                    selectedDevice = new BleDeviceInfo(
                        "Command-line Device",
                        targetAddress,
                        0,
                        []);
                    Log.Information("Using device from command line: {Address}", targetAddress);
                    AnsiConsole.MarkupLine(
                        $"[green]✓[/] Using device from command line: [cyan]{targetAddress.EscapeMarkup()}[/]");
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
                        Log.Information("Found favorite device: {DeviceName} ({Address})", favorite.Name,
                            favorite.Address);
                        AnsiConsole.MarkupLine(
                            $"[yellow]★[/] Found favorite device: [cyan]{favorite.Address.EscapeMarkup()}[/]");

                        // Auto-connect without prompting in non-interactive mode or with --auto flag
                        if (!Console.IsInputRedirected || args.Contains("--auto"))
                        {
                            selectedDevice = favorite;
                            Log.Information("Auto-connecting to favorite device (non-interactive or --auto flag)");
                            AnsiConsole.MarkupLine("[green]✓[/] Auto-connecting to favorite device (no scan required)");
                        }
                        else if (AnsiConsole.Confirm("Auto-connect to favorite?"))
                        {
                            selectedDevice = favorite;
                            Log.Information("User confirmed auto-connect to favorite device");
                            AnsiConsole.MarkupLine("[green]✓[/] Auto-connecting to favorite device (no scan required)");
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
                    var scanService = new DeviceScanService(s_scanDuration);
                    selectedDevice = await scanService.ScanAndSelectDeviceAsync(preferences, cts.Token);
                    if (selectedDevice == null)
                    {
                        Log.Information("No device selected by user. Exiting.");
                        AnsiConsole.MarkupLine("[yellow]No device selected. Exiting.[/]");
                        return;
                    }

                    Log.Information("Device selected: {DeviceName} ({Address})", selectedDevice.Name,
                        selectedDevice.Address);
                }

                // Vehicle selection is VIN-driven (roadmap B6): RunElm327SessionAsync
                // resolves the profile/variant from the car's own VIN once the session
                // is up, via VehicleResolver. No hardcoded vehicle.
                Log.Information(
                    "Starting session with device: {DeviceName} ({Address}); vehicle resolved from VIN after connect",
                    selectedDevice.Name, selectedDevice.Address);
                var retryService = new SessionRetryService();
                await retryService.RunWithRetryAsync(selectedDevice, preferences, RunElm327SessionAsync, null, null,
                    cts.Token);
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
                Log.Information("=== ObdInsight Exiting ===");
                await Log.CloseAndFlushAsync();
            }
        }

        private static async Task RunElm327SessionAsync(BleDeviceInfo selectedDevice, IVehicleProfile? vehicleProfile,
            VehicleVariant? vehicleVariant, CancellationToken ct)
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

            if (vehicleProfile != null && vehicleVariant != null)
            {
                Log.Information("Using vehicle profile: {Make} {Model}, variant: {Variant}",
                    vehicleProfile.Make, vehicleProfile.Model, vehicleVariant.DisplayName);
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[cyan]Connecting to:[/] {selectedDevice.Name.EscapeMarkup()} [grey]({selectedDevice.Address.EscapeMarkup()})[/]");

            // Bridge Core's ILogger-based logging into the app's Serilog pipeline
            // (console + file sinks configured in Main).
            using var loggerFactory = new SerilogLoggerFactory(Log.Logger);

            await using var transport = new BleElmTransport(
                selectedDevice.Address,
                loggerFactory.CreateLogger<BleElmTransport>());

            // Enable debug logging
            transport.EnableDebugLogging = true;

            try
            {
                Log.Information("Opening BLE transport");
                AnsiConsole.MarkupLine("[cyan]Establishing BLE connection...[/]");
                await transport.OpenAsync(ct);

                Log.Information("BLE transport opened successfully");
                AnsiConsole.MarkupLine("[green]✓[/] Bluetooth connected.");
                AnsiConsole.WriteLine();

                var framer = new ElmFramer(transport, loggerFactory.CreateLogger<ElmFramer>())
                {
                    EnableDebugLogging = true
                };

                var session =
                    new ElmSession(framer, new LeafBmsWakeupStrategy(), loggerFactory.CreateLogger<ElmSession>())
                    {
                        CommandTimeout = TimeSpan.FromSeconds(5),
                        MaxConsecutiveFailures = 3,
                        EnableDebugLogging = true
                    };

                Log.Information("Initializing ELM327 session (Timeout={CommandTimeout}s, MaxFailures={MaxFailures})",
                    session.CommandTimeout.TotalSeconds, session.MaxConsecutiveFailures);
                AnsiConsole.MarkupLine("[yellow]Initializing ELM327 session...[/]");
                await session.InitializeAndLockAsync(ct);
                Log.Information("ELM327 session initialized and protocol locked");
                AnsiConsole.MarkupLine("[green]✓[/] Session initialized and protocol locked.");

                // Display connection info
                DeviceRenderer.RenderConnectionInfo(selectedDevice, sessionStart, session.CommandTimeout);

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]IMPORTANT: Vehicle must be in one of these states:[/]\n" +
                                       "[green]1. READY mode[/] (Press brake + Start button) - [cyan]BEST[/]\n" +
                                       "[green]2. Charging[/] (Plugged in and charging)\n" +
                                       "[green]3. ACC mode[/] (Start button without brake) - [yellow]May work[/]\n\n" +
                                       "[red]If car is completely OFF, you will get NO DATA.[/]");

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[yellow]Waking up ECUs and configuring BMS...[/]");

                // Multi-tier wakeup strategy for Nissan Leaf
                var wakeupSucceeded = false;

                // Tier 1: Send to VCM wakeup address (0x679)
                Log.Information("Wakeup Tier 1: Sending VCM wakeup (679)");
                try
                {
                    await framer.SendAndReadFrameAsync("ATSH679", session.CommandTimeout, ct);
                    var vcmWakeupResponse = await framer.SendAndReadFrameAsync("00", session.CommandTimeout, ct);
                    if (!vcmWakeupResponse.Contains("NO DATA", StringComparison.OrdinalIgnoreCase) &&
                        vcmWakeupResponse.Length > 3)
                    {
                        Log.Information("Tier 1 VCM wakeup succeeded: {Response}",
                            vcmWakeupResponse.Substring(0, Math.Min(50, vcmWakeupResponse.Length)));
                        wakeupSucceeded = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("Tier 1 VCM wakeup attempt failed: {Message}", ex.Message);
                }

                // Tier 2: Try battery heater wakeup (0x5C0)
                if (!wakeupSucceeded)
                {
                    Log.Information("Wakeup Tier 2: Sending battery heater wakeup (5C0)");
                    try
                    {
                        await framer.SendAndReadFrameAsync("ATSH5C0", session.CommandTimeout, ct);
                        var battHeaterWakeupResponse =
                            await framer.SendAndReadFrameAsync("00000000", session.CommandTimeout, ct);
                        if (!battHeaterWakeupResponse.Contains("NO DATA", StringComparison.OrdinalIgnoreCase) &&
                            battHeaterWakeupResponse.Length > 3)
                        {
                            Log.Information("Tier 2 battery heater wakeup succeeded: {Response}",
                                battHeaterWakeupResponse.Substring(0, Math.Min(50, battHeaterWakeupResponse.Length)));
                            wakeupSucceeded = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("Tier 2 battery heater wakeup attempt failed: {Message}", ex.Message);
                    }
                }

                // Tier 3: Broadcast wakeup (0x7DF)
                Log.Information("Wakeup Tier 3: Sending broadcast wakeup (7DF)");
                await framer.SendAndReadFrameAsync("ATSH7DF", session.CommandTimeout, ct);
                var wakeupResponse = await framer.SendAndReadFrameAsync("0100", session.CommandTimeout, ct);

                if (wakeupResponse.Contains("NO DATA", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("All wakeup attempts returned NO DATA - ECUs may be sleeping");
                    AnsiConsole.MarkupLine(
                        "[red]⚠[/] [yellow]Wakeup query returned NO DATA - ECUs appear to be sleeping![/]");
                    AnsiConsole.MarkupLine("[yellow]  → Make sure car is in READY mode or charging[/]");
                    AnsiConsole.WriteLine();
                }
                else
                {
                    Log.Information("Broadcast wakeup query succeeded - ECUs responding");
                    AnsiConsole.MarkupLine("[green]✓[/] ECUs responded to wakeup");
                    wakeupSucceeded = true;
                }

                // Additional delay to ensure ECUs are fully awake
                await Task.Delay(500, ct);

                Log.Information("ECU wakeup complete");
                AnsiConsole.MarkupLine("[green]✓[/] ECU wakeup complete");

                AnsiConsole.WriteLine();

                // VIN-driven vehicle resolution (roadmap B6): the car tells us what it is.
                var detection = await VehicleResolver.ResolveAsync(session, ct: ct);
                if (detection.Status != VehicleDetectionStatus.Detected)
                {
                    var detail = detection.Status switch
                    {
                        VehicleDetectionStatus.VinUnreadable =>
                            "Could not read a VIN from the vehicle.",
                        VehicleDetectionStatus.UnsupportedVehicle =>
                            $"VIN {detection.Vin} does not match any supported vehicle.",
                        VehicleDetectionStatus.VariantUnsupported =>
                            $"{detection.Profile?.Make} {detection.Profile?.Model} variant " +
                            $"'{detection.VariantId?.Value}' (VIN {detection.Vin}) has no command set yet.",
                        _ => "Unknown detection failure."
                    };
                    Log.Error("Vehicle detection failed: {Status} — {Detail}", detection.Status, detail);
                    AnsiConsole.MarkupLine($"[red]Vehicle detection failed:[/] {detail.EscapeMarkup()}");
                    return;
                }

                vehicleProfile = detection.Profile!;
                vehicleVariant = vehicleProfile.Variants.FirstOrDefault(v => v.Id == detection.VariantId);
                Log.Information("Detected vehicle: {Make} {Model}, variant {Variant} (VIN {Vin})",
                    vehicleProfile.Make, vehicleProfile.Model, detection.VariantId?.Value, detection.Vin);
                AnsiConsole.MarkupLine(
                    $"[green]✓[/] Detected: {vehicleProfile.Make} {vehicleProfile.Model} " +
                    $"[grey]{vehicleVariant?.DisplayName.EscapeMarkup()} — VIN {detection.Vin!.EscapeMarkup()}[/]");

                AnsiConsole.MarkupLine(
                    $"[cyan]Testing {vehicleProfile.Make} {vehicleProfile.Model} data collection...[/]");
                AnsiConsole.MarkupLine(
                    "[grey]Note: Only testing capabilities that work when vehicle is stationary in READY mode[/]");
                AnsiConsole.MarkupLine(
                    "[grey]Skipped: Motor (requires accelerator), Charger (requires charging), VCM/ABS (require motion)[/]");
                AnsiConsole.WriteLine();

                Log.Information("Starting data collection test for {Make} {Model}", vehicleProfile.Make,
                    vehicleProfile.Model);

                try
                {
                    var commands = detection.Commands!;

                    // ===========================================
                    // 1. Battery Management System (BMS) - 5 consecutive reads for stability
                    // ===========================================
                    if (commands.TryGet<IBatteryManagementSystem>(out var bms))
                    {
                        // Acceptance criteria: 5 consecutive stable reads
                        const int RequiredReads = 5;
                        const double VoltageStabilityThreshold = 2.0; // V
                        const double CurrentStabilityThreshold = 3.0; // A
                        const double HxStabilityThreshold = 0.5; // %

                        AnsiConsole.MarkupLine(
                            $"[cyan]Running {RequiredReads} consecutive BMS reads for stability verification...[/]");
                        Log.Information("Starting {Count} consecutive BMS reads for acceptance criteria",
                            RequiredReads);

                        var voltageReadings = new List<double>();
                        var currentReadings = new List<double>();
                        var hxReadings = new List<double>();
                        var ahrReadings = new List<double>();
                        var successCount = 0;

                        for (var i = 1; i <= RequiredReads; i++)
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
                                    var dir = current > 0 ? "dis" : current < 0 ? "chg" : "idle";
                                    parts.Add($"I: {current:F3}A ({dir})");
                                    currentReadings.Add(current);
                                }

                                if (battery.SocPercent is double soc)
                                {
                                    parts.Add($"SOC: {soc:F1}%");
                                }

                                if (battery.StateOfHealthPercent is double health)
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
                                Log.Information("Read {Index}/{Total}: {Status}", i, RequiredReads,
                                    string.Join(", ", parts));
                                successCount++;
                                successfulQueries++; // Track for session stats

                                // Small delay between reads
                                if (i < RequiredReads)
                                {
                                    await Task.Delay(500, ct);
                                }
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
                            Log.Information(
                                "Voltage stability: Min={Min:F2}V, Max={Max:F2}V, Delta={Delta:F2}V, Stable={Stable}",
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
                            Log.Information(
                                "Current stability: Min={Min:F3}A, Max={Max:F3}A, Delta={Delta:F3}A, Stable={Stable}",
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
                            Log.Information(
                                "Hx stability: Min={Min:F2}%, Max={Max:F2}%, Delta={Delta:F2}%, Stable={Stable}",
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
                            AnsiConsole.MarkupLine(
                                $"  AHR (Capacity): {aMin:F2}Ah - {aMax:F2}Ah (Δ{aDelta:F2}Ah) {status}");
                            Log.Information(
                                "AHR stability: Min={Min:F2}Ah, Max={Max:F2}Ah, Delta={Delta:F2}Ah, Stable={Stable}",
                                aMin, aMax, aDelta, aStable);
                        }

                        AnsiConsole.WriteLine();
                        if (successCount == RequiredReads && allStable)
                        {
                            AnsiConsole.MarkupLine("[green]═══ ACCEPTANCE CRITERIA: PASSED ═══[/]");
                            Log.Information(
                                "Acceptance criteria PASSED: {SuccessCount}/{RequiredReads} reads, all stable",
                                successCount, RequiredReads);
                        }
                        else
                        {
                            AnsiConsole.MarkupLine(
                                $"[yellow]═══ ACCEPTANCE CRITERIA: {(successCount < RequiredReads ? "INCOMPLETE" : "UNSTABLE")} ═══[/]");
                            Log.Warning(
                                "Acceptance criteria not fully met: {SuccessCount}/{RequiredReads} reads, stable={AllStable}",
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

                                Log.Information(
                                    "Cell voltages: Count={CellCount}, Min={Min}mV, Max={Max}mV, Avg={Avg}mV, Delta={Delta}mV",
                                    cells.CellCount,
                                    cells.MinVoltageMv,
                                    cells.MaxVoltageMv,
                                    cells.AvgVoltageMv,
                                    cells.DeltaVoltageMv);

                                successfulQueries++; // Track for session stats

                                // Note: 21 cells is partial - Leaf has 96 cell pairs, may need multiple Group 02 queries
                                if (cells.CellCount < 96)
                                {
                                    AnsiConsole.MarkupLine(
                                        $"[yellow]⚠[/] Note: Only {cells.CellCount}/96 cells returned (partial response)");
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


                    // ===========================================
                    // 2. Vehicle Identification (VIN)
                    // ===========================================
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[cyan]Querying Vehicle Identification (VIN)...[/]");
                    if (commands.TryGet<IVehicleIdentification>(out var vi))
                    {
                        try
                        {
                            var vinResponse = await vi.GetVinAsync(ct);

                            // Newer `IVehicleIdentification` implementations return the VIN directly.
                            // Keep the raw-ISO-TP parsing path as a fallback for older implementations.
                            if (!string.IsNullOrWhiteSpace(vinResponse) && vinResponse.Length == 17)
                            {
                                AnsiConsole.MarkupLine($"[green]✓[/] VIN: {vinResponse}");
                                Log.Information("VIN retrieved: {Vin}", vinResponse);
                                DecodeVin(vinResponse);
                                successfulQueries++;
                            }
                            else if (TryParseVin(vinResponse, out var vin))
                            {
                                AnsiConsole.MarkupLine($"[green]✓[/] VIN: {vin}");
                                Log.Information("VIN retrieved: {Vin}", vin);
                                successfulQueries++;
                            }
                            else
                            {
                                AnsiConsole.MarkupLine("[yellow]⚠[/] VIN: No valid response");
                                Log.Warning("VIN query returned invalid response");
                                invalidResponseQueries++;
                            }
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] VIN query failed: {ex.Message}");
                            Log.Warning(ex, "VIN query failed: {Message}", ex.Message);
                            failedQueries++;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]⚠[/] Vehicle Identification not available");
                        Log.Warning("Vehicle Identification capability not available");
                    }

                    // ===========================================
                    // 3. Motor Controller (Inverter/Motor) - SKIPPED
                    // ===========================================
                    // NOTE: Motor controller frames (0x1DA, 0x55A) only broadcast when accelerator is pressed
                    // or motor is actively running. Skipping this test when vehicle is stationary.
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine(
                        "[grey]Skipping Motor Controller (requires accelerator/motor running)...[/]");
                    Log.Information("Motor Controller test skipped - requires vehicle in motion");

                    // ===========================================
                    // 4. Onboard Charger - SKIPPED
                    // ===========================================
                    // NOTE: Charger frames (0x390, 0x393) only broadcast when vehicle is charging.
                    // Skipping this test when not plugged in.
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[grey]Skipping Onboard Charger (requires active charging)...[/]");
                    Log.Information("Onboard Charger test skipped - requires vehicle charging");

                    // ===========================================
                    // 5. Vehicle Control Module (VCM)
                    // ===========================================
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[cyan]Querying VCM status...[/]");
                    if (commands.TryGet<IVcm>(out var vcm))
                    {
                        try
                        {
                            var vcmStatus = await vcm.GetStatusAsync(ct);
                            var parts = new List<string>();

                            if (vcmStatus is null)
                            {
                                AnsiConsole.MarkupLine("[yellow]⚠[/] VCM: No response from ECU");
                                Log.Warning("VCM status returned null");
                                invalidResponseQueries++;
                            }
                            else
                            {
                                if (vcmStatus.ClimateControlActive is bool climateActive)
                                {
                                    parts.Add($"Climate: {(climateActive ? "ON" : "OFF")}");
                                }

                                if (vcmStatus.ClimateControlPowerKw is double climatePower)
                                {
                                    parts.Add($"Climate Power: {climatePower:F2}kW");
                                }

                                if (vcmStatus.OutsideAmbientTempC is double outsideTemp)
                                {
                                    parts.Add($"Outside: {outsideTemp:F1}°C");
                                }

                                if (vcmStatus.EcoIndicator is int eco)
                                {
                                    parts.Add($"Eco: {eco}/15");
                                }

                                if (vcmStatus.MotorCurrentAmps is int motorAmps)
                                {
                                    parts.Add($"Motor: {motorAmps}A");
                                }

                                if (vcmStatus.ThrottlePositionPercent is double throttle)
                                {
                                    parts.Add($"Throttle: {throttle:F1}%");
                                }

                                if (parts.Count > 0)
                                {
                                    AnsiConsole.MarkupLine($"[green]✓[/] VCM: {string.Join(", ", parts)}");
                                    Log.Information("VCM status: {Status}", string.Join(", ", parts));
                                    successfulQueries++;
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine("[yellow]⚠[/] VCM: No data available");
                                    Log.Warning("VCM status returned no data");
                                    invalidResponseQueries++;
                                }
                            }

                            // Also query gear position
                            try
                            {
                                var gear = await vcm.GetGearPositionAsync(ct);
                                AnsiConsole.MarkupLine($"[green]✓[/] Gear: {gear}");
                                Log.Information("Gear position: {Gear}", gear);
                                successfulQueries++;
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.MarkupLine($"[yellow]⚠[/] Gear position query failed: {ex.Message}");
                                Log.Warning(ex, "Gear position query failed: {Message}", ex.Message);
                                failedQueries++;
                            }
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] VCM query failed: {ex.Message}");
                            Log.Warning(ex, "VCM status query failed: {Message}", ex.Message);
                            failedQueries++;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]⚠[/] VCM not available");
                        Log.Warning("VCM capability not available");
                    }

                    // ===========================================
                    // 6. ABS (Anti-lock Braking System) - SKIPPED
                    // ===========================================
                    // NOTE: ABS frames (0x130, 0x245, 0x284, 0x285, 0x292, 0x354) only broadcast
                    // when wheels are moving or ABS is active. Skipping when stationary.
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[grey]Skipping ABS (requires wheel movement)...[/]");
                    Log.Information("ABS test skipped - requires wheels moving or ABS active");

                    // ===========================================
                    // 7. Brake System
                    // ===========================================
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[cyan]Querying Brake system status...[/]");
                    if (commands.TryGet<IBrake>(out var brake))
                    {
                        try
                        {
                            var brakeStatus = await brake.GetStatusAsync(ct);
                            // BrakeStatus is a struct, check if it has any meaningful data
                            AnsiConsole.MarkupLine("[green]✓[/] Brake: Status retrieved");
                            Log.Information("Brake status retrieved: {@BrakeStatus}", brakeStatus);
                            successfulQueries++;
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] Brake query failed: {ex.Message}");
                            Log.Warning(ex, "Brake status query failed: {Message}", ex.Message);
                            failedQueries++;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]⚠[/] Brake system not available");
                        Log.Warning("Brake capability not available");
                    }

                    // ===========================================
                    // 8. HVAC (Climate Control)
                    // ===========================================
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[cyan]Querying HVAC status...[/]");
                    if (commands.TryGet<IHvac>(out var hvac))
                    {
                        try
                        {
                            var hvacStatus = await hvac.GetStatusAsync(ct);
                            AnsiConsole.MarkupLine("[green]✓[/] HVAC: Status retrieved");
                            Log.Information("HVAC status retrieved: {@HvacStatus}", hvacStatus);
                            successfulQueries++;
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] HVAC query failed: {ex.Message}");
                            Log.Warning(ex, "HVAC status query failed: {Message}", ex.Message);
                            failedQueries++;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]⚠[/] HVAC not available");
                        Log.Warning("HVAC capability not available");
                    }

                    // ===========================================
                    // 9. Body Control
                    // ===========================================
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[cyan]Querying Body Control status...[/]");
                    if (commands.TryGet<IBodyControl>(out var bodyControl))
                    {
                        try
                        {
                            var bodyStatus = await bodyControl.GetStatusAsync(ct);
                            var parts = new List<string>
                            {
                                $"Doors: {(bodyStatus.DoorsLocked ? "Locked" : "Unlocked")}",
                                $"Headlights: {(bodyStatus.HeadlightsOn ? "ON" : "OFF")}",
                                $"Hazards: {(bodyStatus.HazardLightsOn ? "ON" : "OFF")}"
                            };

                            AnsiConsole.MarkupLine($"[green]✓[/] Body Control: {string.Join(", ", parts)}");
                            Log.Information("Body Control status: {Status}", string.Join(", ", parts));
                            successfulQueries++;
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] Body Control query failed: {ex.Message}");
                            Log.Warning(ex, "Body Control status query failed: {Message}", ex.Message);
                            failedQueries++;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]⚠[/] Body Control not available");
                        Log.Warning("Body Control capability not available");
                    }

                    // ===========================================
                    // 10. Steering
                    // ===========================================
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[cyan]Querying Steering status...[/]");
                    if (commands.TryGet<ISteering>(out var steering))
                    {
                        try
                        {
                            var steeringStatus = await steering.GetStatusAsync(ct);
                            AnsiConsole.MarkupLine(
                                $"[green]✓[/] Steering: Angle={steeringStatus.AngleDegrees:F1}°, Torque={steeringStatus.TorqueNm:F1}Nm");
                            Log.Information("Steering status: Angle={Angle:F1}°, Torque={Torque:F1}Nm",
                                steeringStatus.AngleDegrees, steeringStatus.TorqueNm);
                            successfulQueries++;
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.MarkupLine($"[red]✗[/] Steering query failed: {ex.Message}");
                            Log.Warning(ex, "Steering status query failed: {Message}", ex.Message);
                            failedQueries++;
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[yellow]⚠[/] Steering not available");
                        Log.Warning("Steering capability not available");
                    }

                    // ===========================================
                    // 11. Broadcast decode diagnostic
                    // Captures raw frames + decoded values so the wire format and signal bit
                    // layouts can be verified against reality (no broadcast frame had ever been
                    // decoded on hardware as of 2026-07-18). The 0x1DB voltage/current decode is
                    // cross-checkable against the BMS 2101 query values logged above.
                    // ===========================================
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[cyan]Broadcast decode diagnostic (20s, filter rotation)...[/]");
                    if (commands is LeafAze0CommandSet leafCommands)
                    {
                        try
                        {
                            await RunBroadcastDiagnosticAsync(leafCommands.Monitor, ct);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "Broadcast diagnostic failed: {Message}", ex.Message);
                        }
                    }

                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[green]═══ TEST COMPLETE ═══[/]");

                    var totalQueries = successfulQueries + invalidResponseQueries;
                    var successRate = totalQueries > 0 ? (double)successfulQueries / totalQueries * 100 : 0;

                    AnsiConsole.MarkupLine(
                        $"[cyan]Queries:[/] {successfulQueries}/{totalQueries} successful ({successRate:F0}%)");
                    Log.Information("Test complete - MonitorFrames={FrameCount}, QuerySuccess={Success}/{Total}",
                        0, successfulQueries, totalQueries);
                }
                catch (OperationCanceledException)
                {
                    AnsiConsole.MarkupLine("[yellow]✗ Session canceled by user[/]");
                    Log.Warning("Session canceled by user");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during vehicle session: {Message}", ex.Message);
                AnsiConsole.MarkupLine($"[red]✗ Session error:[/] {ex.Message.EscapeMarkup()}");
            }
            finally
            {
                // Final statistics
                var totalUptime = DateTime.UtcNow - sessionStart;

                DeviceRenderer.RenderSessionStats(
                    totalUptime,
                    monitoringFrameCount,
                    monitoringUniqueCanIds,
                    monitoringDuration,
                    successfulQueries,
                    invalidResponseQueries,
                    failedQueries);

                // Log session summary
                var totalQueries = successfulQueries + failedQueries + invalidResponseQueries;
                LogSessionSummary(selectedDevice, sessionStart, totalUptime, successfulQueries, invalidResponseQueries,
                    failedQueries);
            }
        }

        /// <summary>
        ///     Parse VIN from charger response.
        ///     From 2017 Leaf: 79A10156181314E3442\r79A215A304350334843\r79A2233313034303800
        ///     Decoded: 61 81 31 4E 34 42 5A 30 43 50 33 48 43 33 31 30 34 30 38 00
        ///     = "1N4BZ0CP3HC310408" (example)
        /// </summary>
        private static bool TryParseVin(string? response, out string? vin)
        {
            vin = null;

            if (string.IsNullOrEmpty(response))
            {
                return false;
            }

            try
            {
                var bytes = IsoTpParser.ParseIsoTpResponse(response);

                AnsiConsole.MarkupLine($"[grey]   Parsed {bytes.Count} bytes[/]");

                if (bytes.Count < 5)
                {
                    AnsiConsole.MarkupLine("[yellow]   Not enough data for VIN[/]");
                    return false;
                }

                // Show raw for debugging
                if (bytes.Count <= 25)
                {
                    AnsiConsole.MarkupLine($"[grey]   Raw: {BitConverter.ToString([.. bytes])}[/]");
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
                    // Some adapters/frames include spurious bytes in the VIN stream.
                    // VINs are restricted to 0-9 and A-Z (excluding I/O/Q), so extract exactly 17 valid VIN chars.
                    var vinBytes = bytes.Skip(vinStart);
                    Span<char> vinChars = stackalloc char[17];
                    var vinLen = 0;

                    foreach (var b in vinBytes)
                    {
                        if (b == 0x00)
                        {
                            break;
                        }

                        if (b >= (byte)'0' && b <= (byte)'9')
                        {
                            vinChars[vinLen++] = (char)b;
                        }
                        else if (b >= (byte)'A' && b <= (byte)'Z' && b != (byte)'I' && b != (byte)'O' && b != (byte)'Q')
                        {
                            vinChars[vinLen++] = (char)b;
                        }

                        if (vinLen == vinChars.Length)
                        {
                            break;
                        }
                    }

                    if (vinLen == vinChars.Length)
                    {
                        vin = new string(vinChars);
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
                    {
                        rawVin = rawVin[..17];
                    }

                    vin = rawVin;
                    AnsiConsole.MarkupLine($"   [green]VIN: {vin}[/]");
                    DecodeVin(vin);
                    return true;
                }

                AnsiConsole.MarkupLine("[yellow]   Could not extract VIN[/]");
                return false;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
                return false;
            }
        }
    }
}
