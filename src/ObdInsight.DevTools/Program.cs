using ObdInsight.Core;
using ObdInsight.Core.Vehicles;
using ObdInsight.DevTools;
using ObdInsight.Drivers;
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
                        "Connect with vehicle detection",
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
}
