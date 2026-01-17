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

        private static async Task Main()
        {
            var preferences = DevicePreferences.Load();
            using var cts = new CancellationTokenSource();

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                var selectedDevice = await ScanAndSelectDeviceAsync(preferences, cts.Token);
                if (selectedDevice == null)
                {
                    Console.WriteLine("No device selected. Exiting.");
                    return;
                }

                await RunElm327SessionAsync(selectedDevice, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Operation cancelled by user.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
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
                    if (AnsiConsole.Confirm("No BLE devices found. Rescan?", defaultValue: true))
                        continue;

                    return null;
                }

                var orderedDevices = devices.Values
                    .OrderByDescending(d => d.Rssi)
                    .ToList();

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
                    preferences.RememberDevice(favorite, markAsFavorite: true);
                    AnsiConsole.MarkupLine($"[green]✓[/] Selected: [cyan]{favorite.Name}[/] ({favorite.Address})");
                    return favorite;
                }

                if (action.Equals("Rescan", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (action.Equals("Cancel", StringComparison.OrdinalIgnoreCase))
                    return null;

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

        private static async Task RunElm327SessionAsync(BleDeviceInfo selectedDevice, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(selectedDevice);

            Console.WriteLine($"\nConnecting to: {selectedDevice.Name}");

            await using var transport = new BleElmTransport(selectedDevice.Address);
            await transport.OpenAsync(ct);
            Console.WriteLine("Bluetooth connected.");

            var framer = new ElmFramer(transport);
            var session = new ElmSession(framer)
            {
                CommandTimeout = TimeSpan.FromSeconds(5),
                MaxConsecutiveFailures = 3
            };

            Console.WriteLine("Initializing ELM327 session...");
            await session.InitializeAndLockAsync(ct);
            Console.WriteLine("Session initialized and protocol locked.");

            Console.WriteLine("\nReading vehicle data (Ctrl+C to exit)...\n");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var rpmLines = await session.QueryAsync("010C", ct);
                    var rpmLine = rpmLines.FirstOrDefault(l =>
                        l.StartsWith("41 0C", StringComparison.OrdinalIgnoreCase));

                    if (rpmLine != null &&
                        ElmParsing.TryParseMode01Response(rpmLine, 0x0C, out var rpmData) &&
                        rpmData.Length >= 2)
                    {
                        var rpm = (256 * rpmData[0] + rpmData[1]) / 4.0;
                        Console.Write($"RPM: {rpm,5:F0}  ");
                    }

                    var speedLines = await session.QueryAsync("010D", ct);
                    var speedLine = speedLines.FirstOrDefault(l =>
                        l.StartsWith("41 0D", StringComparison.OrdinalIgnoreCase));

                    if (speedLine != null &&
                        ElmParsing.TryParseMode01Response(speedLine, 0x0D, out var speedData) &&
                        speedData.Length >= 1)
                    {
                        var speed = speedData[0];
                        Console.Write($"Speed: {speed,3} km/h  ");
                    }

                    var tempLines = await session.QueryAsync("0105", ct);
                    var tempLine = tempLines.FirstOrDefault(l =>
                        l.StartsWith("41 05", StringComparison.OrdinalIgnoreCase));

                    if (tempLine != null &&
                        ElmParsing.TryParseMode01Response(tempLine, 0x05, out var tempData) &&
                        tempData.Length >= 1)
                    {
                        var temp = tempData[0] - 40;
                        Console.Write($"Coolant: {temp,3}°C");
                    }

                    Console.WriteLine();
                    await Task.Delay(250, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"\nError reading data: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }
        }
    }
}