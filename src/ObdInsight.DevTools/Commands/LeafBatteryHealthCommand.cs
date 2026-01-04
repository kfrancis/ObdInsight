using ObdInsight.Core.Transports.Ble;
using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Battery health data collected from BMS.
/// </summary>
public record BatteryHealthData(
    double? SohBmsPercent,
    double? AhrCurrent,
    double? AhrNew,
    double? HxPercent,
    int? Gids,
    double? PackVoltage,
    double? CurrentAmps,
    int? DashSocPercent,
    double? HighResSocPercent)
{
    public double? SohFromAhrPercent => AhrNew > 0 && AhrCurrent > 0
        ? (AhrCurrent / AhrNew) * 100.0
        : null;

    public double? RemainingKwh => Gids.HasValue && Gids >= 0
        ? Gids.Value * 0.08
        : null;
}

/// <summary>
/// Cell voltage statistics for imbalance detection.
/// </summary>
public record CellVoltageSnapshot(
    List<double> CellVoltages,
    CellMeasurementCondition Condition,
    DateTime Timestamp)
{
    public double MinVoltage => CellVoltages.Count > 0 ? CellVoltages.Min() : 0;
    public double MaxVoltage => CellVoltages.Count > 0 ? CellVoltages.Max() : 0;
    public double AvgVoltage => CellVoltages.Count > 0 ? CellVoltages.Average() : 0;
    public double DeltaV => MaxVoltage - MinVoltage;
    public int CellCount => CellVoltages.Count;
    public int WeakestCellIndex => CellVoltages.Count > 0 ? CellVoltages.IndexOf(CellVoltages.Min()) : -1;
    public int StrongestCellIndex => CellVoltages.Count > 0 ? CellVoltages.IndexOf(CellVoltages.Max()) : -1;
}

public enum CellMeasurementCondition { Rest, Load }

/// <summary>
/// Temperature data from battery pack.
/// </summary>
public record BatteryTemperatureData(List<int> ModuleTemps, DateTime Timestamp)
{
    public int MinTemp => ModuleTemps.Count > 0 ? ModuleTemps.Min() : 0;
    public int MaxTemp => ModuleTemps.Count > 0 ? ModuleTemps.Max() : 0;
    public double AvgTemp => ModuleTemps.Count > 0 ? ModuleTemps.Average() : 0;
    public int TempSpread => MaxTemp - MinTemp;
}

/// <summary>
/// Complete battery health assessment result.
/// </summary>
public record BatteryHealthAssessment(
    BatteryHealthData? HealthData,
    CellVoltageSnapshot? CellsAtRest,
    CellVoltageSnapshot? CellsUnderLoad,
    BatteryTemperatureData? Temperatures,
    DateTime AssessmentTime,
    string PackType)
{
    public string OverallRating
    {
        get
        {
            var soh = HealthData?.SohBmsPercent ?? HealthData?.SohFromAhrPercent;
            if (!soh.HasValue) return "Unknown";
            return soh.Value switch
            {
                >= 90 => "Excellent",
                >= 80 => "Very Good",
                >= 70 => "Good",
                >= 60 => "Fair",
                >= 50 => "Moderate Degradation",
                _ => "Significant Degradation"
            };
        }
    }

    public string CellBalanceRating
    {
        get
        {
            var delta = CellsAtRest?.DeltaV ?? CellsUnderLoad?.DeltaV;
            if (!delta.HasValue) return "Unknown";
            return (delta.Value * 1000) switch
            {
                < 20 => "Excellent",
                < 50 => "Good",
                < 100 => "Fair",
                _ => "Poor"
            };
        }
    }
}

/// <summary>
/// Comprehensive Nissan Leaf battery health assessment command.
/// </summary>
public static class LeafBatteryHealthCommand
{
    // CAN IDs from OVMS
    private const int BMS_TXID = 0x79B;
    private const int BMS_RXID = 0x7BB;

    // Reference AHr values for different pack sizes (community-derived)
    private const double AHR_NEW_24KWH = 66.0;
    private const double AHR_NEW_30KWH = 79.0;
    private const double AHR_NEW_40KWH = 110.0;
    private const double AHR_NEW_62KWH = 170.0;

    /// <summary>
    /// Run comprehensive battery health assessment.
    /// </summary>
    public static async Task RunAsync(DevToolsSession session)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Nissan Leaf Battery Health Assessment[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Panel(
            """
            [yellow]Comprehensive Battery Health Analysis[/]

            This assessment collects multiple data points to provide an accurate,
            defensible battery State of Health (SoH) evaluation.

            [cyan]What we measure:[/]

            [green]1. BMS-Reported Health (Overall)[/]
               • SoH% - BMS estimate of remaining capacity vs new
               • AHr  - Usable charge capacity in Amp-hours

            [green]2. Cell Health (Imbalance Detection)[/]
               • Cell voltage spread at rest (ΔV_rest)
               • Cell voltage spread under load (ΔV_load) (optional)
               • Identification of weakest cell pairs

            [green]3. Temperature Check[/]
               • Battery module temperatures

            [yellow]Vehicle Requirements:[/]
            • Car should be in READY mode for best results
            • For load test: be prepared to accelerate briefly
            """)
            .Header("[cyan]Assessment Overview[/]")
            .Border(BoxBorder.Rounded));

