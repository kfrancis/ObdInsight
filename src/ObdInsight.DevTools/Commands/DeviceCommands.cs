using ObdInsight.Core.Transports.Ble;
using Spectre.Console;
using System.Runtime.InteropServices.WindowsRuntime;
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
    /// Display detailed information about the currently connected device.
    /// </summary>
    public static async Task ShowDeviceInfoAsync(DevToolsSession session)
    {
        if (!session.IsConnected || session.Transport is not WindowsBleTransport transport)
        {
            AnsiConsole.MarkupLine("[yellow]No device connected via BLE transport.[/]");
            return;
        }

        var grid = new Grid();
        grid.AddColumn(new GridColumn().Width(22));
        grid.AddColumn(new GridColumn());

        // Basic device info
        grid.AddRow("[grey]Device Name:[/]", $"[white]{session.DeviceName.EscapeMarkup()}[/]");
        grid.AddRow("[grey]Address:[/]", $"[cyan]{session.DeviceAddress}[/]");
        grid.AddRow("[grey]Connection Type:[/]", "[blue]Bluetooth Low Energy (BLE)[/]");
        grid.AddRow("[grey]Profile:[/]", $"[white]{session.Profile?.Name ?? "Unknown"}[/]");
        grid.AddRow("[grey]Service UUID:[/]", $"[cyan]{session.Profile?.ServiceUuid}[/]");
        
        // Connection statistics from the transport
        var diagnostics = transport.GetDiagnostics();
        grid.AddRow("[grey]Connection Stats:[/]", $"[white]{diagnostics.EscapeMarkup()}[/]");
        
        // Get Windows BLE device details
        if (!string.IsNullOrEmpty(session.DeviceAddress))
        {
            try
            {
                var mac = ParseMacAddress(session.DeviceAddress);
                using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(mac);
                
                if (device != null)
                {
                    grid.AddEmptyRow();
                    grid.AddRow("[grey]Connection Status:[/]", 
                        device.ConnectionStatus == BluetoothConnectionStatus.Connected 
                            ? "[green]Connected[/]" 
                            : "[red]Disconnected[/]");
                    
                    // Device appearance
                    if (device.Appearance != null)
                    {
                        grid.AddRow("[grey]Appearance:[/]", 
                            $"[white]{device.Appearance.Category} (0x{device.Appearance.RawValue:X})[/]");
                    }
                    
                    // Bluetooth device ID
                    grid.AddRow("[grey]Device ID:[/]", $"[grey]{device.BluetoothDeviceId.Id.EscapeMarkup()}[/]");
                    
                    // RSSI (signal strength) if available
                    try
                    {
                        // Note: RSSI may not be available when connected
                        var deviceInfo = await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(device.BluetoothDeviceId.Id);
                        if (deviceInfo?.Properties.TryGetValue("System.Devices.Aep.SignalStrength", out var rssiObj) == true)
                        {
                            if (rssiObj is int rssi)
                            {
                                var rssiColor = rssi switch
                                {
                                    > -50 => "green",
                                    > -70 => "yellow",
                                    _ => "red"
                                };
                                grid.AddRow("[grey]Signal Strength:[/]", $"[{rssiColor}]{rssi} dBm[/]");
                            }
                        }
                    }
                    catch
                    {
                        // RSSI not available
                    }
                    
                    // Device Information Service (if available)
                    var disResult = await device.GetGattServicesForUuidAsync(
                        Guid.Parse("0000180A-0000-1000-8000-00805F9B34FB"), // Device Information Service
                        BluetoothCacheMode.Cached);
                        
                    if (disResult.Status == GattCommunicationStatus.Success && disResult.Services.Count > 0)
                    {
                        grid.AddEmptyRow();
                        grid.AddRow("[cyan]Device Information[/]", "");
                        
                        using var disService = disResult.Services[0];
                        
                        // Try to read common DIS characteristics
                        await TryReadDisCharacteristic(grid, disService, "Manufacturer", 
                            Guid.Parse("00002A29-0000-1000-8000-00805F9B34FB"));
                        await TryReadDisCharacteristic(grid, disService, "Model Number", 
                            Guid.Parse("00002A24-0000-1000-8000-00805F9B34FB"));
                        await TryReadDisCharacteristic(grid, disService, "Serial Number", 
                            Guid.Parse("00002A25-0000-1000-8000-00805F9B34FB"));
                        await TryReadDisCharacteristic(grid, disService, "Hardware Rev", 
                            Guid.Parse("00002A27-0000-1000-8000-00805F9B34FB"));
                        await TryReadDisCharacteristic(grid, disService, "Firmware Rev", 
                            Guid.Parse("00002A26-0000-1000-8000-00805F9B34FB"));
                        await TryReadDisCharacteristic(grid, disService, "Software Rev", 
                            Guid.Parse("00002A28-0000-1000-8000-00805F9B34FB"));

                        await TryReadDisCharacteristic(grid, disService, "Something1",
                            Guid.Parse("00002a23-0000-1000-8000-00805f9b34fb"));

                        await TryReadDisCharacteristic(grid, disService, "Something2",
                            Guid.Parse("00002a2a-0000-1000-8000-00805f9b34fb"));

                        await TryReadDisCharacteristic(grid, disService, "Something3",
                            Guid.Parse("00002a50-0000-1000-8000-00805f9b34fb"));
                    }
                    
                    // List all available GATT services
                    var allServicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Cached);
                    if (allServicesResult.Status == GattCommunicationStatus.Success && allServicesResult.Services.Count > 0)
                    {
                        grid.AddEmptyRow();
                        grid.AddRow("[cyan]Available Services[/]", "");
                        
                        foreach (var service in allServicesResult.Services)
                        {
                            var serviceName = GetServiceName(service.Uuid);
                            grid.AddRow($"[grey]{serviceName}:[/]", $"[cyan]{service.Uuid}[/]");
                            service.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                grid.AddEmptyRow();
                grid.AddRow("[red]Error:[/]", $"[red]{ex.Message.EscapeMarkup()}[/]");
            }
        }
        
        var panel = new Panel(grid)
            .Border(BoxBorder.Rounded)
            .BorderColor(Color.Cyan1)
            .Header("[cyan]Device Information[/]");
        
        AnsiConsole.Write(panel);
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
                var serviceName = GetServiceName(service.Uuid);
                var serviceNode = tree.AddNode($"[yellow]{serviceName}[/] [grey]({service.Uuid})[/]");

                var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (charsResult.Status == GattCommunicationStatus.Success)
                {
                    foreach (var characteristic in charsResult.Characteristics)
                    {
                        var props = characteristic.CharacteristicProperties;
                        var propsStr = string.Join(", ", GetPropertyStrings(props));
                        var charNode = serviceNode.AddNode($"[green]Char:[/] {characteristic.Uuid} [grey]({propsStr})[/]");
                        
                        // If characteristic is readable, try to read its value
                        if (props.HasFlag(GattCharacteristicProperties.Read))
                        {
                            try
                            {
                                var readResult = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                                if (readResult.Status == GattCommunicationStatus.Success && readResult.Value.Length > 0)
                                {
                                    var bytes = new byte[readResult.Value.Length];
                                    readResult.Value.CopyTo(bytes);
                                    
                                    // Try to display as UTF-8 string first
                                    var stringValue = System.Text.Encoding.UTF8.GetString(bytes).Trim('\0');
                                    if (!string.IsNullOrWhiteSpace(stringValue) && stringValue.All(c => !char.IsControl(c) || char.IsWhiteSpace(c)))
                                    {
                                        charNode.AddNode($"[blue]Value:[/] [white]{stringValue.EscapeMarkup()}[/]");
                                    }
                                    else
                                    {
                                        // Display as hex if not valid UTF-8
                                        var hexValue = BitConverter.ToString(bytes).Replace("-", " ");
                                        charNode.AddNode($"[blue]Value (hex):[/] [white]{hexValue}[/]");
                                    }
                                }
                                else if (readResult.Status != GattCommunicationStatus.Success)
                                {
                                    charNode.AddNode($"[red]Read failed: {readResult.Status}[/]");
                                }
                            }
                            catch (Exception ex)
                            {
                                charNode.AddNode($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
                            }
                        }
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
    /// Read all readable characteristics from all services on the connected device.
    /// </summary>
    public static async Task ReadAllCharacteristicsAsync(DevToolsSession session)
    {
        if (!session.IsConnected)
        {
            AnsiConsole.MarkupLine("[yellow]No device connected.[/]");
            return;
        }

        if (string.IsNullOrEmpty(session.DeviceAddress))
        {
            AnsiConsole.MarkupLine("[yellow]No device address available.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Reading all characteristics from {session.DeviceName}...[/]");
        AnsiConsole.WriteLine();

        try
        {
            var mac = ParseMacAddress(session.DeviceAddress);
            using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(mac);

            if (device == null)
            {
                AnsiConsole.MarkupLine("[red]Failed to access device[/]");
                return;
            }

            var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Cached);

            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                AnsiConsole.MarkupLine($"[red]Failed to get services: {servicesResult.Status}[/]");
                return;
            }

            var totalReadable = 0;
            var successfulReads = 0;
            var failedReads = 0;

            foreach (var service in servicesResult.Services)
            {
                var serviceName = GetServiceName(service.Uuid);
                
                var charsResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Cached);
                if (charsResult.Status == GattCommunicationStatus.Success)
                {
                    var readableChars = charsResult.Characteristics
                        .Where(c => c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                        .ToList();

                    if (readableChars.Count > 0)
                    {
                        AnsiConsole.MarkupLine($"[cyan]Service:[/] [yellow]{serviceName}[/] [grey]({service.Uuid})[/]");
                        
                        foreach (var characteristic in readableChars)
                        {
                            totalReadable++;
                            
                            try
                            {
                                var readResult = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                                
                                if (readResult.Status == GattCommunicationStatus.Success)
                                {
                                    successfulReads++;
                                    var bytes = new byte[readResult.Value.Length];
                                    readResult.Value.CopyTo(bytes);
                                    
                                    AnsiConsole.Markup($"  [green]?[/] {characteristic.Uuid} [[{bytes.Length} bytes]]: ");
                                    
                                    // Try UTF-8 string first
                                    var stringValue = System.Text.Encoding.UTF8.GetString(bytes).Trim('\0');
                                    if (!string.IsNullOrWhiteSpace(stringValue) && stringValue.All(c => !char.IsControl(c) || char.IsWhiteSpace(c)))
                                    {
                                        AnsiConsole.MarkupLine($"[white]{stringValue.EscapeMarkup()}[/]");
                                    }
                                    else if (bytes.Length == 1)
                                    {
                                        // Single byte - show as decimal and hex
                                        AnsiConsole.MarkupLine($"[white]{bytes[0]} (0x{bytes[0]:X2})[/]");
                                    }
                                    else
                                    {
                                        // Show as hex dump
                                        var hexValue = BitConverter.ToString(bytes).Replace("-", " ");
                                        AnsiConsole.MarkupLine($"[white]{hexValue}[/]");
                                    }
                                }
                                else
                                {
                                    failedReads++;
                                    AnsiConsole.MarkupLine($"  [red]?[/] {characteristic.Uuid}: [red]{readResult.Status}[/]");
                                }
                            }
                            catch (Exception ex)
                            {
                                failedReads++;
                                AnsiConsole.MarkupLine($"  [red]?[/] {characteristic.Uuid}: [red]{ex.Message.EscapeMarkup()}[/]");
                            }
                        }
                        
                        AnsiConsole.WriteLine();
                    }
                }

                service.Dispose();
            }

            // Summary
            var summaryTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Metric")
                .AddColumn("Value");

            summaryTable.AddRow("Total Readable Characteristics", totalReadable.ToString());
            summaryTable.AddRow("[green]Successful Reads[/]", successfulReads.ToString());
            summaryTable.AddRow("[red]Failed Reads[/]", failedReads.ToString());
            summaryTable.AddRow("Success Rate", totalReadable > 0 
                ? $"{(successfulReads * 100.0 / totalReadable):F1}%" 
                : "N/A");

            AnsiConsole.Write(summaryTable);
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

    private static async Task TryReadDisCharacteristic(Grid grid, GattDeviceService service, 
        string name, Guid characteristicUuid)
    {
        try
        {
            var charResult = await service.GetCharacteristicsForUuidAsync(characteristicUuid, 
                BluetoothCacheMode.Cached);
                
            if (charResult.Status == GattCommunicationStatus.Success && charResult.Characteristics.Count > 0)
            {
                var characteristic = charResult.Characteristics[0];
                var readResult = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                
                if (readResult.Status == GattCommunicationStatus.Success)
                {
                    var bytes = new byte[readResult.Value.Length];
                    readResult.Value.CopyTo(bytes);
                    var value = System.Text.Encoding.UTF8.GetString(bytes).Trim('\0');
                    
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        grid.AddRow($"[grey]{name}:[/]", $"[white]{value.EscapeMarkup()}[/]");
                    }
                }
            }
        }
        catch
        {
            // Silently skip characteristics that can't be read
        }
    }

    private static async Task TryReadBatteryLevel(Grid grid, GattDeviceService service)
    {
        try
        {
            var batteryLevelUuid = Guid.Parse("00002A19-0000-1000-8000-00805F9B34FB");
            var charResult = await service.GetCharacteristicsForUuidAsync(batteryLevelUuid, 
                BluetoothCacheMode.Cached);
                
            if (charResult.Status == GattCommunicationStatus.Success && charResult.Characteristics.Count > 0)
            {
                var characteristic = charResult.Characteristics[0];
                var readResult = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                
                if (readResult.Status == GattCommunicationStatus.Success && readResult.Value.Length > 0)
                {
                    var bytes = new byte[readResult.Value.Length];
                    readResult.Value.CopyTo(bytes);
                    var batteryLevel = bytes[0];
                    
                    var batteryColor = batteryLevel switch
                    {
                        > 80 => "green",
                        > 50 => "yellow",
                        > 20 => "orange1",
                        _ => "red"
                    };
                    
                    grid.AddRow("[grey]Battery Level:[/]", $"[{batteryColor}]{batteryLevel}%[/]");
                }
            }
        }
        catch
        {
            // Silently skip if battery service can't be read
        }
    }

    private static string GetServiceName(Guid uuid)
    {
        // Known standard GATT services
        return uuid.ToString() switch
        {
            "00001800-0000-1000-8000-00805f9b34fb" => "Generic Access",
            "00001801-0000-1000-8000-00805f9b34fb" => "Generic Attribute",
            "0000180a-0000-1000-8000-00805f9b34fb" => "Device Information",
            "0000180f-0000-1000-8000-00805f9b34fb" => "Battery Service",
            "0000fff0-0000-1000-8000-00805f9b34fb" => "OBD Service (FFF0)",
            "0000ffe0-0000-1000-8000-00805f9b34fb" => "OBD Service (FFE0)",
            "6e400001-b5a3-f393-e0a9-e50e24dcca9e" => "Nordic UART",
            "00006287-3c17-d293-8e48-14fe2e4da212" => "Binary Protocol",
            _ => "Unknown Service"
        };
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
