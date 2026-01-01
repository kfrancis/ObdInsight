using ObdInsight.Core.Transports.Ble;
using ObdInsight.Core.Transports.Tracing;
using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Nissan Leaf-specific commands based on OVMS implementation.
/// Includes ECU wakeup sequences and BMS communication.
/// </summary>
/// <remarks>
/// Key insights from OVMS vehicle_nissanleaf.cpp:
/// - ECUs sleep when car is off - wakeup messages required
/// - BMS uses TX:0x79B / RX:0x7BB with Mode 21 (manufacturer-specific)
/// - Charger uses TX:0x797 / RX:0x79A
/// - Multiple wakeup strategies exist for different model years
/// - Multi-frame responses require ISO-TP flow control
/// </remarks>
public static class NissanLeafCommands
{
    // CAN IDs from OVMS
    private const int BMS_TXID = 0x79B;
    private const int BMS_RXID = 0x7BB;
    private const int CHARGER_TXID = 0x797;
    private const int CHARGER_RXID = 0x79A;
    private const int BROADCAST_TXID = 0x7DF;

    // Wakeup CAN IDs
    private const int VCM_WAKEUP_ID = 0x679;
    private const int BATTERY_HEATER_WAKEUP_ID = 0x5C0;
    private const int TCU_WAKEUP_ID = 0x68C;

    // EV-CAN Broadcast Frame IDs (from DBC glossary)
    // These are passively broadcast by the car - no request needed when car is ON
    private const int CAN_LB_STATUS = 0x1DB;      // LB_Current, LB_Total_Voltage, LB_Usable_SOC (dash SOC)
    private const int CAN_LB_LIMITS = 0x1DC;      // Discharge/Charge Power Limits
    private const int CAN_INVERTER = 0x1DA;       // Motor voltage, torque, RPM
    private const int CAN_LB_SOC = 0x55B;         // High-resolution SOC (0.1%)
    private const int CAN_LB_GIDS = 0x5BC;        // GIDs (remaining capacity), SOH, charge time
    private const int CAN_LB_TEMPS = 0x5C0;       // Battery temperatures, heater status
    private const int CAN_RANGE = 0x5A9;          // Range display, ECO mode
    private const int CAN_QC_CAPACITY = 0x59E;    // Full/Remaining capacity for QC (Wh)

    // Session recording state
    private static TransportTracer? _activeTracer;
    private static List<string> _sessionLog = new();
    private static bool _isRecording;

    /// <summary>
    /// Run comprehensive Nissan Leaf diagnostics with proper wakeup sequence.
    /// </summary>
    public static async Task RunLeafDiagnosticsAsync(DevToolsSession session)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Nissan Leaf Diagnostics (OVMS-style)[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Panel(
            """
            [yellow]Nissan Leaf Communication Protocol[/]
            
            Based on OVMS (Open Vehicle Monitoring System) implementation.
            
            [cyan]Key Points:[/]
            • ECUs sleep when car is off - wakeup sequence required
            • BMS (Battery Management System): TX 0x79B ? RX 0x7BB
            • Charger: TX 0x797 ? RX 0x79A
            • Uses Mode 21 (manufacturer-specific diagnostic)
            • Multi-frame responses use ISO-TP flow control
            
            [green]Wakeup Messages:[/]
            • 0x679 - VCM startup spoof
            • 0x5C0 - Battery heater request spoof
            • 0x68C - TCU wakeup (pre-2013)
            
            [yellow]Vehicle State:[/]
            The car should be in READY mode (foot on brake + start)
            or actively charging for best results.
            """)
            .Header("[cyan]Protocol Information[/]")
            .Border(BoxBorder.Rounded));

        AnsiConsole.WriteLine();

        if (!session.IsConnected)
        {
            if (!await session.ConnectAsync())
                return;
        }

        var transport = session.Transport!;

        // Helper to send raw AT command (no logging during send)
        async Task<string> SendCommandAsync(string cmd, TimeSpan timeout)
        {
            transport.DrainBuffer();
            await transport.WriteAsync(cmd + "\r");
            try
            {
                var response = await transport.ReadUntilAsync(">", timeout);
                return response.Replace(cmd, "").Replace(">", "").Replace("\r", " ").Replace("\n", " ").Trim();
            }
            catch (TimeoutException)
            {
                return "(timeout)";
            }
        }

