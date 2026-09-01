using ObdInsight.Core.Communication.Bluetooth;
using ObdInsight.Transports.WindowsBle;
using ObdInsight.UI;
using Serilog;
using Spectre.Console;

namespace ObdInsight.Application;

/// <summary>
///     Manages BLE device scanning and selection
/// </summary>
public class DeviceScanService
{
    private readonly TimeSpan _scanDuration;

    public DeviceScanService(TimeSpan scanDuration)
    {
        _scanDuration = scanDuration;
    }

    /// <summary>
    ///     Scans for and selects a BLE device
    /// </summary>
    public async Task<BleDeviceInfo?> ScanAndSelectDeviceAsync(
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
                if (await AnsiConsole.ConfirmAsync("No BLE devices found. Rescan?", true, ct))
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
            DeviceRenderer.RenderDeviceTable(orderedDevices, preferences);

            var favorite = preferences.GetPreferredDevice(orderedDevices);

            var actions = new List<string>();
            if (favorite != null)
                actions.Add($"Connect to favorite ({favorite.Name})");

            actions.Add("Choose device from list");
            actions.Add("Rescan");
            actions.Add("Cancel");

            var action = await AnsiConsole.PromptAsync(
                new SelectionPrompt<string>()
                    .Title("[cyan]Select an action:[/]")
                    .AddChoices(actions), ct);

            if (action.StartsWith("Connect to favorite", StringComparison.OrdinalIgnoreCase) && favorite != null)
            {
                Log.Information("User selected favorite device: {DeviceName} ({Address})", favorite.Name,
                    favorite.Address);
                preferences.RememberDevice(favorite, true);
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

            var selection = await AnsiConsole.PromptAsync(
                new TextPrompt<int>("[cyan]Enter device number:[/]")
                    .DefaultValue(defaultSelection)
                    .Validate(n => n >= 1 && n <= orderedDevices.Count
                        ? ValidationResult.Success()
                        : ValidationResult.Error($"Enter a number between 1 and {orderedDevices.Count}")), ct);

            var selectedDevice = orderedDevices[selection - 1];

            var shouldFavorite = preferences.IsFavorite(selectedDevice) ||
                                 await AnsiConsole.ConfirmAsync($"Mark {selectedDevice.Name} as a favorite?", false,
                                     ct);

            Log.Information("User selected device #{Number}: {DeviceName} ({Address}), Favorite={IsFavorite}",
                selection, selectedDevice.Name, selectedDevice.Address, shouldFavorite);
            preferences.RememberDevice(selectedDevice, shouldFavorite);

            AnsiConsole.MarkupLine($"[green]✓[/] Selected: [cyan]{selectedDevice.Name}[/] ({selectedDevice.Address})");
            return selectedDevice;
        }
    }

    private async Task<Dictionary<string, BleDeviceInfo>> PerformScanAsync(BleScanner scanner, CancellationToken ct)
    {
        var devices = new Dictionary<string, BleDeviceInfo>(StringComparer.OrdinalIgnoreCase);

        void OnDevice(object? _, BleDeviceDiscoveredEventArgs args)
        {
            devices[args.Device.Address] = args.Device;
        }

        scanner.DeviceDiscovered += OnDevice;

        try
        {
            Log.Information("Starting BLE scan for {Duration} seconds", _scanDuration.TotalSeconds);
            AnsiConsole.MarkupLine($"[cyan]Scanning for BLE devices ({_scanDuration.TotalSeconds:0}s)...[/]");

            await scanner.StartScanAsync(ct: ct);
            try
            {
                await Task.Delay(_scanDuration, ct);
            }
            finally
            {
                await scanner.StopScanAsync(ct);
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
}
