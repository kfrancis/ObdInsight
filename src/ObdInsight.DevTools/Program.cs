using ObdInsight.Core;
using ObdInsight.DevTools;
using Spectre.Console;

namespace ObdInsight.DevTools;

internal class Program
{
    private const string TargetMacAddress = "66:1e:87:02:c2:db";

    static async Task Main(string[] args)
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
                case "Discover device services":
                    await DiscoverServicesAsync(TargetMacAddress);
                    break;
                case "Exit":
                    return;
            }

            AnsiConsole.WriteLine();
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
}
