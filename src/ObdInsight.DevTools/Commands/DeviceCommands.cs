using ObdInsight.Core.Communication.Bluetooth;
using Spectre.Console;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Commands for device discovery and connection management.
/// </summary>
public static class DeviceCommands
{
    /// <summary>
    /// Scan for BLE devices and optionally select one.
    /// </summary>
    public static async Task<BleDeviceInfo?> ScanAndSelectDeviceAsync(DevToolsSession session)
    {
        using var scanner = new WindowsBleScanner();

        var devices = new Dictionary<string, BleDeviceInfo>();

        scanner.DeviceDiscovered += (_, e) =>
        {
            devices[e.Device.Address] = e.Device;
        };

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Scanning for BLE devices (10 seconds)...", async ctx =>
            {
                await scanner.StartScanAsync();
                await Task.Delay(TimeSpan.FromSeconds(10));
                await scanner.StopScanAsync();
            });

        if (devices.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No BLE devices found.[/]");
            return null;
        }

        // Display found devices, marking those we've seen before
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("#")
            .AddColumn("Name")
            .AddColumn("Address")
            .AddColumn("RSSI")
            .AddColumn("Saved");

        var orderedDevices = devices.Values.OrderByDescending(d => d.Rssi).ToList();
        var savedDevices = session.DeviceHistory.Devices;
        var index = 1;

        foreach (var device in orderedDevices)
        {
            var rssiColor = device.Rssi switch
            {
                > -50 => "green",
                > -70 => "yellow",
                _ => "red"
            };

            // Check if this device is in our history
            var savedDevice = savedDevices.FirstOrDefault(s => 
                s.Address.Replace(":", "").Equals(device.Address.Replace(":", ""), StringComparison.OrdinalIgnoreCase));
            var savedIndicator = savedDevice != null 
                ? (savedDevice.IsFavorite ? "[yellow]?[/]" : "[green]?[/]") 
                : "[grey]-[/]";

            table.AddRow(
                index.ToString(),
                device.Name.EscapeMarkup(),
                $"[cyan]{device.Address}[/]",
                $"[{rssiColor}]{device.Rssi} dBm[/]",
                savedIndicator
            );
            index++;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Found {devices.Count} devices ([yellow]?[/]=favorite, [green]?[/]=saved)[/]");
        AnsiConsole.WriteLine();

        // Ask user to select a device
        if (!AnsiConsole.Confirm("Select a device to use?"))
            return null;

        var selection = AnsiConsole.Prompt(
            new TextPrompt<int>("[cyan]Enter device number:[/]")
                .DefaultValue(1)
                .Validate(n => n >= 1 && n <= orderedDevices.Count
                    ? ValidationResult.Success()
                    : ValidationResult.Error($"Enter a number between 1 and {orderedDevices.Count}")));

        var selectedDevice = orderedDevices[selection - 1];
        
        // Set the device in the session
        session.SetDevice(selectedDevice.Address, selectedDevice.Name);
        
        // Save to history immediately (will be updated again on successful connect)
        session.DeviceHistory.AddOrUpdate(selectedDevice.Address, selectedDevice.Name, BleDeviceProfile.VeepeakBle.Name);
        
        AnsiConsole.MarkupLine($"[green]?[/] Selected: [cyan]{selectedDevice.Name}[/] ({selectedDevice.Address})");

        return selectedDevice;
    }

    /// <summary>
    /// Manually set a device address.
    /// </summary>
    public static void SetDeviceAddress(DevToolsSession session)
    {
        var address = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Enter MAC address (e.g., 48:23:35:12:6D:6A):[/]")
                .Validate(mac =>
                {
                    var clean = mac.Replace(":", "").Replace("-", "");
                    return clean.Length == 12 && clean.All(c => Uri.IsHexDigit(c))
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Invalid MAC address format");
                }));

        var name = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Device name (optional):[/]")
                .DefaultValue("OBD Adapter")
                .AllowEmpty());

        // Select profile
        var profileChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select BLE profile:[/]")
                .AddChoices(
                    "Veepeak BLE+ (FFF0) - Most common",
                    "Veepeak BLE+ Alt (FFE0)",
                    "OBDLink MX+",
                    "Nordic UART Service"
                ));

        var profile = profileChoice switch
        {
            "Veepeak BLE+ Alt (FFE0)" => BleDeviceProfile.VeepeakBleAlt,
            "OBDLink MX+" => BleDeviceProfile.ObdLinkMx,
            "Nordic UART Service" => BleDeviceProfile.NordicUart,
            _ => BleDeviceProfile.VeepeakBle
        };

        session.SetDevice(address, string.IsNullOrWhiteSpace(name) ? null : name, profile);
        AnsiConsole.MarkupLine($"[green]?[/] Device set: [cyan]{session.DeviceName}[/] ({session.DeviceAddress})");
    }

    /// <summary>
    /// Discover and display all GATT services on a device.
    /// </summary>
    public static async Task DiscoverServicesAsync(DevToolsSession session)
    {
        if (string.IsNullOrEmpty(session.DeviceAddress))
        {
            AnsiConsole.MarkupLine("[red]No device selected.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Discovering services on {session.DeviceName}...[/]");

        try
        {
            var mac = ParseMacAddress(session.DeviceAddress);
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(mac);

            if (device == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to connect to device[/]");
                return;
            }

            var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);

            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                AnsiConsole.MarkupLine($"[red]Failed to get services: {servicesResult.Status}[/]");
                return;
            }

            var deviceName = device.Name?.EscapeMarkup() ?? "Unknown";
            var tree = new Tree($"[cyan]{deviceName}[/] ({session.DeviceAddress})");

            foreach (var service in servicesResult.Services)
            {
                var serviceNode = tree.AddNode($"[yellow]Service:[/] {service.Uuid}");

                var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (charsResult.Status == GattCommunicationStatus.Success)
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

    /// <summary>
    /// Show all known BLE profiles.
    /// </summary>
    public static void ShowKnownProfiles()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Profile")
            .AddColumn("Service UUID")
            .AddColumn("Write Char")
            .AddColumn("Notify Char")
            .AddColumn("Notes");

        foreach (var profile in BleDeviceProfile.AllProfiles)
        {
            table.AddRow(
                profile.Name,
                profile.ServiceUuid.ToString()[..8] + "...",
                profile.WriteCharacteristicUuid.ToString()[..8] + "...",
                profile.NotifyCharacteristicUuid.ToString()[..8] + "...",
                profile.WriteWithResponse ? "Write w/ response" : "Write w/o response"
            );
        }

        AnsiConsole.Write(table);
    }

    private static ulong ParseMacAddress(string mac)
    {
        var cleanMac = mac.Replace(":", "").Replace("-", "");
        return Convert.ToUInt64(cleanMac, 16);
    }

    private static IEnumerable<string> GetPropertyStrings(GattCharacteristicProperties props)
    {
        if (props.HasFlag(GattCharacteristicProperties.Read))
            yield return "Read";
        if (props.HasFlag(GattCharacteristicProperties.Write))
            yield return "Write";
        if (props.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
            yield return "WriteNoResp";
        if (props.HasFlag(GattCharacteristicProperties.Notify))
            yield return "Notify";
        if (props.HasFlag(GattCharacteristicProperties.Indicate))
            yield return "Indicate";
    }
}