        AnsiConsole.WriteLine();

        if (!session.IsConnected && !await session.ConnectAsync())
            return;

        var transport = session.Transport!;

        // Select pack type
        var packType = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select your battery pack size:[/]")
                .AddChoices(
                    "30 kWh (2016-2017 AZE0)",
                    "24 kWh (2011-2015 ZE0)",
                    "40 kWh (2018+ ZE1)",
                    "62 kWh (2019+ ZE1 e+)",
                    "Auto-detect (use BMS data)"));

        var ahrNew = packType switch
        {
            "24 kWh (2011-2015 ZE0)" => AHR_NEW_24KWH,
            "30 kWh (2016-2017 AZE0)" => AHR_NEW_30KWH,
            "40 kWh (2018+ ZE1)" => AHR_NEW_40KWH,
            "62 kWh (2019+ ZE1 e+)" => AHR_NEW_62KWH,
            _ => 0.0
        };

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[cyan]Starting assessment...[/]");
        AnsiConsole.WriteLine();

        async Task<string> SendCommandAsync(string cmd, TimeSpan timeout)
        {
            transport.DrainBuffer();
            await transport.WriteAsync(cmd + "\r");
            try
            {
                var response = await transport.ReadUntilAsync(">", timeout);
                return response.Replace(cmd, "").Replace(">", "").Replace("\r", " ").Replace("\n", " ").Trim();
            }
            catch (TimeoutException) { return "(timeout)"; }
        }

        async Task<string> SendAndCollectAsync(string cmd, TimeSpan initialWait, TimeSpan collectTime)
        {
            transport.DrainBuffer();
            await transport.WriteAsync(cmd + "\r");
            await Task.Delay(initialWait);

            var response = new System.Text.StringBuilder();
            var endTime = DateTime.UtcNow + collectTime;

            while (DateTime.UtcNow < endTime)
            {
                try
                {
                    var chunk = await transport.ReadUntilAsync(">", TimeSpan.FromMilliseconds(200));
                    response.Append(chunk);
                    if (chunk.Contains(">")) break;
                }
                catch (TimeoutException) { await Task.Delay(100); }
            }

            return response.ToString().Replace(cmd, "").Replace(">", "").Trim();
        }

        try
        {
            // Initialize adapter
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Initializing adapter...", async ctx =>
                {
                    await SendCommandAsync("ATZ", TimeSpan.FromSeconds(5));
                    await Task.Delay(500);
                    await SendCommandAsync("ATE0", TimeSpan.FromSeconds(2));
                    await SendCommandAsync("ATL0", TimeSpan.FromSeconds(2));
                    await SendCommandAsync("ATS0", TimeSpan.FromSeconds(2));
                    await SendCommandAsync("ATH1", TimeSpan.FromSeconds(2));
                    await SendCommandAsync("ATSP6", TimeSpan.FromSeconds(3));
                });

            BatteryHealthData? healthData = null;
            CellVoltageSnapshot? cellsAtRest = null;
            CellVoltageSnapshot? cellsUnderLoad = null;
            BatteryTemperatureData? temperatures = null;

            // PHASE 1: BMS Health Data
            AnsiConsole.Write(new Rule("[green]Phase 1: BMS Health Data[/]").RuleStyle("grey"));

            healthData = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Querying BMS for health data...", async ctx =>
                    await CollectBmsHealthDataAsync(SendCommandAsync, SendAndCollectAsync, ahrNew));

            if (healthData != null)
                DisplayHealthDataSummary(healthData, packType);
            else
                AnsiConsole.MarkupLine("[yellow]⚠ Could not retrieve BMS health data[/]");

            AnsiConsole.WriteLine();

            // PHASE 2: Cell Voltages at Rest
            AnsiConsole.Write(new Rule("[green]Phase 2: Cell Voltages (Rest)[/]").RuleStyle("grey"));
            AnsiConsole.MarkupLine("[grey]For best results, car should have been resting for 30+ minutes[/]");
            AnsiConsole.WriteLine();

            cellsAtRest = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Reading cell voltages...", async ctx =>
                    await CollectCellVoltagesAsync(SendCommandAsync, SendAndCollectAsync, CellMeasurementCondition.Rest));

            if (cellsAtRest != null && cellsAtRest.CellCount > 0)
                DisplayCellVoltageSummary(cellsAtRest, "Rest");
            else
                AnsiConsole.MarkupLine("[yellow]⚠ Could not retrieve cell voltages[/]");

            AnsiConsole.WriteLine();

            // PHASE 3: Temperatures
            AnsiConsole.Write(new Rule("[green]Phase 3: Battery Temperatures[/]").RuleStyle("grey"));

            temperatures = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Reading battery temperatures...", async ctx =>
                    await CollectTemperaturesAsync(SendCommandAsync, SendAndCollectAsync));

            if (temperatures != null && temperatures.ModuleTemps.Count > 0)
                DisplayTemperatureSummary(temperatures);
            else
                AnsiConsole.MarkupLine("[yellow]⚠ Could not retrieve temperature data[/]");

            AnsiConsole.WriteLine();

            // PHASE 4 (Optional): Load Test
            if (cellsAtRest?.CellCount > 0)
            {
                var doLoadTest = AnsiConsole.Confirm(
                    "[yellow]Run load test?[/] (Requires brief acceleration to measure cell sag)",
                    defaultValue: false);

                if (doLoadTest)
                {
                    AnsiConsole.Write(new Rule("[green]Phase 4: Cell Voltages (Load)[/]").RuleStyle("grey"));
                    AnsiConsole.WriteLine();

                    AnsiConsole.Write(new Panel(
                        """
                        [yellow]Load Test Instructions:[/]

                        1. Ensure car is in READY mode with foot on brake
                        2. When prompted, press accelerator firmly for 2-3 seconds
                        3. Release accelerator when instructed

                        [red]SAFETY: Ensure car is in PARK and area is clear![/]
                        """)
                        .Border(BoxBorder.Rounded));

                    if (AnsiConsole.Confirm("Ready to proceed?", defaultValue: false))
                    {
                        AnsiConsole.MarkupLine("[yellow]>>> Press accelerator NOW for 3 seconds <<<[/]");

                        cellsUnderLoad = await AnsiConsole.Status()
                            .Spinner(Spinner.Known.Dots)
                            .StartAsync("Capturing cell voltages under load...", async ctx =>
                            {
                                await Task.Delay(1000);
                                return await CollectCellVoltagesAsync(SendCommandAsync, SendAndCollectAsync, CellMeasurementCondition.Load);
                            });

                        AnsiConsole.MarkupLine("[green]>>> Release accelerator <<<[/]");
                        AnsiConsole.WriteLine();

                        if (cellsUnderLoad != null && cellsUnderLoad.CellCount > 0)
                        {
                            DisplayCellVoltageSummary(cellsUnderLoad, "Load");
                            if (cellsAtRest != null)
                                DisplayLoadComparisonSummary(cellsAtRest, cellsUnderLoad);
                        }
                    }
                }
            }

            AnsiConsole.WriteLine();

            // FINAL ASSESSMENT
            var assessment = new BatteryHealthAssessment(
                healthData, cellsAtRest, cellsUnderLoad, temperatures, DateTime.Now, packType);

            DisplayFinalAssessment(assessment);

            if (AnsiConsole.Confirm("Export assessment to file?", defaultValue: false))
                await ExportAssessmentAsync(assessment, session);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error during assessment: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    private static async Task<BatteryHealthData?> CollectBmsHealthDataAsync(
        Func<string, TimeSpan, Task<string>> sendCommand,
        Func<string, TimeSpan, TimeSpan, Task<string>> sendAndCollect,
        double ahrNewReference)
    {
        double? sohBms = null, ahrCurrent = null, hxPercent = null;
        double? ahrNew = ahrNewReference > 0 ? ahrNewReference : null;
        int? gids = null, dashSoc = null;
        double? packVoltage = null, currentAmps = null, highResSoc = null;

        // Configure for BMS with proper flow control
        await sendCommand($"ATSH{BMS_TXID:X3}", TimeSpan.FromSeconds(2));
        await sendCommand($"ATCRA{BMS_RXID:X3}", TimeSpan.FromSeconds(2));
        await sendCommand($"ATFCSH{BMS_TXID:X3}", TimeSpan.FromSeconds(2));
        await sendCommand("ATFCSD300000", TimeSpan.FromSeconds(2));
        await sendCommand("ATFCSM1", TimeSpan.FromSeconds(2));

        // Query Group 01 - need longer collection time for multi-frame response
        var g01Response = await sendAndCollect("2101", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(8));
        var g01Bytes = ParseIsoTpResponse(g01Response);

        // Debug output
        AnsiConsole.MarkupLine($"[grey]   Group 01: {g01Bytes.Count} bytes parsed[/]");
        if (g01Bytes.Count > 0 && g01Bytes.Count <= 60)
        {
            AnsiConsole.MarkupLine($"[grey]   Raw: {BitConverter.ToString(g01Bytes.ToArray())}[/]");
        }

        // Parse Group 01 data
        // Based on verified 2017 AZE0 data layout:
        // Bytes 0-1: Response header (61 01)
        // Bytes 2-5: Current (signed 32-bit BE, /2 for amps)
        // Byte 22: Dashboard SOC (direct percentage)
        // For capacity - scan for reasonable values since offset varies by model
        if (g01Bytes.Count >= 23)
        {
            // Battery current (bytes 2-5, signed 32-bit big-endian, divide by 2 for amps)
            if (g01Bytes.Count >= 6)
            {
                uint currentUnsigned = ((uint)g01Bytes[2] << 24) | ((uint)g01Bytes[3] << 16) |
                                       ((uint)g01Bytes[4] << 8) | g01Bytes[5];
                int currentRaw = unchecked((int)currentUnsigned);
                if (currentRaw != -1 && Math.Abs(currentRaw / 2.0) < 500) // -1 = 0xFFFFFFFF = invalid
                {
                    currentAmps = currentRaw / 2.0;
                    AnsiConsole.MarkupLine($"[grey]   Current: {currentAmps:F1}A[/]");
                }
            }

            // Dashboard SOC (byte 22)
            if (g01Bytes[22] is > 0 and <= 100)
            {
                dashSoc = g01Bytes[22];
                AnsiConsole.MarkupLine($"[grey]   Dashboard SOC: {dashSoc}%[/]");
            }

            // Scan for capacity value - it's a 4-byte value that when divided by 10000 gives 30-80 Ah
            // For AZE0, commonly found around bytes 29-32 or nearby
            for (int i = 25; i <= Math.Min(g01Bytes.Count - 4, 40); i++)
            {
                var capacityRaw = (g01Bytes[i] << 24) | (g01Bytes[i + 1] << 16) | (g01Bytes[i + 2] << 8) | g01Bytes[i + 3];
                var capacityAh = capacityRaw / 10000.0;
                
                if (capacityAh is > 20 and < 100)
                {
                    ahrCurrent = capacityAh;
                    AnsiConsole.MarkupLine($"[grey]   Found capacity at byte {i}: {capacityAh:F2} Ah (raw 0x{capacityRaw:X8})[/]");
                    break;
                }
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]   ⚠ Group 01 response too short ({g01Bytes.Count} bytes, need 23+)[/]");
        }

        // Query Group 61 - SOH data (shorter response, should work reliably)
        var g61Response = await sendAndCollect("2161", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        var g61Bytes = ParseIsoTpResponse(g61Response);

        AnsiConsole.MarkupLine($"[grey]   Group 61: {g61Bytes.Count} bytes parsed[/]");
        if (g61Bytes.Count > 0 && g61Bytes.Count <= 20)
        {
            AnsiConsole.MarkupLine($"[grey]   Raw: {BitConverter.ToString(g61Bytes.ToArray())}[/]");
        }

        if (g61Bytes.Count >= 6 && g61Bytes[0] == 0x61 && g61Bytes[1] == 0x61)
        {
            // Bytes 2-3: Current capacity (raw units)
            var currentCap = (g61Bytes[2] << 8) | g61Bytes[3];
            // Bytes 4-5: Original capacity (raw units)
            var originalCap = (g61Bytes[4] << 8) | g61Bytes[5];

            AnsiConsole.MarkupLine($"[grey]   SOH raw: current={currentCap}, original={originalCap}[/]");

            if (originalCap > 0)
            {
                sohBms = (currentCap / (double)originalCap) * 100.0;
                AnsiConsole.MarkupLine($"[grey]   Calculated SOH: {sohBms:F1}%[/]");
                
                // If we have SOH but no AHr from Group 01, derive AHr from SOH
                // For 30kWh pack: 79 Ah new × SOH% = current capacity
                if (!ahrCurrent.HasValue && ahrNew.HasValue && sohBms > 0)
                {
                    ahrCurrent = ahrNew.Value * (sohBms.Value / 100.0);
                    AnsiConsole.MarkupLine($"[green]   Derived AHr from SOH: {ahrCurrent:F2} Ah[/]");
                }
            }
        }

        return new BatteryHealthData(sohBms, ahrCurrent, ahrNew, hxPercent, gids, packVoltage, currentAmps, dashSoc, highResSoc);
    }

    private static async Task<CellVoltageSnapshot?> CollectCellVoltagesAsync(
        Func<string, TimeSpan, Task<string>> sendCommand,
        Func<string, TimeSpan, TimeSpan, Task<string>> sendAndCollect,
        CellMeasurementCondition condition)
    {
        await sendCommand($"ATSH{BMS_TXID:X3}", TimeSpan.FromSeconds(2));
        await sendCommand($"ATCRA{BMS_RXID:X3}", TimeSpan.FromSeconds(2));
        await sendCommand($"ATFCSH{BMS_TXID:X3}", TimeSpan.FromSeconds(2));
        await sendCommand("ATFCSD300000", TimeSpan.FromSeconds(2));
        await sendCommand("ATFCSM1", TimeSpan.FromSeconds(2));

        var g02Response = await sendAndCollect("2102", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(8));
        var bytes = ParseIsoTpResponse(g02Response);

        if (bytes.Count < 4) return null;

        int dataStart = (bytes.Count >= 2 && bytes[0] == 0x61 && bytes[1] == 0x02) ? 2 : 0;
        var cellData = bytes.Skip(dataStart).ToList();
        var voltages = new List<double>();

        for (int i = 0; i + 1 < cellData.Count && voltages.Count < 96; i += 2)
        {
            int millivolt = (cellData[i] << 8) | cellData[i + 1];
            if ((millivolt >= 2500 && millivolt <= 4300) || (millivolt >= 0x0D00 && millivolt <= 0x1000))
                voltages.Add(millivolt / 1000.0);
        }

        return voltages.Count > 0 ? new CellVoltageSnapshot(voltages, condition, DateTime.Now) : null;
    }

    private static async Task<BatteryTemperatureData?> CollectTemperaturesAsync(
        Func<string, TimeSpan, Task<string>> sendCommand,
        Func<string, TimeSpan, TimeSpan, Task<string>> sendAndCollect)
    {
        await sendCommand($"ATSH{BMS_TXID:X3}", TimeSpan.FromSeconds(2));
        await sendCommand($"ATCRA{BMS_RXID:X3}", TimeSpan.FromSeconds(2));
        await sendCommand($"ATFCSH{BMS_TXID:X3}", TimeSpan.FromSeconds(2));
        await sendCommand("ATFCSD300000", TimeSpan.FromSeconds(2));
        await sendCommand("ATFCSM1", TimeSpan.FromSeconds(2));

        var g04Response = await sendAndCollect("2104", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
        var bytes = ParseIsoTpResponse(g04Response);

        if (bytes.Count < 6 || bytes[0] != 0x61 || bytes[1] != 0x04) return null;

        var data = bytes.Skip(2).ToArray();
        var temps = new List<int>();
        var tempPositions = new[] { 0, 2, 10, 12 };

        foreach (var pos in tempPositions)
        {
            if (pos < data.Length && data[pos] != 0xFF)
            {
                var tempC = (sbyte)data[pos];
                if (tempC is >= -30 and <= 60)
                    temps.Add(tempC);
            }
        }

        return temps.Count > 0 ? new BatteryTemperatureData(temps, DateTime.Now) : null;
    }

    private static void DisplayHealthDataSummary(BatteryHealthData data, string packType)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[cyan]BMS Health Data[/]")
            .AddColumn("Metric")
            .AddColumn("Value")
            .AddColumn("Status");

        if (data.SohBmsPercent.HasValue)
        {
            var sohColor = data.SohBmsPercent.Value >= 70 ? "green" : (data.SohBmsPercent.Value >= 50 ? "yellow" : "red");
            var sohStatus = data.SohBmsPercent.Value >= 90 ? "Excellent" : (data.SohBmsPercent.Value >= 80 ? "Very Good" : (data.SohBmsPercent.Value >= 70 ? "Good" : "Fair"));
            table.AddRow("SoH (BMS)", $"[{sohColor}]{data.SohBmsPercent.Value:F1}%[/]", sohStatus);
        }
        else
            table.AddRow("SoH (BMS)", "[grey]Not available[/]", "-");

        if (data.AhrCurrent.HasValue)
            table.AddRow("Capacity (AHr)", $"[cyan]{data.AhrCurrent.Value:F2} Ah[/]", "Current usable");

        if (data.SohFromAhrPercent.HasValue)
        {
            var ahrSohColor = data.SohFromAhrPercent.Value >= 70 ? "green" : "yellow";
            table.AddRow("SoH (from AHr)", $"[{ahrSohColor}]{data.SohFromAhrPercent.Value:F1}%[/]", "Cross-check");
        }

        if (data.DashSocPercent.HasValue)
            table.AddRow("Dashboard SOC", $"[green]{data.DashSocPercent.Value}%[/]", "Current charge");

        if (data.CurrentAmps.HasValue)
        {
            var dir = data.CurrentAmps.Value > 0 ? "Discharging" : (data.CurrentAmps.Value < 0 ? "Charging" : "Idle");
            table.AddRow("Current", $"{Math.Abs(data.CurrentAmps.Value):F1}A", dir);
        }

        AnsiConsole.Write(table);
    }

    private static void DisplayCellVoltageSummary(CellVoltageSnapshot snapshot, string condition)
    {
        var deltaColor = (snapshot.DeltaV * 1000) < 50 ? "green" : ((snapshot.DeltaV * 1000) < 100 ? "yellow" : "red");
        var balanceStatus = (snapshot.DeltaV * 1000) < 20 ? "Excellent" : ((snapshot.DeltaV * 1000) < 50 ? "Good" : ((snapshot.DeltaV * 1000) < 100 ? "Fair" : "Poor"));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[cyan]Cell Voltages ({condition})[/]")
            .AddColumn("Metric")
            .AddColumn("Value");

        table.AddRow("Cells Measured", $"{snapshot.CellCount}");
        table.AddRow("Minimum", $"[cyan]{snapshot.MinVoltage:F3}V[/] (Cell {snapshot.WeakestCellIndex + 1})");
        table.AddRow("Maximum", $"[cyan]{snapshot.MaxVoltage:F3}V[/] (Cell {snapshot.StrongestCellIndex + 1})");
        table.AddRow("Average", $"[cyan]{snapshot.AvgVoltage:F3}V[/]");
        table.AddRow($"[bold]ΔV ({condition})[/]", $"[{deltaColor}]{snapshot.DeltaV * 1000:F0}mV[/] - {balanceStatus}");
        table.AddRow("Pack Total", $"[yellow]{snapshot.CellVoltages.Sum():F1}V[/]");

        AnsiConsole.Write(table);

        if (snapshot.WeakestCellIndex >= 0)
            AnsiConsole.MarkupLine($"[grey]   Weakest cell pair: #{snapshot.WeakestCellIndex + 1} at {snapshot.MinVoltage:F3}V[/]");
    }

    private static void DisplayTemperatureSummary(BatteryTemperatureData temps)
    {
        var avgColor = temps.AvgTemp < 25 ? "green" : (temps.AvgTemp < 40 ? "yellow" : "red");
        var tempStatus = temps.AvgTemp < 0 ? "Below freezing" : (temps.AvgTemp < 10 ? "Cold" : (temps.AvgTemp < 25 ? "Optimal" : (temps.AvgTemp < 40 ? "Warm" : "Hot")));

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[cyan]Battery Temperatures[/]")
            .AddColumn("Metric")
            .AddColumn("Value");

        table.AddRow("Sensors", $"{temps.ModuleTemps.Count}");
        table.AddRow("Min", $"{temps.MinTemp}°C");
        table.AddRow("Max", $"{temps.MaxTemp}°C");
        table.AddRow("Average", $"[{avgColor}]{temps.AvgTemp:F1}°C[/]");
        table.AddRow("Spread", $"{temps.TempSpread}°C");
        table.AddRow("Status", tempStatus);

        AnsiConsole.Write(table);
    }

    private static void DisplayLoadComparisonSummary(CellVoltageSnapshot rest, CellVoltageSnapshot load)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Rest vs Load Comparison[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var deltaRestMv = rest.DeltaV * 1000;
        var deltaLoadMv = load.DeltaV * 1000;
        var deltaIncrease = deltaLoadMv - deltaRestMv;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Condition")
            .AddColumn("ΔV")
            .AddColumn("Weakest Cell");

        table.AddRow("At Rest", $"{deltaRestMv:F0}mV", $"Cell {rest.WeakestCellIndex + 1}");
        table.AddRow("Under Load", $"{deltaLoadMv:F0}mV", $"Cell {load.WeakestCellIndex + 1}");
        table.AddRow("Increase", $"{deltaIncrease:F0}mV", "-");

        AnsiConsole.Write(table);

        if (rest.WeakestCellIndex == load.WeakestCellIndex)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Cell {rest.WeakestCellIndex + 1} is consistently the weakest[/]");
            AnsiConsole.MarkupLine("[grey]   This cell pair may be the limiting factor for your pack.[/]");
        }

        if (deltaIncrease > 30)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠ Significant voltage spread increase under load (+{deltaIncrease:F0}mV)[/]");
            AnsiConsole.MarkupLine("[grey]   This suggests elevated internal resistance in some cells.[/]");
        }
        else if (deltaIncrease > 10)
        {
            AnsiConsole.MarkupLine($"[green]✓ Normal voltage spread increase under load (+{deltaIncrease:F0}mV)[/]");
        }
    }

    private static void DisplayFinalAssessment(BatteryHealthAssessment assessment)
    {
        AnsiConsole.Write(new Rule("[green]═══ FINAL ASSESSMENT ═══[/]").RuleStyle("green"));
        AnsiConsole.WriteLine();

        var ratingColor = assessment.OverallRating is "Excellent" or "Very Good" ? "green" : (assessment.OverallRating is "Good" or "Fair" ? "yellow" : "red");

        AnsiConsole.Write(new Panel(
            $"""
            [bold]Overall Battery Health: [{ratingColor}]{assessment.OverallRating}[/][/]

            Pack Type: {assessment.PackType}
            Assessment Time: {assessment.AssessmentTime:g}
            """)
            .Header("[green]Summary[/]")
            .Border(BoxBorder.Double));

        AnsiConsole.WriteLine();

        // Health Panel
        if (assessment.HealthData != null)
        {
            var grid = new Grid().AddColumn().AddColumn().AddColumn();
            grid.AddRow(
                new Panel(assessment.HealthData.SohBmsPercent.HasValue ? $"[bold]{assessment.HealthData.SohBmsPercent.Value:F1}%[/]" : "[grey]N/A[/]")
                    .Header("[cyan]SoH (BMS)[/]").Border(BoxBorder.Rounded),
                new Panel(assessment.HealthData.AhrCurrent.HasValue ? $"[bold]{assessment.HealthData.AhrCurrent.Value:F1} Ah[/]" : "[grey]N/A[/]")
                    .Header("[cyan]Capacity (AHr)[/]").Border(BoxBorder.Rounded),
                new Panel(assessment.HealthData.SohFromAhrPercent.HasValue ? $"[bold]{assessment.HealthData.SohFromAhrPercent.Value:F1}%[/]" : "[grey]N/A[/]")
                    .Header("[cyan]SoH (AHr)[/]").Border(BoxBorder.Rounded));
            AnsiConsole.Write(grid);
        }

        AnsiConsole.WriteLine();

        // Cell Health Panel
        var balanceColor = assessment.CellBalanceRating is "Excellent" or "Good" ? "green" : (assessment.CellBalanceRating == "Fair" ? "yellow" : "red");

        var cellGrid = new Grid().AddColumn().AddColumn().AddColumn();
        cellGrid.AddRow(
            new Panel(assessment.CellsAtRest != null ? $"[bold]{assessment.CellsAtRest.DeltaV * 1000:F0}mV[/]" : "[grey]N/A[/]")
                .Header("[cyan]ΔV (Rest)[/]").Border(BoxBorder.Rounded),
            new Panel(assessment.CellsUnderLoad != null ? $"[bold]{assessment.CellsUnderLoad.DeltaV * 1000:F0}mV[/]" : "[grey]Not tested[/]")
                .Header("[cyan]ΔV (Load)[/]").Border(BoxBorder.Rounded),
            new Panel($"[{balanceColor}]{assessment.CellBalanceRating}[/]")
                .Header("[cyan]Balance[/]").Border(BoxBorder.Rounded));
        AnsiConsole.Write(cellGrid);

        AnsiConsole.WriteLine();

        // Recommendations
        AnsiConsole.Write(new Rule("[yellow]Recommendations[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var recommendations = new List<string>();

        if (assessment.HealthData?.SohBmsPercent < 70)
            recommendations.Add("• Battery has moderate to significant degradation. Consider range expectations.");

        if (assessment.CellsAtRest?.DeltaV > 0.050)
            recommendations.Add("• Cell imbalance detected. A full charge to 100% may help with balancing.");

        if (assessment.CellsAtRest != null && assessment.CellsUnderLoad != null &&
            assessment.CellsAtRest.WeakestCellIndex == assessment.CellsUnderLoad.WeakestCellIndex)
            recommendations.Add($"• Cell pair #{assessment.CellsAtRest.WeakestCellIndex + 1} is consistently weak and may limit pack performance.");

        if (assessment.Temperatures?.AvgTemp < 10)
            recommendations.Add("• Battery is cold. Allow warming before fast charging for best performance.");
        else if (assessment.Temperatures?.AvgTemp > 35)
            recommendations.Add("• Battery is warm. Allow cooling before fast charging to preserve longevity.");

        if (recommendations.Count == 0)
            recommendations.Add("• Battery appears to be in good condition. Continue normal use.");

        foreach (var rec in recommendations)
            AnsiConsole.MarkupLine(rec);

        AnsiConsole.WriteLine();
    }

    private static async Task ExportAssessmentAsync(BatteryHealthAssessment assessment, DevToolsSession session)
    {
        var timestamp = assessment.AssessmentTime.ToString("yyyyMMdd_HHmmss");
        var defaultName = $"leaf_battery_health_{timestamp}.txt";

        var fileName = AnsiConsole.Prompt(new TextPrompt<string>("[cyan]Save as:[/]").DefaultValue(defaultName));
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) fileName += ".txt";

        var filePath = Path.Combine(Environment.CurrentDirectory, fileName);

        var content = new System.Text.StringBuilder();
        content.AppendLine("================================================================================");
        content.AppendLine("NISSAN LEAF BATTERY HEALTH ASSESSMENT REPORT");
        content.AppendLine("================================================================================");
        content.AppendLine();
        content.AppendLine($"Assessment Date: {assessment.AssessmentTime:yyyy-MM-dd HH:mm:ss}");
        content.AppendLine($"Pack Type: {assessment.PackType}");
        content.AppendLine($"Device: {session.DeviceName ?? "Unknown"}");
        content.AppendLine();
        content.AppendLine($"OVERALL RATING: {assessment.OverallRating}");
        content.AppendLine();

        if (assessment.HealthData != null)
        {
            content.AppendLine("BMS HEALTH DATA");
            content.AppendLine($"  SoH (BMS):      {assessment.HealthData.SohBmsPercent?.ToString("F1") ?? "N/A"}%");
            content.AppendLine($"  SoH (from AHr): {assessment.HealthData.SohFromAhrPercent?.ToString("F1") ?? "N/A"}%");
            content.AppendLine($"  Capacity:       {assessment.HealthData.AhrCurrent?.ToString("F2") ?? "N/A"} Ah");
            content.AppendLine($"  Dashboard SOC:  {assessment.HealthData.DashSocPercent?.ToString() ?? "N/A"}%");
            content.AppendLine();
        }

        if (assessment.CellsAtRest != null)
        {
            content.AppendLine("CELL VOLTAGES (REST)");
            content.AppendLine($"  Cells: {assessment.CellsAtRest.CellCount}");
            content.AppendLine($"  Min:   {assessment.CellsAtRest.MinVoltage:F3}V (Cell {assessment.CellsAtRest.WeakestCellIndex + 1})");
            content.AppendLine($"  Max:   {assessment.CellsAtRest.MaxVoltage:F3}V");
            content.AppendLine($"  ΔV:    {assessment.CellsAtRest.DeltaV * 1000:F0}mV");
            content.AppendLine($"  Total: {assessment.CellsAtRest.CellVoltages.Sum():F1}V");
            content.AppendLine();
        }

        if (assessment.Temperatures != null)
        {
            content.AppendLine("TEMPERATURES");
            content.AppendLine($"  Avg: {assessment.Temperatures.AvgTemp:F1}°C");
            content.AppendLine($"  Range: {assessment.Temperatures.MinTemp}°C to {assessment.Temperatures.MaxTemp}°C");
            content.AppendLine();
        }

        content.AppendLine("================================================================================");

        await File.WriteAllTextAsync(filePath, content.ToString());
        AnsiConsole.MarkupLine($"[green]✓[/] Report saved to: [cyan]{filePath.EscapeMarkup()}[/]");
    }

    private static List<byte> ParseIsoTpResponse(string response)
    {
        var bytes = new List<byte>();
        if (string.IsNullOrWhiteSpace(response)) return bytes;

        var cleaned = response.Replace("\r", "\n").Replace(">", "").Trim();
        var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var frameSequence = new List<(int Type, int Seq, byte[] Data)>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 6) continue;
            if (!IsCanIdPrefix(trimmed)) continue;

            var frameHex = trimmed[3..];
            if (frameHex.Length < 2) continue;
            if (!byte.TryParse(frameHex[..2], System.Globalization.NumberStyles.HexNumber, null, out var frameTypeByte)) continue;

            var frameType = (frameTypeByte & 0xF0) >> 4;
            var frameInfo = frameTypeByte & 0x0F;
            byte[] frameData;

            switch (frameType)
            {
                case 0:
                    var sfLen = frameInfo;
                    frameData = ParseHexString(frameHex[2..]);
                    if (frameData.Length > sfLen) frameData = frameData[..sfLen];
                    frameSequence.Add((0, 0, frameData));
                    break;
                case 1:
                    if (frameHex.Length < 4) continue;
                    frameData = ParseHexString(frameHex[4..]);
                    frameSequence.Add((1, 0, frameData));
                    break;
                case 2:
                    frameData = ParseHexString(frameHex[2..]);
                    frameSequence.Add((2, frameInfo, frameData));
                    break;
                default:
                    frameData = ParseHexString(frameHex);
                    if (frameData.Length > 0) frameSequence.Add((-1, 0, frameData));
                    break;
            }
        }

        var firstFrame = frameSequence.FirstOrDefault(f => f.Type == 0 || f.Type == 1);
        if (firstFrame.Data != null) bytes.AddRange(firstFrame.Data);

        foreach (var cf in frameSequence.Where(f => f.Type == 2).OrderBy(f => f.Seq))
            bytes.AddRange(cf.Data);

        if (bytes.Count == 0)
            foreach (var line in lines.Where(l => l.Trim().All(c => Uri.IsHexDigit(c))))
                bytes.AddRange(ParseHexString(line.Trim()));

        return bytes;
    }

    private static bool IsCanIdPrefix(string s) =>
        s.Length >= 3 && s[..3].All(c => Uri.IsHexDigit(c)) &&
        int.TryParse(s[..3], System.Globalization.NumberStyles.HexNumber, null, out var id) &&
        id >= 0x700 && id <= 0x7FF;

    private static byte[] ParseHexString(string hex)
    {
        var result = new List<byte>();
        for (int i = 0; i + 1 < hex.Length; i += 2)
            if (byte.TryParse(hex.Substring(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                result.Add(b);
            else break;
        return result.ToArray();
    }
}