        try
        {
            // Step 1: Initialize adapter
            AnsiConsole.MarkupLine("[cyan]Step 1: Initialize ELM327 adapter[/]");
            
            var initCommands = new (string Cmd, string Desc, TimeSpan Timeout)[]
            {
                ("ATZ", "Reset", TimeSpan.FromSeconds(5)),
                ("ATE0", "Echo off", TimeSpan.FromSeconds(2)),
                ("ATL0", "Linefeeds off", TimeSpan.FromSeconds(2)),
                ("ATS0", "Spaces off", TimeSpan.FromSeconds(2)),
                ("ATH1", "Headers on", TimeSpan.FromSeconds(2)),
                ("ATSP6", "Protocol: CAN 11-bit 500k", TimeSpan.FromSeconds(3)),
            };

            foreach (var (cmd, desc, timeout) in initCommands)
            {
                var resp = await SendCommandAsync(cmd, timeout);
                var status = resp.Contains("OK") || resp.Contains("ELM") ? "[green]?[/]" : "[yellow]?[/]";
                AnsiConsole.MarkupLine($"   {status} {cmd}: [grey]{resp.EscapeMarkup()}[/]");
                await Task.Delay(200);
            }

            AnsiConsole.WriteLine();

            // Step 2: Configure ISO-TP flow control for multi-frame responses
            AnsiConsole.MarkupLine("[cyan]Step 2: Configure ISO-TP flow control[/]");
            AnsiConsole.MarkupLine("[grey]   (Required for multi-frame BMS responses)[/]");
            
            var flowControlSetup = new (string Cmd, string Desc)[]
            {
                ("ATCAF0", "CAN auto-formatting off"),
                ($"ATSH{BMS_TXID:X3}", $"Set TX header to BMS (0x{BMS_TXID:X3})"),
                ($"ATCRA{BMS_RXID:X3}", $"Filter RX to BMS response (0x{BMS_RXID:X3})"),
                ($"ATFCSH{BMS_TXID:X3}", "Flow control TX header"),
                ("ATFCSD300000", "Flow control data: CTS, BS=0, STmin=0"),
                ("ATFCSM1", "Flow control mode: auto-respond"),
            };

            foreach (var (cmd, desc) in flowControlSetup)
            {
                var resp = await SendCommandAsync(cmd, TimeSpan.FromSeconds(2));
                var status = !resp.Contains("?") && !resp.Contains("ERROR") ? "[green]?[/]" : "[red]?[/]";
                AnsiConsole.MarkupLine($"   {status} {cmd} [grey]({desc})[/]");
                await Task.Delay(200);
            }

            AnsiConsole.WriteLine();

            // Step 3: Query BMS data groups
            AnsiConsole.MarkupLine("[cyan]Step 3: Query BMS data (Mode 21)[/]");

            var bmsQueries = new (string Cmd, string Desc, int ExpectedLen)[]
            {
                ("2101", "Group 01: SOC, Capacity, Current, Voltage", 39),
                ("2102", "Group 02: Cell Voltages (96 cells)", 196),
                ("2104", "Group 04: Pack Temperatures", 14),
            };

            var results = new List<(string Cmd, string Desc, string Response, bool Success)>();

            foreach (var (cmd, desc, expectedLen) in bmsQueries)
            {
                AnsiConsole.MarkupLine($"   [cyan]Sending {cmd}[/] - {desc}");
                
                // Send with longer timeout for multi-frame responses
                var response = await SendCommandAsync(cmd, TimeSpan.FromSeconds(10));
                
                var hasData = !string.IsNullOrWhiteSpace(response) &&
                             !response.Contains("NO DATA") &&
                             !response.Contains("ERROR") &&
                             !response.Contains("?") &&
                             response.Length > 10;

                results.Add((cmd, desc, response, hasData));

                if (hasData)
                {
                    AnsiConsole.MarkupLine($"   [green]?[/] Got {response.Length} chars");
                    
                    // Show preview and try to parse
                    var preview = response.Length > 100 ? response[..100] + "..." : response;
                    AnsiConsole.MarkupLine($"   [grey]{preview.EscapeMarkup()}[/]");

                    if (cmd == "2101")
                    {
                        TryParseBmsGroup01(response);
                    }
                    else if (cmd == "2102")
                    {
                        TryParseCellVoltages(response);
                    }
                    else if (cmd == "2104")
                    {
                        TryParseTemperatures(response);
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine($"   [red]?[/] No data: [grey]{response.EscapeMarkup()}[/]");
                }

                await Task.Delay(500);
            }

            // Step 4: Query charger data
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]Step 4: Query Charger data[/]");

            // Reconfigure for charger
            await SendCommandAsync($"ATSH{CHARGER_TXID:X3}", TimeSpan.FromSeconds(2));
            await SendCommandAsync($"ATCRA{CHARGER_RXID:X3}", TimeSpan.FromSeconds(2));
            await SendCommandAsync($"ATFCSH{CHARGER_TXID:X3}", TimeSpan.FromSeconds(2));

            // Query QC count
            var qcResponse = await SendCommandAsync("221203", TimeSpan.FromSeconds(5));
            if (!qcResponse.Contains("NO DATA"))
            {
                AnsiConsole.MarkupLine($"   [green]?[/] QC Count response: [grey]{qcResponse.EscapeMarkup()}[/]");
                TryParseQcCount(qcResponse);
            }

            // Query L1/L2 count
            var l2Response = await SendCommandAsync("221205", TimeSpan.FromSeconds(5));
            if (!l2Response.Contains("NO DATA"))
            {
                AnsiConsole.MarkupLine($"   [green]?[/] L1/L2 Count response: [grey]{l2Response.EscapeMarkup()}[/]");
                TryParseL2Count(l2Response);
            }

            // Query VIN
            var vinResponse = await SendCommandAsync("2181", TimeSpan.FromSeconds(5));
            if (!vinResponse.Contains("NO DATA"))
            {
                AnsiConsole.MarkupLine($"   [green]?[/] VIN response: [grey]{vinResponse.EscapeMarkup()}[/]");
                TryParseVin(vinResponse);
            }

            // Summary
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[green]Results Summary[/]").RuleStyle("grey"));

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Query")
                .AddColumn("Description")
                .AddColumn("Status")
                .AddColumn("Data");

            foreach (var (cmd, desc, response, success) in results)
            {
                table.AddRow(
                    cmd,
                    desc,
                    success ? "[green]Success[/]" : "[red]Failed[/]",
                    success ? $"{response.Length} chars" : "-"
                );
            }

            AnsiConsole.Write(table);

            var successCount = results.Count(r => r.Success);
            if (successCount > 0)
            {
                AnsiConsole.MarkupLine($"[green]?[/] {successCount}/{results.Count} queries successful");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]No BMS data received. Possible causes:[/]");
                AnsiConsole.MarkupLine("  • Vehicle not in READY mode");
                AnsiConsole.MarkupLine("  • Vehicle not charging");
                AnsiConsole.MarkupLine("  • ECUs still asleep (try again after wakeup)");
                AnsiConsole.MarkupLine("  • BLE connection unstable");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Send the OVMS-style wakeup sequence to wake ECUs.
    /// </summary>
    private static async Task SendWakeupSequenceAsync(
        WindowsBleTransport transport,
        Func<string, TimeSpan, Task<string>> sendCommand)
    {
        // The ELM327 can't directly send arbitrary CAN messages without OBD framing,
        // but we can try to wake up ECUs by sending to specific headers
        
        var wakeupAttempts = new (string Cmd, string Desc)[]
        {
            // Try sending to VCM wakeup address
            ($"ATSH{VCM_WAKEUP_ID:X3}", "Set header to VCM wakeup (0x679)"),
            ("00", "Send empty wakeup byte"),
            
            // Try battery heater spoof
            ($"ATSH{BATTERY_HEATER_WAKEUP_ID:X3}", "Set header to battery heater (0x5C0)"),
            ("0000000000000000", "Send 8-byte empty message"),
            
            // Try broadcast
            ($"ATSH{BROADCAST_TXID:X3}", "Set header to broadcast (0x7DF)"),
            ("0100", "Send Mode 01 PID 00 (supported PIDs)"),
        };

        foreach (var (cmd, desc) in wakeupAttempts)
        {
            var resp = await sendCommand(cmd, TimeSpan.FromSeconds(3));
            AnsiConsole.MarkupLine($"   [grey]{cmd}: {resp.EscapeMarkup()}[/]");
            await Task.Delay(300);
        }

        // Small delay for ECUs to wake up
        AnsiConsole.MarkupLine("[grey]   Waiting 2 seconds for ECUs to wake...[/]");
        await Task.Delay(2000);
    }

    /// <summary>
    /// Parse BMS Group 01 response to extract SOC, voltage, current.
    /// Based on OVMS PollReply_Battery() implementation.
    /// Verified against 2017 Nissan Leaf (AZE0 30kWh) data.
    /// </summary>
    private static void TryParseBmsGroup01(string response)
    {
        try
        {
            var bytes = ParseIsoTpResponse(response);

            if (bytes.Count < 2)
            {
                AnsiConsole.MarkupLine("[yellow]   Parse: Not enough bytes for Group 01[/]");
                return;
            }

            // First 2 bytes should be 61 01 (positive response to 21 01)
            if (bytes[0] != 0x61 || bytes[1] != 0x01)
            {
                AnsiConsole.MarkupLine($"[yellow]   Parse: Unexpected response header: {bytes[0]:X2} {bytes[1]:X2}[/]");
            }

            AnsiConsole.MarkupLine($"[cyan]   Parsed BMS Group 01 ({bytes.Count} bytes):[/]");
            
            // Show raw data for debugging
            if (bytes.Count <= 60)
            {
                var hexDump = string.Join("-", bytes.Select(b => b.ToString("X2")));
                AnsiConsole.MarkupLine($"[grey]   Raw: {hexDump}[/]");
            }

            // Data layout verified from 2017 Leaf (AZE0) session:
            // Response: 61-01-FF-FF-F7-70-02-8A-FF-FF-F9-3D-FF-FF-FF-FF-06-0E-17-E8-8F-1F-38-E1-03-92-00-5C-0D-D8-00-06-AC-74-...
            // Offsets from start of response (including 61 01 header):
            //   Bytes 2-5:   Current (signed, big-endian) - FFFFF770 = -2192 ? -4.38A (charging)
            //   Bytes 26-28: Unknown (5C 0D D8)
            //   Bytes 29-32: Capacity in Ah * 10000 (00 06 AC 74 = 437,364 ? 43.74 Ah)
            
            if (bytes.Count >= 33)
            {
                // Battery current (bytes 2-5, signed 32-bit big-endian, divide by 2 for amps)
                uint currentUnsigned = ((uint)bytes[2] << 24) | ((uint)bytes[3] << 16) | ((uint)bytes[4] << 8) | bytes[5];
                int currentRaw = unchecked((int)currentUnsigned);
                
                var currentAmps = currentRaw / 2.0;
                
                if (Math.Abs(currentAmps) < 500 && currentRaw != -1) // -1 = 0xFFFFFFFF = invalid
                {
                    var currentDir = currentAmps > 0 ? "discharging" : (currentAmps < 0 ? "charging" : "idle");
                    AnsiConsole.MarkupLine($"   [green]Battery Current: {Math.Abs(currentAmps):F1}A ({currentDir})[/]");
                }

                // Capacity at bytes 29-32 (verified from AZE0 data: 00 06 AC 74)
                if (bytes.Count >= 33)
                {
                    var capacityRaw = (bytes[29] << 24) | (bytes[30] << 16) | (bytes[31] << 8) | bytes[32];
                    var capacityAh = capacityRaw / 10000.0;
                    
                    if (capacityAh is > 10 and < 100)
                    {
                        AnsiConsole.MarkupLine($"   [green]Battery Capacity: {capacityAh:F2} Ah[/]");
                        
                        // Calculate estimated kWh (360V nominal for 30kWh pack)
                        var kwhEst = capacityAh * 360 / 1000;
                        AnsiConsole.MarkupLine($"   [grey]   (~{kwhEst:F1} kWh at nominal voltage)[/]");
                        
                        // Calculate SOH based on original capacity
                        // 30 kWh pack = ~79 Ah nominal, 24 kWh = ~66 Ah nominal
                        var sohFrom30 = (capacityAh / 79.0) * 100;
                        var sohFrom24 = (capacityAh / 66.0) * 100;
                        AnsiConsole.MarkupLine($"   [grey]   SOH: ~{sohFrom30:F0}% (if 30kWh) / ~{sohFrom24:F0}% (if 24kWh)[/]");
                    }
                }
                
                // HX value - check bytes 24-25 area (5C 0D in sample = 23565)
                // This doesn't match expected HX range, so it may be at different offset
                // Let's scan for reasonable HX values
                for (int i = 20; i < Math.Min(30, bytes.Count - 1); i++)
                {
                    var val = (bytes[i] << 8) | bytes[i + 1];
                    var hxPercent = val / 100.0;
                    if (hxPercent is > 50 and < 110)
                    {
                        AnsiConsole.MarkupLine($"   [grey]   Potential HX at byte {i}: {hxPercent:F1}%[/]");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Parse BMS Group 61 response for State of Health (SOH) data.
    /// Response format from 2017 Leaf: 61-61-0D-D8-19-D7-FF-19-E4-19-D7-03-FF
    /// </summary>
    private static void TryParseBmsGroup61(string response)
    {
        try
        {
            var bytes = ParseIsoTpResponse(response);

            if (bytes.Count < 4)
            {
                AnsiConsole.MarkupLine("[yellow]   Parse: Not enough bytes for Group 61[/]");
                return;
            }

            // Response header should be 61 61 (positive response to 21 61)
            if (bytes[0] == 0x61 && bytes[1] == 0x61)
            {
                AnsiConsole.MarkupLine($"[cyan]   Parsed BMS Group 61 ({bytes.Count} bytes):[/]");
                
                // From 2017 Leaf data: 61-61-0D-D8-19-D7-FF-19-E4-19-D7-03-FF
                // Bytes 2-3: 0DD8 = 3544 
                // This appears to be related to capacity/health
                
                if (bytes.Count >= 4)
                {
                    var val1 = (bytes[2] << 8) | bytes[3];
                    AnsiConsole.MarkupLine($"   [grey]Value at bytes 2-3: {val1} (0x{val1:X4})[/]");
                    
                    // Try various interpretations
                    var asGids = val1; // GIDs remaining
                    var asAh = val1 / 100.0; // Ah (if scaled by 100)
                    
                    if (asGids is > 0 and < 300)
                    {
                        AnsiConsole.MarkupLine($"   [green]GIDs: {asGids}[/]");
                        // Approximate kWh: GIDs * 0.075 for 24kWh, * 0.08 for 30kWh
                        var kwhEst = asGids * 0.08;
                        AnsiConsole.MarkupLine($"   [grey]   (~{kwhEst:F1} kWh estimated)[/]");
                    }
                }
                
                // Look for additional values
                if (bytes.Count >= 6)
                {
                    var val2 = (bytes[4] << 8) | bytes[5];
                    AnsiConsole.MarkupLine($"   [grey]Value at bytes 4-5: {val2} (0x{val2:X4})[/]");
                }
                
                if (bytes.Count >= 10)
                {
                    var val3 = (bytes[7] << 8) | bytes[8];
                    var val4 = (bytes[9] << 8) | bytes[10];
                    AnsiConsole.MarkupLine($"   [grey]Values at 7-8, 9-10: {val3}, {val4}");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]   Unexpected header: {bytes[0]:X2} {bytes[1]:X2}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Scan data for potential BMS values (SOC, capacity, voltage, etc.)
    /// </summary>
    private static void ScanForBmsValues(List<byte> data)
    {
        for (int i = 0; i < data.Count - 2; i++)
        {
            // Look for 3-byte values that could be capacity (30-80 Ah typical)
            if (i + 2 < data.Count)
            {
                var val3 = (data[i] << 16) | (data[i + 1] << 8) | data[i + 2];
                var ah = val3 / 10000.0;
                if (ah is > 30 and < 80)
                {
                    AnsiConsole.MarkupLine($"   [grey]   Byte {i}: {ah:F2} Ah? (raw: {val3:X6})[/]");
                }
            }
            
            // Look for 2-byte values that could be HX (50-100% typical)
            if (i + 1 < data.Count)
            {
                var val2 = (data[i] << 8) | data[i + 1];
                var hx = val2 / 100.0;
                if (hx is > 50 and < 110 && data[i] < 0x30)
                {
                    AnsiConsole.MarkupLine($"   [grey]   Byte {i}: {hx:F1}% HX? (raw: {val2:X4})[/]");
                }
            }
        }
    }

    /// <summary>
    /// Parse cell voltages from Group 02 response.
    /// Verified against 2017 Leaf (AZE0) data - cells read ~0EE0-0EF0 (3808-3824mV).
    /// </summary>
    private static void TryParseCellVoltages(string response)
    {
        try
        {
            var bytes = ParseIsoTpResponse(response);

            // Response header should be 61 02
            int dataStart = 0;
            if (bytes.Count >= 2 && bytes[0] == 0x61 && bytes[1] == 0x02)
            {
                dataStart = 2; // Skip response header
            }

            var cellData = bytes.Skip(dataStart).ToList();
            
            if (cellData.Count < 4)
            {
                AnsiConsole.MarkupLine($"[yellow]   Parse: Only {cellData.Count} bytes for cell voltages[/]");
                return;
            }

            var voltages = new List<double>();
            
            // Parse cell voltages (2 bytes each, big-endian, millivolts)
            // 96 cells in 24/30 kWh Leaf
            for (int i = 0; i + 1 < cellData.Count && voltages.Count < 96; i += 2)
            {
                int millivolt = (cellData[i] << 8) | cellData[i + 1];
                
                // Valid cell voltage range: 2.5V - 4.3V (2500-4300mV)
                if (millivolt >= 2500 && millivolt <= 4300)
                {
                    voltages.Add(millivolt / 1000.0);
                }
                // Also accept values in 0E00-0F00 hex range (3584-3840mV typical)
                else if (millivolt >= 0x0D00 && millivolt <= 0x1000)
                {
                    voltages.Add(millivolt / 1000.0);
                }
            }

            if (voltages.Count > 0)
            {
                var min = voltages.Min();
                var max = voltages.Max();
                var avg = voltages.Average();
                var delta = max - min;
                var total = voltages.Sum();

                AnsiConsole.MarkupLine($"[cyan]   Cell Voltages ({voltages.Count} cells):[/]");
                AnsiConsole.MarkupLine($"   [green]Min: {min:F3}V  Max: {max:F3}V  Avg: {avg:F3}V[/]");
                AnsiConsole.MarkupLine($"   [green]Delta: {delta * 1000:F0}mV  Pack Total: {total:F1}V[/]");
                
                // Cell balance assessment
                if (delta < 0.020) // < 20mV
                    AnsiConsole.MarkupLine($"   [green]Cell balance: Excellent[/]");
                else if (delta < 0.050) // < 50mV
                    AnsiConsole.MarkupLine($"   [yellow]Cell balance: Good[/]");
                else if (delta < 0.100) // < 100mV
                    AnsiConsole.MarkupLine($"   [yellow]Cell balance: Fair[/]");
                else
                    AnsiConsole.MarkupLine($"   [red]Cell balance: Poor (consider balancing)[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]   Could not parse cell voltages from {bytes.Count} bytes[/]");
                
                // Show raw data for debugging
                if (cellData.Count <= 20)
                {
                    AnsiConsole.MarkupLine($"[grey]   Raw: {BitConverter.ToString(cellData.ToArray())}[/]");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Parse temperatures from Group 04 response.
    /// From 2017 Leaf: 61-04-2B-40-20-2B-60-2F-FF-FF-FF-02-D7-00-00-00-FF-FF-FF-FF
    /// </summary>
    private static void TryParseTemperatures(string response)
    {
        try
        {
            var bytes = ParseIsoTpResponse(response);

            if (bytes.Count < 6)
            {
                AnsiConsole.MarkupLine($"[yellow]   Parse: Only {bytes.Count} bytes for temperatures[/]");
                return;
            }

            // Response header should be 61 04
            int dataStart = 0;
            if (bytes[0] == 0x61 && bytes[1] == 0x04)
            {
                dataStart = 2;
                AnsiConsole.MarkupLine($"[cyan]   Parsed BMS Group 04 ({bytes.Count} bytes):[/]");
            }
            
            var data = bytes.Skip(dataStart).ToList();
            
            // From AZE0 data: 2B-40-20-2B-60-2F-FF-FF-FF-02-D7
            // Temperature bytes appear to be at specific offsets
            // Values like 2B (43), 40 (64), 20 (32), 2B (43), 60 (96) need interpretation
            // Likely: raw - 40 = Celsius, or direct Celsius
            
            var temps = new List<(int Index, int Raw, int Celsius)>();
            
            // Check bytes that might be temperatures (0x00-0x50 range suggests 0-80°C)
            int[] possibleTempOffsets = { 0, 1, 2, 3, 4, 5 };
            
            foreach (var offset in possibleTempOffsets)
            {
                if (offset < data.Count)
                {
                    var raw = data[offset];
                    
                    // Try different interpretations
                    if (raw >= 0x10 && raw <= 0x50) // 16-80 range - likely direct Celsius
                    {
                        temps.Add((offset, raw, raw));
                    }
                    else if (raw >= 0x40 && raw <= 0x80) // 64-128 range - might need -40 offset
                    {
                        temps.Add((offset, raw, raw - 40));
                    }
                }
            }

            // Filter to reasonable temperature range (0-60°C typical for battery)
            var validTemps = temps.Where(t => t.Celsius is > 0 and < 60).ToList();
            
            if (validTemps.Count > 0)
            {
                AnsiConsole.MarkupLine($"[cyan]   Pack Temperatures:[/]");
                foreach (var t in validTemps.Take(4))
                {
                    AnsiConsole.MarkupLine($"   [grey]   Sensor {t.Index}: {t.Celsius}°C (raw: 0x{t.Raw:X2})[/]");
                }
                
                var avgTemp = validTemps.Average(t => t.Celsius);
                var minTemp = validTemps.Min(t => t.Celsius);
                var maxTemp = validTemps.Max(t => t.Celsius);
                
                AnsiConsole.MarkupLine($"   [green]Min: {minTemp}°C  Max: {maxTemp}°C  Avg: {avgTemp:F0}°C[/]");
                
                // Temperature spread assessment
                var spread = maxTemp - minTemp;
                if (spread <= 3)
                    AnsiConsole.MarkupLine($"   [green]Temperature balance: Excellent ({spread}°C spread)[/]");
                else if (spread <= 6)
                    AnsiConsole.MarkupLine($"   [yellow]Temperature balance: Good ({spread}°C spread)[/]");
                else
                    AnsiConsole.MarkupLine($"   [red]Temperature balance: Check cooling ({spread}°C spread)[/]");
            }
            else
            {
                // Show raw bytes for debugging
                AnsiConsole.MarkupLine($"[grey]   Raw data: {BitConverter.ToString(data.Take(12).ToArray())}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Parse QC (Quick Charge) count from charger response.
    /// Response format: 79A 05 62 12 03 XX XX
    /// </summary>
    private static void TryParseQcCount(string response)
    {
        try
        {
            var bytes = ParseIsoTpResponse(response);
            
            // Response should be: 62 12 03 XX XX (positive response + PID + count)
            // Or in raw form: 05 62 12 03 XX XX (length + response)
            
            AnsiConsole.MarkupLine($"[grey]   Parsed {bytes.Count} bytes: {string.Join("-", bytes.Select(b => b.ToString("X2")))}[/]");
            
            if (bytes.Count >= 5)
            {
                // Find the response header 62 12 03
                for (int i = 0; i < bytes.Count - 4; i++)
                {
                    if (bytes[i] == 0x62 && bytes[i + 1] == 0x12 && bytes[i + 2] == 0x03)
                    {
                        var count = (bytes[i + 3] << 8) | bytes[i + 4];
                        if (count != 0xFFFF)
                        {
                            AnsiConsole.MarkupLine($"   [green]Quick Charge (DC) Count: {count}[/]");
                            return;
                        }
                    }
                }
            }
            
            // Fallback: last 2 bytes
            if (bytes.Count >= 2)
            {
                var count = (bytes[^2] << 8) | bytes[^1];
                if (count != 0xFFFF && count < 10000)
                {
                    AnsiConsole.MarkupLine($"   [green]Quick Charge Count: {count}[/]");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Parse L1/L2 charge count from charger response.
    /// Response format: 79A 05 62 12 05 XX XX
    /// </summary>
    private static void TryParseL2Count(string response)
    {
        try
        {
            var bytes = ParseIsoTpResponse(response);
            
            AnsiConsole.MarkupLine($"[grey]   Parsed {bytes.Count} bytes: {string.Join("-", bytes.Select(b => b.ToString("X2")))}[/]");
            
            if (bytes.Count >= 5)
            {
                // Find the response header 62 12 05
                for (int i = 0; i < bytes.Count - 4; i++)
                {
                    if (bytes[i] == 0x62 && bytes[i + 1] == 0x12 && bytes[i + 2] == 0x05)
                    {
                        var count = (bytes[i + 3] << 8) | bytes[i + 4];
                        if (count != 0xFFFF)
                        {
                            AnsiConsole.MarkupLine($"   [green]L1/L2 (AC) Charge Count: {count}[/]");
                            return;
                        }
                    }
                }
            }
            
            // Fallback: last 2 bytes
            if (bytes.Count >= 2)
            {
                var count = (bytes[^2] << 8) | bytes[^1];
                if (count != 0xFFFF && count < 20000)
                {
                    AnsiConsole.MarkupLine($"   [green]L1/L2 Charge Count: {count}[/]");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
        }
    }

    /// <summary>
    /// Parse VIN from charger response.
    /// From 2017 Leaf: 79A10156181314E3442\r79A215A304350334843\r79A2233313034303800
    /// Decoded: 61 81 31 4E 34 42 5A 30 43 50 33 48 43 33 31 30 34 30 38 00
    ///        = "1N4BZ0CP3HC310408" (example)
    /// </summary>
    private static void TryParseVin(string response)
    {
        try
        {
            var bytes = ParseIsoTpResponse(response);
            
            AnsiConsole.MarkupLine($"[grey]   Parsed {bytes.Count} bytes[/]");
            
            if (bytes.Count < 5)
            {
                AnsiConsole.MarkupLine("[yellow]   Not enough data for VIN[/]");
                return;
            }
            
            // Show raw for debugging
            if (bytes.Count <= 25)
            {
                AnsiConsole.MarkupLine($"[grey]   Raw: {BitConverter.ToString(bytes.ToArray())}[/]");
            }
            
            // Find response header 61 81 (positive response to 21 81)
            int vinStart = -1;
            for (int i = 0; i < bytes.Count - 1; i++)
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
                
                var vin = new string(vinChars).Trim('\0', ' ');
                
                if (vin.Length >= 10)
                {
                    AnsiConsole.MarkupLine($"   [green]VIN: {vin}[/]");
                    DecodeVin(vin);
                    return;
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
                    
                AnsiConsole.MarkupLine($"   [green]VIN: {rawVin}[/]");
                DecodeVin(rawVin);
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]   Could not extract VIN[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]   Parse error: {ex.Message.EscapeMarkup()}[/]");
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
                'A' => 2010, 'B' => 2011, 'C' => 2012, 'D' => 2013,
                'E' => 2014, 'F' => 2015, 'G' => 2016, 'H' => 2017,
                'J' => 2018, 'K' => 2019, 'L' => 2020, 'M' => 2021,
                'N' => 2022, 'P' => 2023, 'R' => 2024, 'S' => 2025,
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

    /// <summary>
    /// Parse ISO-TP response, handling multi-frame messages.
    /// Handles both spaced and concatenated hex formats from ELM327.
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
        var frameSequence = new List<(int Type, int Seq, byte[] Data)>();
        
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 6) continue;
            
            if (!IsCanIdPrefix(trimmed))
                continue;
                
            var frameHex = trimmed[3..];
            
            if (frameHex.Length < 2) continue;
            
            if (!byte.TryParse(frameHex[..2], System.Globalization.NumberStyles.HexNumber, null, out var frameTypeByte))
                continue;
                
            var frameType = (frameTypeByte & 0xF0) >> 4;
            var frameInfo = frameTypeByte & 0x0F;
            
            byte[] frameData;
            
            switch (frameType)
            {
                case 0:
                    var sfLen = frameInfo;
                    var sfDataHex = frameHex[2..];
                    frameData = ParseHexString(sfDataHex);
                    if (frameData.Length > sfLen)
                        frameData = frameData[..sfLen];
                    frameSequence.Add((0, 0, frameData));
                    break;
                    
                case 1:
                    if (frameHex.Length < 4) continue;
                    if (!byte.TryParse(frameHex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var lenLowByte))
                        continue;
                    var ffDataHex = frameHex[4..];
                    frameData = ParseHexString(ffDataHex);
                    frameSequence.Add((1, 0, frameData));
                    break;
                    
                case 2:
                    var seqNum = frameInfo;
                    var cfDataHex = frameHex[2..];
                    frameData = ParseHexString(cfDataHex);
                    frameSequence.Add((2, seqNum, frameData));
                    break;
                    
                default:
                    frameData = ParseHexString(frameHex);
                    if (frameData.Length > 0)
                        frameSequence.Add((-1, 0, frameData));
                    break;
            }
        }
        
        var firstFrame = frameSequence.FirstOrDefault(f => f.Type == 0 || f.Type == 1);
        if (firstFrame.Data != null)
        {
            bytes.AddRange(firstFrame.Data);
        }
        
        var consecutiveFrames = frameSequence
            .Where(f => f.Type == 2)
            .OrderBy(f => f.Seq)
            .ToList();
            
        foreach (var cf in consecutiveFrames)
        {
            bytes.AddRange(cf.Data);
        }
        
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
        
        return bytes;
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
        for (int i = 0; i + 1 < hex.Length; i += 2)
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
    /// Interactive Leaf command explorer with session recording.
    /// </summary>
    public static async Task RunInteractiveAsync(DevToolsSession session)
    {
        if (!session.IsConnected)
        {
            if (!await session.ConnectAsync())
                return;
        }

        var transport = session.Transport!;
        
        _sessionLog.Clear();
        _isRecording = false;
        var sessionStartTime = DateTime.Now;
        
        void LogToSession(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var entry = $"[{timestamp}] {message}";
            _sessionLog.Add(entry);
            
            if (_isRecording)
            {
                AnsiConsole.MarkupLine($"[grey]?[/] {message.EscapeMarkup()}");
            }
        }

        async Task<string> SendAsync(string cmd, TimeSpan timeout)
        {
            LogToSession($"TX: {cmd}");
            transport.DrainBuffer();
            await transport.WriteAsync(cmd + "\r");
            
            var response = new System.Text.StringBuilder();
            var startTime = DateTime.UtcNow;
            var lastDataTime = DateTime.UtcNow;
            
            while (DateTime.UtcNow - startTime < timeout)
            {
                try
                {
                    var chunk = await transport.ReadUntilAsync(">", TimeSpan.FromMilliseconds(500));
                    response.Append(chunk);
                    
                    if (chunk.Contains(">"))
                        break;
                    
                    lastDataTime = DateTime.UtcNow;
                }
                catch (TimeoutException)
                {
                    if (DateTime.UtcNow - lastDataTime > TimeSpan.FromSeconds(2))
                        break;
                }
            }
            
            var result = response.ToString()
                .Replace(cmd, "")
                .Replace(">", "")
                .Trim();
                
            LogToSession($"RX: {result.Replace("\r", "\\r").Replace("\n", "\\n")}");
            return result;
        }

        async Task<string> SendAndCollectAsync(string cmd, TimeSpan initialWait, TimeSpan collectTime)
        {
            LogToSession($"TX: {cmd}");
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
                    
                    if (chunk.Contains(">"))
                        break;
                }
                catch (TimeoutException)
                {
                    await Task.Delay(100);
                }
            }
            
            var result = response.ToString()
                .Replace(cmd, "")
                .Replace(">", "")
                .Trim();
                
            LogToSession($"RX: {result.Replace("\r", "\\r").Replace("\n", "\\n")}");
            return result;
        }

        AnsiConsole.MarkupLine("[grey]Initializing ELM327...[/]");
        LogToSession("=== Session Started ===");
        LogToSession($"Device: {session.DeviceName ?? session.DeviceAddress}");
        
        await SendAsync("ATZ", TimeSpan.FromSeconds(3));
        await Task.Delay(500);
        await SendAsync("ATE0", TimeSpan.FromSeconds(2));
        await SendAsync("ATH1", TimeSpan.FromSeconds(2));
        await SendAsync("ATS0", TimeSpan.FromSeconds(2));
        await SendAsync("ATSP6", TimeSpan.FromSeconds(3));
        AnsiConsole.MarkupLine("[green]?[/] Ready");
        AnsiConsole.WriteLine();

        while (session.IsConnected)
        {
            // Build menu with recording status
            var recordingStatus = _isRecording ? " [red]? REC[/]" : "";
            var menuChoices = new List<string>
            {
                "Query BMS Group 01 (SOC/Capacity)",
                "Query BMS Group 02 (Cell Voltages)",
                "Query BMS Group 04 (Temperatures)",
                "Query BMS Group 61 (SOH)",
                "Query Charger: QC Count",
                "Query Charger: L1/L2 Count", 
                "Query Charger: VIN",
                "?? Passive CAN Monitor ??",
                "Monitor live battery data (0x1DB/0x5BC)",
                "?? Tools ??",
                "Send wakeup sequence",
                "Send custom Mode 21 query",
                "Send custom Mode 22 query",
                "Send raw AT command",
                "?? Session ??",
                _isRecording ? "Stop recording" : "Start recording",
                "Export session log",
                "Back to main menu"
            };

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[cyan]Nissan Leaf Commands:{recordingStatus}[/]")
                    .PageSize(20)
                    .AddChoices(menuChoices));

            // Skip separator lines
            if (choice.StartsWith("??"))
                continue;

            try
            {
                switch (choice)
                {
                    case "Query BMS Group 01 (SOC/Capacity)":
                        LogToSession("--- BMS Group 01 Query ---");
                        await ConfigureBmsAsync(SendAsync);
                        var g01 = await SendAndCollectAsync("2101", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
                        AnsiConsole.MarkupLine($"[green]Response ({g01.Length} chars):[/]");
                        AnsiConsole.MarkupLine($"[grey]{g01.EscapeMarkup()}[/]");
                        var g01Bytes = ParseIsoTpResponse(g01);
                        AnsiConsole.MarkupLine($"[cyan]Parsed {g01Bytes.Count} bytes[/]");
                        if (g01Bytes.Count > 0)
                        {
                            var hexDump = BitConverter.ToString(g01Bytes.ToArray());
                            AnsiConsole.MarkupLine($"[grey]Hex: {hexDump}[/]");
                            LogToSession($"Parsed: {hexDump}");
                        }
                        TryParseBmsGroup01(g01);
                        break;

                    case "Query BMS Group 02 (Cell Voltages)":
                        LogToSession("--- BMS Group 02 Query ---");
                        await ConfigureBmsAsync(SendAsync);
                        var g02 = await SendAndCollectAsync("2102", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(8));
                        AnsiConsole.MarkupLine($"[green]Response ({g02.Length} chars):[/]");
                        AnsiConsole.MarkupLine($"[grey]{(g02.Length > 200 ? g02[..200] + "..." : g02).EscapeMarkup()}[/]");
                        var g02Bytes = ParseIsoTpResponse(g02);
                        AnsiConsole.MarkupLine($"[cyan]Parsed {g02Bytes.Count} bytes[/]");
                        LogToSession($"Parsed {g02Bytes.Count} bytes");
                        TryParseCellVoltages(g02);
                        break;

                    case "Query BMS Group 04 (Temperatures)":
                        LogToSession("--- BMS Group 04 Query ---");
                        await ConfigureBmsAsync(SendAsync);
                        var g04 = await SendAndCollectAsync("2104", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
                        AnsiConsole.MarkupLine($"[green]Response ({g04.Length} chars):[/]");
                        AnsiConsole.MarkupLine($"[grey]{g04.EscapeMarkup()}[/]");
                        var g04Bytes = ParseIsoTpResponse(g04);
                        AnsiConsole.MarkupLine($"[cyan]Parsed {g04Bytes.Count} bytes[/]");
                        TryParseTemperatures(g04);
                        break;

                    case "Query BMS Group 61 (SOH)":
                        LogToSession("--- BMS Group 61 Query ---");
                        await ConfigureBmsAsync(SendAsync);
                        var g61 = await SendAndCollectAsync("2161", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
                        AnsiConsole.MarkupLine($"[green]Response ({g61.Length} chars):[/]");
                        AnsiConsole.MarkupLine($"[grey]{g61.EscapeMarkup()}[/]");
                        var g61Bytes = ParseIsoTpResponse(g61);
                        AnsiConsole.MarkupLine($"[cyan]Parsed {g61Bytes.Count} bytes[/]");
                        if (g61Bytes.Count > 0)
                        {
                            AnsiConsole.MarkupLine($"[grey]Hex: {BitConverter.ToString(g61Bytes.ToArray())}[/]");
                        }
                        TryParseBmsGroup61(g61);
                        break;

                    case "Query Charger: QC Count":
                        LogToSession("--- Charger QC Count ---");
                        await ConfigureChargerAsync(SendAsync);
                        var qc = await SendAsync("221203", TimeSpan.FromSeconds(5));
                        AnsiConsole.MarkupLine($"[green]Response:[/] {qc.EscapeMarkup()}");
                        TryParseQcCount(qc);
                        break;

                    case "Query Charger: L1/L2 Count":
                        LogToSession("--- Charger L1/L2 Count ---");
                        await ConfigureChargerAsync(SendAsync);
                        var l2 = await SendAsync("221205", TimeSpan.FromSeconds(5));
                        AnsiConsole.MarkupLine($"[green]Response:[/] {l2.EscapeMarkup()}");
                        TryParseL2Count(l2);
                        break;

                    case "Query Charger: VIN":
                        LogToSession("--- Charger VIN ---");
                        await ConfigureChargerAsync(SendAsync);
                        var vin = await SendAndCollectAsync("2181", TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(3));
                        AnsiConsole.MarkupLine($"[green]Response:[/] {vin.EscapeMarkup()}");
                        TryParseVin(vin);
                        break;

                    case "Monitor live battery data (0x1DB/0x5BC)":
                        LogToSession("--- Passive CAN Monitor ---");
                        await RunPassiveCanMonitorAsync(transport, SendAsync, LogToSession);
                        break;

                    case "Send wakeup sequence":
                        LogToSession("--- Wakeup Sequence ---");
                        await SendWakeupSequenceAsync(transport, SendAsync);
                        AnsiConsole.MarkupLine("[green]Wakeup sequence sent[/]");
                        break;

                    case "Send custom Mode 21 query":
                        var group21 = AnsiConsole.Ask<string>("Enter group (e.g., 01, 02, 04, 61):");
                        LogToSession($"--- Custom Mode 21 Group {group21} ---");
                        await ConfigureBmsAsync(SendAsync);
                        var custom21 = await SendAndCollectAsync($"21{group21}", TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));
                        AnsiConsole.MarkupLine($"[green]Response:[/] {custom21.EscapeMarkup()}");
                        var custom21Bytes = ParseIsoTpResponse(custom21);
                        AnsiConsole.MarkupLine($"[cyan]Parsed {custom21Bytes.Count} bytes[/]");
                        if (custom21Bytes.Count > 0)
                        {
                            AnsiConsole.MarkupLine($"[grey]Hex: {BitConverter.ToString(custom21Bytes.ToArray())}[/]");
                        }
                        break;

                    case "Send custom Mode 22 query":
                        var pid22 = AnsiConsole.Ask<string>("Enter PID (e.g., 1203, 1205):");
                        LogToSession($"--- Custom Mode 22 PID {pid22} ---");
                        await ConfigureChargerAsync(SendAsync);
                        var custom22 = await SendAsync($"22{pid22}", TimeSpan.FromSeconds(10));
                        AnsiConsole.MarkupLine($"[green]Response:[/] {custom22.EscapeMarkup()}");
                        break;

                    case "Send raw AT command":
                        var cmd = AnsiConsole.Ask<string>("Enter command:");
                        LogToSession($"--- Raw Command: {cmd} ---");
                        var resp = await SendAsync(cmd, TimeSpan.FromSeconds(5));
                        AnsiConsole.MarkupLine($"[green]Response:[/] {resp.EscapeMarkup()}");
                        break;

                    case "Start recording":
                        _isRecording = true;
                        LogToSession("=== Recording Started ===");
                        AnsiConsole.MarkupLine("[red]? Recording started[/] - All commands will be logged");
                        break;

                    case "Stop recording":
                        _isRecording = false;
                        LogToSession("=== Recording Stopped ===");
                        AnsiConsole.MarkupLine("[grey]Recording stopped[/]");
                        break;

                    case "Export session log":
                        await ExportSessionLogAsync(session, sessionStartTime);
                        break;

                    case "Back to main menu":
                        LogToSession("=== Session Ended ===");
                        return;
                }
            }
            catch (Exception ex)
            {
                LogToSession($"ERROR: {ex.Message}");
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            }

            AnsiConsole.WriteLine();
        }
    }

    private static async Task ExportSessionLogAsync(DevToolsSession session, DateTime sessionStartTime)
    {
        if (_sessionLog.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No session data to export.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Export Session Log[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var logContent = new System.Text.StringBuilder();
        logContent.AppendLine("================================================================================");
        logContent.AppendLine("NISSAN LEAF DIAGNOSTIC SESSION LOG");
        logContent.AppendLine("================================================================================");
        logContent.AppendLine($"Session Start: {sessionStartTime:yyyy-MM-dd HH:mm:ss}");
        logContent.AppendLine($"Session End:   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        logContent.AppendLine($"Device:        {session.DeviceName ?? "Unknown"}");
        logContent.AppendLine($"Address:       {session.DeviceAddress ?? "Unknown"}");
        logContent.AppendLine($"Total Entries: {_sessionLog.Count}");
        logContent.AppendLine("================================================================================");
        logContent.AppendLine();

        foreach (var entry in _sessionLog)
        {
            logContent.AppendLine(entry);
        }

        logContent.AppendLine();
        logContent.AppendLine("================================================================================");
        logContent.AppendLine("END OF LOG");
        logContent.AppendLine("================================================================================");

        // Show preview
        var previewLines = _sessionLog.TakeLast(10).ToList();
        AnsiConsole.MarkupLine($"[grey]Log contains {_sessionLog.Count} entries. Last 10:[/]");
        foreach (var line in previewLines)
        {
            AnsiConsole.MarkupLine($"  [grey]{line.EscapeMarkup()}[/]");
        }
        AnsiConsole.WriteLine();

        // Get filename
        var timestamp = sessionStartTime.ToString("yyyyMMdd_HHmmss");
        var defaultName = $"leaf_session_{timestamp}.txt";

        var fileName = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Save as:[/]")
                .DefaultValue(defaultName));

        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".txt";
        }

        var filePath = Path.Combine(Environment.CurrentDirectory, fileName);

        // Save the file
        await File.WriteAllTextAsync(filePath, logContent.ToString());

        AnsiConsole.MarkupLine($"[green]?[/] Session log saved to: [cyan]{filePath.EscapeMarkup()}[/]");
        AnsiConsole.WriteLine();

        // Offer to open file location
        if (AnsiConsole.Confirm("Open file location?", defaultValue: false))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch
            {
                AnsiConsole.MarkupLine("[yellow]Could not open file location.[/]");
            }
        }
    }

    /// <summary>
    /// Configure ELM327 for BMS communication with flow control.
    /// Includes wakeup attempt for sleeping ECUs.
    /// </summary>
    private static async Task ConfigureBmsAsync(Func<string, TimeSpan, Task<string>> sendAsync)
    {
        // First, try to wake up the BMS by sending to broadcast address
        // This helps when ECUs are sleeping (car OFF but accessory on)
        await sendAsync("ATSH7DF", TimeSpan.FromSeconds(2)); // Broadcast
        await sendAsync("0100", TimeSpan.FromSeconds(2));    // Standard OBD query to wake ECUs
        await Task.Delay(200);
        
        // Now configure for BMS
        await sendAsync($"ATSH{BMS_TXID:X3}", TimeSpan.FromSeconds(2));
        await sendAsync($"ATCRA{BMS_RXID:X3}", TimeSpan.FromSeconds(2));
        await sendAsync($"ATFCSH{BMS_TXID:X3}", TimeSpan.FromSeconds(2));
        await sendAsync("ATFCSD300000", TimeSpan.FromSeconds(2));
        await sendAsync("ATFCSM1", TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Configure ELM327 for Charger communication with flow control.
    /// Includes wakeup attempt for sleeping ECUs.
    /// </summary>
    private static async Task ConfigureChargerAsync(Func<string, TimeSpan, Task<string>> sendAsync)
    {
        // Try to wake up ECUs first
        await sendAsync("ATSH7DF", TimeSpan.FromSeconds(2));
        await sendAsync("0100", TimeSpan.FromSeconds(2));
        await Task.Delay(200);
        
        // Now configure for Charger
        await sendAsync($"ATSH{CHARGER_TXID:X3}", TimeSpan.FromSeconds(2));
        await sendAsync($"ATCRA{CHARGER_RXID:X3}", TimeSpan.FromSeconds(2));
        await sendAsync($"ATFCSH{CHARGER_TXID:X3}", TimeSpan.FromSeconds(2));
        await sendAsync("ATFCSD300000", TimeSpan.FromSeconds(2));
        await sendAsync("ATFCSM1", TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Run passive CAN monitor to capture broadcast frames.
    /// Based on DBC glossary - these frames are broadcast when car is ON.
    /// </summary>
    private static async Task RunPassiveCanMonitorAsync(
        WindowsBleTransport transport,
        Func<string, TimeSpan, Task<string>> sendAsync,
        Action<string> logToSession)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            """
            [yellow]Passive CAN Monitor[/]
            
            This monitors broadcast CAN frames from the battery controller.
            These frames are sent automatically when the car is ON (READY mode).
            
            [cyan]Key Frames (from DBC glossary):[/]
            • 0x1DB: Current, Voltage, Dash SOC (10ms cycle)
            • 0x5BC: GIDs, SOH, Charge Time (100ms cycle)
            • 0x55B: High-resolution SOC (100ms cycle)
            • 0x1DC: Power Limits (10ms cycle)
            
            [red]IMPORTANT: Car MUST be in one of these states:[/]
            • READY mode (foot on brake + press start button)
            • Actively charging (plugged in and charging)
            • Accessory mode may work for some data
            
            [yellow]If car is completely OFF, you will get NO DATA.[/]
            The Nissan Leaf's ECUs sleep when the car is off to save battery.
            
            [grey]Press any key to stop monitoring...[/]
            """)
            .Header("[cyan]Passive Monitor Mode[/]")
            .Border(BoxBorder.Rounded));

        AnsiConsole.WriteLine();

        // Configure ELM327 for passive monitoring
        await sendAsync("ATZ", TimeSpan.FromSeconds(3));
        await Task.Delay(500);
        await sendAsync("ATE0", TimeSpan.FromSeconds(2));
        await sendAsync("ATH1", TimeSpan.FromSeconds(2));
        await sendAsync("ATS0", TimeSpan.FromSeconds(2));
        await sendAsync("ATSP6", TimeSpan.FromSeconds(2));
        
        // Disable auto-formatting to get raw frames
        await sendAsync("ATCAF0", TimeSpan.FromSeconds(2));
        
        // Try to wake up ECUs first by sending a broadcast query
        AnsiConsole.MarkupLine("[grey]Attempting to wake ECUs...[/]");
        await sendAsync("ATSH7DF", TimeSpan.FromSeconds(2));
        var wakeResponse = await sendAsync("0100", TimeSpan.FromSeconds(3));
        
        if (wakeResponse.Contains("NO DATA") || wakeResponse.Contains("UNABLE"))
        {
            AnsiConsole.MarkupLine("[yellow]? No response to wakeup - ECUs may be sleeping[/]");
            AnsiConsole.MarkupLine("[yellow]  Make sure car is in READY mode or charging[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[green]? ECU responded - car appears to be awake[/]");
        }
        
        AnsiConsole.WriteLine();
        
        // Set up filter for battery frames
        // Using no filter (ATAR) to see all traffic, then we'll parse what we want
        await sendAsync("ATAR", TimeSpan.FromSeconds(2)); // Auto Receive address (accept all)
        
        AnsiConsole.MarkupLine("[cyan]Monitoring CAN bus for battery frames...[/]");
        AnsiConsole.MarkupLine("[grey]Looking for: 0x1DB, 0x1DC, 0x55B, 0x5BC, 0x5C0[/]");
        AnsiConsole.WriteLine();

        var frameCount = 0;
        var startTime = DateTime.Now;
        var lastUpdate = DateTime.MinValue;
        var lastFrameTime = DateTime.Now;
        var noDataWarningShown = false;

        // Start monitor mode
        transport.DrainBuffer();
        await transport.WriteAsync("ATMA\r"); // Monitor All

        try
        {
            var cts = new CancellationTokenSource();
            
            // Start key listener
            _ = Task.Run(() =>
            {
                Console.ReadKey(true);
                cts.Cancel();
            });

            var buffer = new System.Text.StringBuilder();
            
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    // Read available data with short timeout
                    var chunk = await transport.ReadUntilAsync("\r", TimeSpan.FromMilliseconds(100));
                    buffer.Append(chunk);
                    
                    // Process complete lines
                    var lines = buffer.ToString().Split('\r', StringSplitOptions.RemoveEmptyEntries);
                    buffer.Clear();
                    
                    // Keep incomplete line in buffer
                    if (!chunk.EndsWith("\r") && lines.Length > 0)
                    {
                        buffer.Append(lines[^1]);
                        lines = lines[..^1];
                    }
                    
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed == ">" || trimmed == "STOPPED" || trimmed == "NO DATA")
                            continue;
                            
                        // Parse CAN frame
                        var frame = ParseCanFrame(trimmed);
                        if (frame != null)
                        {
                            frameCount++;
                            lastFrameTime = DateTime.Now;
                            logToSession($"CAN: {trimmed}");
                            
                            // Only update display every 500ms to avoid flicker
                            if ((DateTime.Now - lastUpdate).TotalMilliseconds > 500)
                            {
                                DisplayFrameData(frame);
                                lastUpdate = DateTime.Now;
                            }
                        }
                    }
                }
                catch (TimeoutException)
                {
                    // Check if we've been waiting too long with no data
                    var timeSinceLastFrame = (DateTime.Now - lastFrameTime).TotalSeconds;
                    
                    if (frameCount == 0 && timeSinceLastFrame > 5 && !noDataWarningShown)
                    {
                        AnsiConsole.WriteLine();
                        AnsiConsole.MarkupLine("[red]??????????????????????????????????????????????????[/]");
                        AnsiConsole.MarkupLine("[red]  NO CAN FRAMES RECEIVED[/]");
                        AnsiConsole.MarkupLine("[yellow]  The car's ECUs appear to be sleeping.[/]");
                        AnsiConsole.MarkupLine("");
                        AnsiConsole.MarkupLine("[white]  To wake the car, do ONE of these:[/]");
                        AnsiConsole.MarkupLine("[cyan]  1. Press brake pedal + Start button (READY mode)[/]");
                        AnsiConsole.MarkupLine("[cyan]  2. Plug in charge cable and start charging[/]");
                        AnsiConsole.MarkupLine("[cyan]  3. Press Start button twice without brake (ACC mode)[/]");
                        AnsiConsole.MarkupLine("[red]??????????????????????????????????????????????????[/]");
                        AnsiConsole.WriteLine();
                        noDataWarningShown = true;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled
        }
        finally
        {
            // Stop monitor mode by sending any character
            await transport.WriteAsync("\r");
            await Task.Delay(200);
            transport.DrainBuffer();
        }

        AnsiConsole.WriteLine();
        if (frameCount > 0)
        {
            AnsiConsole.MarkupLine($"[green]? Monitor stopped. Captured {frameCount} frames.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Monitor stopped. No frames captured - car was likely OFF.[/]");
        }
    }

    /// <summary>
    /// Parse a raw CAN frame from ELM327.
    /// Format: "1DB8010003FF0..." (ID + Data)
    /// </summary>
    private static CanFrame? ParseCanFrame(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length < 5)
            return null;
            
        // Skip non-hex content
        if (!raw.All(c => Uri.IsHexDigit(c)))
            return null;
            
        // First 3 chars = CAN ID (11-bit)
        if (!int.TryParse(raw[..3], System.Globalization.NumberStyles.HexNumber, null, out var canId))
            return null;
            
        // Rest is data (up to 8 bytes = 16 hex chars)
        var dataHex = raw[3..];
        var data = ParseHexString(dataHex);
        
        if (data.Length == 0)
            return null;
            
        return new CanFrame(canId, data);
    }

    /// <summary>
    /// Display parsed CAN frame data.
    /// </summary>
    private static void DisplayFrameData(CanFrame frame)
    {
        switch (frame.Id)
        {
            case CAN_LB_STATUS: // 0x1DB
                ParseAndDisplay1DB(frame.Data);
                break;
                
            case CAN_LB_GIDS: // 0x5BC
                ParseAndDisplay5BC(frame.Data);
                break;
                
            case CAN_LB_SOC: // 0x55B
                ParseAndDisplay55B(frame.Data);
                break;
                
            case CAN_LB_LIMITS: // 0x1DC
                ParseAndDisplay1DC(frame.Data);
                break;
                
            default:
                AnsiConsole.MarkupLine($"[grey]Frame 0x{frame.Id:X3}: {BitConverter.ToString(frame.Data)}[/]");
                break;
        }
    }

    /// <summary>
    /// Parse 0x1DB - Battery Status (10ms cycle)
    /// Contains: Current, Voltage, Dash SOC
    /// </summary>
    private static void ParseAndDisplay1DB(byte[] data)
    {
        if (data.Length < 7) return;
        
        // LB_Current: bits 7-17 (11 bits), big-endian, signed, factor 0.5
        // Start bit 7 = byte 0 bit 7, 11 bits
        int currentRaw = ((data[0] & 0x7F) << 4) | ((data[1] & 0xF0) >> 4);
        // Sign extend 11-bit value
        if ((currentRaw & 0x400) != 0)
            currentRaw |= unchecked((int)0xFFFFF800);
        var currentAmps = currentRaw * 0.5;
        
        // LB_Total_Voltage: bits 23-32 (10 bits), big-endian, unsigned, factor 0.5
        int voltageRaw = ((data[2] & 0x03) << 8) | data[3];
        var voltage = voltageRaw * 0.5;
        
        // LB_Usable_SOC: bits 32-38 (7 bits), byte 4 bits 0-6
        var socDash = data[4] & 0x7F;
        
        var currentDir = currentAmps > 0.5 ? "[red]?[/]" : (currentAmps < -0.5 ? "[green]?[/]" : "[grey]?[/]");
        
        AnsiConsole.MarkupLine(
            $"[cyan]0x1DB[/] | " +
            $"Current: {currentDir} {Math.Abs(currentAmps):F1}A | " +
            $"Voltage: [yellow]{voltage:F1}V[/] | " +
            $"SOC: [green]{socDash}%[/]");
    }

    /// <summary>
    /// Parse 0x5BC - GIDs and SOH (100ms cycle)
    /// Contains: Remaining GIDs, SOH, Charge Time
    /// </summary>
    private static void ParseAndDisplay5BC(byte[] data)
    {
        if (data.Length < 6) return;
        
        // LB_Remain_Capacity_GIDS: bits 7-16 (10 bits), big-endian
        int gids = ((data[0] & 0x01) << 9) | (data[1] << 1) | ((data[2] & 0x80) >> 7);
        
        // LB_Capacity_Deterioration_Rate (SOH): bits 33-39 (7 bits)
        var soh = data[4] & 0x7F;
        
        // Estimate kWh from GIDs (80 Wh per GID for 30kWh pack)
        var kwhRemaining = gids * 0.08;
        
        AnsiConsole.MarkupLine(
            $"[cyan]0x5BC[/] | " +
            $"GIDs: [yellow]{gids}[/] | " +
            $"~{kwhRemaining:F1} kWh | " +
            $"SOH: [green]{soh}%[/]");
    }

    /// <summary>
    /// Parse 0x55B - High-resolution SOC (100ms cycle)
    /// </summary>
    private static void ParseAndDisplay55B(byte[] data)
    {
        if (data.Length < 2) return;
        
        // LB_SOC: bits 7-16 (10 bits), big-endian, 0.1% resolution
        int socRaw = ((data[0] & 0x01) << 9) | (data[1] << 1) | ((data[2] & 0x80) >> 7);
        var socPercent = socRaw * 0.1;
        
        AnsiConsole.MarkupLine(
            $"[cyan]0x55B[/] | " +
            $"SOC (fine): [green]{socPercent:F1}%[/]");
    }

    /// <summary>
    /// Parse 0x1DC - Power Limits (10ms cycle)
    /// </summary>
    private static void ParseAndDisplay1DC(byte[] data)
    {
        if (data.Length < 4) return;
        
        // LB_Discharge_Power_Limit: bits 7-16 (10 bits), factor 0.25 kW
        int dischargeLimitRaw = ((data[0] & 0x01) << 9) | (data[1] << 1) | ((data[2] & 0x80) >> 7);
        var dischargeLimit = dischargeLimitRaw * 0.25;
        
        // LB_Charge_Power_Limit: bits 13-22 (10 bits), factor 0.25 kW
        int chargeLimitRaw = ((data[1] & 0x07) << 7) | ((data[2] & 0xFE) >> 1);
        var chargeLimit = chargeLimitRaw * 0.25;
        
        AnsiConsole.MarkupLine(
            $"[cyan]0x1DC[/] | " +
            $"Discharge Limit: [yellow]{dischargeLimit:F1} kW[/] | " +
            $"Charge Limit: [green]{chargeLimit:F1} kW[/]");
    }

    /// <summary>
    /// Simple CAN frame structure.
    /// </summary>
    private record CanFrame(int Id, byte[] Data);
}
