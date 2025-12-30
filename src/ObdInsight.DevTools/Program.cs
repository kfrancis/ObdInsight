using ObdInsight.Core;
using ObdInsight.Core.Adapters;
using ObdInsight.Core.Adapters.Elm327;
using ObdInsight.Core.Diagnostics;
using ObdInsight.Core.Transports.Ble;
using ObdInsight.Core.Transports.Tracing;
using ObdInsight.Core.Vehicles;
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
                        "Record OBD session",
                        "Generate Vehicle Support Report",
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
                case "Record OBD session":
                    await RecordSessionAsync();
                    break;
                case "Generate Vehicle Support Report":
                    await GenerateVehicleSupportReportAsync();
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

    private static async Task GenerateVehicleSupportReportAsync()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Vehicle Support Report Generator[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[yellow]This tool collects diagnostic data to help add support for new vehicles and OBD adapters.[/]");
        AnsiConsole.MarkupLine("[grey]The report will be saved as a markdown file suitable for GitHub issues.[/]");
        AnsiConsole.WriteLine();

        // Step 1: Get user vehicle info
        var userInfo = CollectUserVehicleInfo();

        // Step 2: Get adapter MAC address
        var macAddress = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Enter OBD adapter MAC address:[/]")
                .DefaultValue(TargetMacAddress)
                .Validate(mac =>
                {
                    var clean = mac.Replace(":", "").Replace("-", "");
                    return clean.Length == 12 && clean.All(c => Uri.IsHexDigit(c))
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Invalid MAC address format");
                }));

        // Step 3: Select BLE profile
        var bleProfile = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select BLE adapter profile:[/]")
                .AddChoices(
                    "Veepeak BLE+ (FFF0/FFF1/FFF2)",
                    "Veepeak BLE+ Alt (FFE0/FFE1)",
                    "Nordic UART Service",
                    "Auto-detect (try all)"
                ));

        var profile = bleProfile switch
        {
            "Veepeak BLE+ (FFF0/FFF1/FFF2)" => BleDeviceProfile.VeepeakBle,
            "Veepeak BLE+ Alt (FFE0/FFE1)" => BleDeviceProfile.VeepeakBleAlt,
            "Nordic UART Service" => BleDeviceProfile.NordicUart,
            _ => BleDeviceProfile.VeepeakBle
        };

        AnsiConsole.WriteLine();

        if (!AnsiConsole.Confirm("[yellow]Ready to start diagnostic collection. Vehicle should be on (ignition on or running). Continue?[/]"))
        {
            return;
        }

        // Create collector
        var collector = new DiagnosticDataCollector();
        BleAdapterInfo? bleInfo = null;
        ObdAdapterInfo? obdAdapterInfo = null;
        VehicleIdentification? vehicleId = null;
        SupportedPidsInfo? supportedPids = null;

        // Progress display
        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var bleTask = ctx.AddTask("[cyan]Collecting BLE adapter info...[/]");
                var obdTask = ctx.AddTask("[cyan]Collecting OBD adapter info...[/]");
                var vinTask = ctx.AddTask("[cyan]Reading vehicle identification...[/]");
                var pidsTask = ctx.AddTask("[cyan]Querying supported PIDs...[/]");
                var probeTask = ctx.AddTask("[cyan]Probing standard PIDs...[/]");
                var evTask = ctx.AddTask("[cyan]Probing EV/extended PIDs...[/]");

                // Collect BLE info
                bleTask.StartTask();
                bleInfo = await collector.CollectBleInfoAsync(macAddress);
                bleTask.Value = 100;

                // Connect transport
                using var transport = new WindowsBleTransport(profile);
                var adapter = new Elm327Adapter();

                var connected = await transport.ConnectAsync(macAddress);
                if (!connected)
                {
                    AnsiConsole.MarkupLine("[red]Failed to connect to BLE device![/]");
                    return;
                }

                // Initialize adapter
                obdTask.StartTask();
                var initialized = await adapter.InitializeAsync(transport);
                if (initialized)
                {
                    obdAdapterInfo = await collector.CollectObdAdapterInfoAsync(adapter);
                }
                obdTask.Value = 100;

                // Collect vehicle ID
                vinTask.StartTask();
                vehicleId = await collector.CollectVehicleIdAsync(adapter);
                vinTask.Value = 100;

                // Collect supported PIDs
                pidsTask.StartTask();
                supportedPids = await collector.CollectSupportedPidsAsync(adapter);
                pidsTask.Value = 100;

                // Probe standard PIDs
                probeTask.StartTask();
                await collector.ProbeStandardPidsAsync(adapter, supportedPids);
                probeTask.Value = 100;

                // Probe extended PIDs
                evTask.StartTask();
                await collector.ProbeExtendedPidsAsync(adapter);
                evTask.Value = 100;

                await transport.DisconnectAsync();
            });

        // Build report
        var report = collector.BuildReport(userInfo, bleInfo, obdAdapterInfo, vehicleId, supportedPids);

        // Generate markdown
        var markdown = MarkdownReportGenerator.Generate(report);

        // Save to file
        var fileName = $"vehicle_report_{userInfo.Year}_{userInfo.Make}_{userInfo.Model}_{DateTime.Now:yyyyMMdd_HHmmss}.md"
            .Replace(" ", "_")
            .Replace("/", "-");

        var filePath = Path.Combine(Environment.CurrentDirectory, fileName);
        await File.WriteAllTextAsync(filePath, markdown);

        // Display summary
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Report Generated Successfully[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        // Show summary table
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Item")
            .AddColumn("Value");

        summaryTable.AddRow("Vehicle", $"{userInfo.Year} {userInfo.Make} {userInfo.Model}");
        summaryTable.AddRow("VIN", vehicleId?.Vin != null ? MaskVin(vehicleId.Vin) : "[grey]Not available[/]");
        summaryTable.AddRow("Adapter", obdAdapterInfo?.VersionResponse?.Trim() ?? "[grey]Unknown[/]");
        summaryTable.AddRow("Protocol", obdAdapterInfo?.ProtocolDescription?.Trim() ?? "[grey]Unknown[/]");
        summaryTable.AddRow("Mode 01 PIDs", $"{supportedPids?.Mode01Pids.Count ?? 0} supported");
        summaryTable.AddRow("Report File", $"[link={filePath}]{fileName}[/]");

        AnsiConsole.Write(summaryTable);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]✓[/] Report saved to: [cyan]{0}[/]", filePath.EscapeMarkup());
        AnsiConsole.WriteLine();

        // Instructions
        AnsiConsole.Write(new Panel(
            """
            [yellow]Next Steps:[/]
            
            1. Open a new issue at: [link]https://github.com/kfrancis/ObdInsight/issues/new[/]
            2. Use the title format: [cyan]Vehicle Support: {Year} {Make} {Model}[/]
            3. Copy the contents of the generated markdown file into the issue
            4. Add any additional observations about your vehicle
            
            [grey]Thank you for helping improve ObdInsight![/]
            """)
            .Header("[cyan]Submit to GitHub[/]")
            .Border(BoxBorder.Rounded));

        // Offer to open file
        if (AnsiConsole.Confirm("Open the report file now?"))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                AnsiConsole.MarkupLine("[yellow]Could not open file automatically. Please open manually.[/]");
            }
        }
    }

    private static UserVehicleInfo CollectUserVehicleInfo()
    {
        AnsiConsole.MarkupLine("[cyan]Please enter your vehicle information:[/]");
        AnsiConsole.WriteLine();

        var year = AnsiConsole.Prompt(
            new TextPrompt<int>("[cyan]Vehicle Year:[/]")
                .DefaultValue(DateTime.Now.Year)
                .Validate(y => y >= 1996 && y <= DateTime.Now.Year + 1
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Year must be between 1996 and current year")));

        var make = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Make (e.g., Honda, Toyota, Nissan):[/]")
                .Validate(m => !string.IsNullOrWhiteSpace(m)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Make is required")));

        var model = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Model (e.g., CR-V, Camry, Leaf):[/]")
                .Validate(m => !string.IsNullOrWhiteSpace(m)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Model is required")));

        var trim = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Trim (optional, e.g., EX-L, XLE):[/]")
                .AllowEmpty());

        var engineType = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Engine/Powertrain Type:[/]")
                .AddChoices(
                    "Gasoline",
                    "Diesel",
                    "Hybrid",
                    "Plug-in Hybrid (PHEV)",
                    "Electric (BEV)",
                    "Other/Unknown"));

        var transmission = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Transmission Type:[/]")
                .AddChoices(
                    "Automatic",
                    "CVT",
                    "Manual",
                    "Dual-Clutch (DCT)",
                    "Single-Speed (EV)",
                    "Other/Unknown"));

        var notes = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Additional Notes (optional, any relevant details):[/]")
                .AllowEmpty());

        return new UserVehicleInfo
        {
            Year = year,
            Make = make.Trim(),
            Model = model.Trim(),
            Trim = string.IsNullOrWhiteSpace(trim) ? null : trim.Trim(),
            EngineType = engineType,
            TransmissionType = transmission,
            AdditionalNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        };
    }

    private static string MaskVin(string vin)
    {
        if (vin.Length <= 6)
            return new string('*', vin.Length);
        return vin[..^6] + "******";
    }

    private static async Task RecordSessionAsync()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Record OBD Session[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[yellow]This tool records all OBD communication for replay in unit tests.[/]");
        AnsiConsole.MarkupLine("[grey]The trace file will be saved in JSONL format.[/]");
        AnsiConsole.WriteLine();

        // Get MAC address
        var macAddress = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Enter OBD adapter MAC address:[/]")
                .DefaultValue(TargetMacAddress)
                .Validate(mac =>
                {
                    var clean = mac.Replace(":", "").Replace("-", "");
                    return clean.Length == 12 && clean.All(c => Uri.IsHexDigit(c))
                        ? ValidationResult.Success()
                        : ValidationResult.Error("Invalid MAC address format");
                }));

        // Get session description
        var description = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Session description (optional):[/]")
                .AllowEmpty());

        // Select BLE profile
        var bleProfile = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select BLE adapter profile:[/]")
                .AddChoices(
                    "Veepeak BLE+ (FFF0/FFF1/FFF2)",
                    "Veepeak BLE+ Alt (FFE0/FFE1)",
                    "Nordic UART Service"
                ));

        var profile = bleProfile switch
        {
            "Veepeak BLE+ (FFF0/FFF1/FFF2)" => BleDeviceProfile.VeepeakBle,
            "Veepeak BLE+ Alt (FFE0/FFE1)" => BleDeviceProfile.VeepeakBleAlt,
            "Nordic UART Service" => BleDeviceProfile.NordicUart,
            _ => BleDeviceProfile.VeepeakBle
        };

        AnsiConsole.WriteLine();

        // Create transport with recording
        using var baseTransport = new WindowsBleTransport(profile);
        var tracer = new TransportTracer();
        using var transport = new RecordingTransportDecorator(baseTransport, tracer);

        // Setup live traffic logging
        transport.DataSent += (_, data) => LogBleTraffic("TX", data);
        transport.DataReceived += (_, data) => LogBleTraffic("RX", data);

        // Connect
        var connected = await AnsiConsole.Status()
            .StartAsync($"Connecting to {macAddress}...", async ctx =>
            {
                return await baseTransport.ConnectAsync(macAddress);
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
            DeviceAddress = macAddress,
            DeviceName = profile.Name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description
        };
        transport.StartRecording(sessionMetadata);

        AnsiConsole.MarkupLine("[yellow]Recording started. All commands will be traced.[/]");
        AnsiConsole.WriteLine();

        // Run command loop with recording
        var adapter = new Elm327Adapter();
        adapter.Log += (_, e) => LogAdapter(e);

        var initialized = await AnsiConsole.Status()
            .StartAsync("Initializing ELM327 adapter...", async ctx =>
            {
                return await adapter.InitializeAsync(transport);
            });

        if (initialized)
        {
            // Update metadata with detected protocol
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
        await RunRecordingCommandLoopAsync(transport, adapter);

        // Stop recording
        var session = transport.StopRecording();

        // Disconnect
        await baseTransport.DisconnectAsync();
        AnsiConsole.MarkupLine("[grey]Disconnected[/]");

        // Save the recording
        await SaveRecordedSessionAsync(session);
    }

    private static async Task RunRecordingCommandLoopAsync(RecordingTransportDecorator transport, Elm327Adapter adapter)
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

    private static async Task SaveRecordedSessionAsync(TransportSession session)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Recording Complete[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        // Display summary
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Property")
            .AddColumn("Value");

        summaryTable.AddRow("Session ID", session.SessionId);
        summaryTable.AddRow("Duration", session.Duration.ToString(@"mm\:ss\.fff"));
        summaryTable.AddRow("Total Entries", session.EntryCount.ToString());
        summaryTable.AddRow("Bytes TX", session.TotalBytesTx.ToString());
        summaryTable.AddRow("Bytes RX", session.TotalBytesRx.ToString());
        summaryTable.AddRow("Protocol", session.Metadata.Protocol ?? "[grey]Unknown[/]");
        summaryTable.AddRow("Adapter", session.Metadata.AdapterVersion ?? "[grey]Unknown[/]");

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        // Generate filename
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var defaultName = $"obd_session_{timestamp}.jsonl";

        var fileName = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Save as:[/]")
                .DefaultValue(defaultName));

        if (!fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            fileName += ".jsonl";

        var filePath = Path.Combine(Environment.CurrentDirectory, fileName);

        // Save
        var serializer = new JsonLTransportSessionSerializer();
        await serializer.SaveAsync(session, filePath);

        AnsiConsole.MarkupLine($"[green]✓[/] Session saved to: [cyan]{filePath.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();

        // Show usage instructions
        AnsiConsole.Write(new Panel(
            $"""
            [yellow]Usage in Unit Tests:[/]
            
            ```csharp
            // Load and replay the session
            var transport = await ReplayTransportFactory.FromFileAsync("{fileName}");
            var adapter = new Elm327Adapter();
            await adapter.InitializeAsync(transport);
            
            // Your test assertions here...
            ```
            
            [grey]The trace file can be embedded as a test resource or loaded from disk.[/]
            """)
            .Header("[cyan]Next Steps[/]")
            .Border(BoxBorder.Rounded));

        // Offer to open file location
        if (AnsiConsole.Confirm("Open file location?", defaultValue: false))
        {
            try
            {
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{filePath}\"",
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                }
            }
            catch
            {
                AnsiConsole.MarkupLine("[yellow]Could not open file location.[/]");
            }
        }
    }
}
