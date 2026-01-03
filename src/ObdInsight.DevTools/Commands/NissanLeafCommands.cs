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
            //   Bytes 22:    SOC (direct percentage 0-100)
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

                // State of Charge (SOC) - byte 22 for AZE0
                if (bytes.Count >= 23)
                {
                    var socRaw = bytes[22];
                    // For 2017 AZE0, this appears to be direct percentage (0-100)
                    if (socRaw is > 0 and <= 100)
                    {
                        AnsiConsole.MarkupLine($"   [green]State of Charge (SOC): {socRaw}%[/]");
                    }
                    else if (socRaw != 0xFF) // 0xFF = invalid/not available
                    {
                        AnsiConsole.MarkupLine($"   [yellow]SOC value out of range: {socRaw}[/]");
                    }
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
    /// Bytes 2-3: Current battery capacity
    /// Bytes 4-5: Original/new battery capacity
    /// SOH% = (Current / Original) × 100
    /// </summary>
    private static void TryParseBmsGroup61(string response)
    {
        try
        {
            var bytes = ParseIsoTpResponse(response);

            if (bytes.Count < 6)
            {
                AnsiConsole.MarkupLine("[yellow]   Parse: Not enough bytes for Group 61[/]");
                return;
            }

            // Response header should be 61 61 (positive response to 21 61)
            if (bytes[0] == 0x61 && bytes[1] == 0x61)
            {
                AnsiConsole.MarkupLine($"[cyan]   Parsed BMS Group 61 ({bytes.Count} bytes):[/]");
                
                // Show hex dump for debugging
                if (bytes.Count <= 20)
                {
                    var hexDump = string.Join("-", bytes.Select(b => b.ToString("X2")));
                    AnsiConsole.MarkupLine($"[grey]   Hex: {hexDump}[/]");
                }
                
                // Bytes 2-3: Current battery capacity
                var currentCapacity = (bytes[2] << 8) | bytes[3];
                AnsiConsole.MarkupLine($"[grey]   Current Capacity: {currentCapacity} (0x{currentCapacity:X4})[/]");
                
                // Bytes 4-5: Original/new battery capacity
                var originalCapacity = (bytes[4] << 8) | bytes[5];
                AnsiConsole.MarkupLine($"[grey]   Original Capacity: {originalCapacity} (0x{originalCapacity:X4})[/]");
                
                // Calculate SOH percentage
                if (originalCapacity > 0)
                {
                    var sohPercent = (currentCapacity / (float)originalCapacity) * 100.0f;
                    AnsiConsole.MarkupLine($"   [green]State of Health (SOH): {sohPercent:F1}%[/]");
                    
                    // Provide context based on SOH value
                    if (sohPercent >= 90)
                        AnsiConsole.MarkupLine("   [green]Battery condition: Excellent[/]");
                    else if (sohPercent >= 80)
                        AnsiConsole.MarkupLine("   [green]Battery condition: Very Good[/]");
                    else if (sohPercent >= 70)
                        AnsiConsole.MarkupLine("   [yellow]Battery condition: Good[/]");
                    else if (sohPercent >= 60)
                        AnsiConsole.MarkupLine("   [yellow]Battery condition: Fair[/]");
                    else if (sohPercent >= 50)
                        AnsiConsole.MarkupLine("   [yellow]Battery condition: Moderate degradation[/]");
                    else
                        AnsiConsole.MarkupLine("   [red]Battery condition: Significant degradation[/]");
                    
                    // Estimate remaining capacity for 30kWh and 24kWh packs
                    // Using capacity ratio to calculate kWh
                    var remaining30kWh = 30.0f * (sohPercent / 100.0f);
                    var remaining24kWh = 24.0f * (sohPercent / 100.0f);
                    AnsiConsole.MarkupLine($"   [grey]   Est. capacity: ~{remaining30kWh:F1} kWh (if 30kWh) / ~{remaining24kWh:F1} kWh (if 24kWh)[/]");
                }
                
                // Additional fields if present
                if (bytes.Count >= 9)
                {
                    var val3 = (bytes[7] << 8) | bytes[8];
                    AnsiConsole.MarkupLine($"   [grey]   Bytes 7-8: {val3} (0x{val3:X4})[/]");
                }
                
                if (bytes.Count >= 11)
                {
                    var val4 = (bytes[9] << 8) | bytes[10];
                    AnsiConsole.MarkupLine($"   [grey]   Bytes 9-10: {val4} (0x{val4:X4})[/]");
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
    /// Format varies by model year - this tries multiple interpretations.
    /// From 2017 Leaf AZE0: Temperatures are direct Celsius values at specific byte positions.
    /// </summary>
    private static void TryParseTemperatures(string response)
    {
        try
        {
            var bytes = ParseIsoTpResponse(response);
            if (bytes.Count < 6)
            {
                AnsiConsole.MarkupLine("[yellow]   Parse: Only {bytes.Count} bytes for temperatures[/]");
                return;
            }

            // Response header should be 61 04
            if (bytes[0] != 0x61 || bytes[1] != 0x04)
            {
                AnsiConsole.MarkupLine($"[yellow]   Parse: Unexpected header {bytes[0]:X2} {bytes[1]:X2}[/]");
                return;
            }

            AnsiConsole.MarkupLine($"[cyan]   Parsed BMS Group 04 ({bytes.Count} bytes):[/]");

            var data = bytes.Skip(2).ToArray();

            // Show full raw data for debugging
            var hexDump = string.Join("-", data.Select(b => b.ToString("X2")));
            AnsiConsole.MarkupLine($"[grey]   Raw: {hexDump}[/]");

            // For AZE0, temperatures appear to be single-byte Celsius values at positions 0, 2, 10, 12
            // This gives us 4 temperature sensors (typical for 30kWh pack)
            var tempPositions = new[] { 0, 2, 10, 12 };
            var temps = new List<(int Sensor, int TempC)>();

            for (int i = 0; i < tempPositions.Length; i++)
            {
                var pos = tempPositions[i];
                if (pos < data.Length)
                {
                    var rawByte = data[pos];

                    // Skip invalid markers (0xFF)
                    if (rawByte == 0xFF) continue;

                    // Interpret as signed byte for negative temps
                    var tempC = (sbyte)rawByte;

                    // Only accept reasonable battery temps (-30°C to 60°C)
                    if (tempC is >= -30 and <= 60)
                    {
                        temps.Add((i + 1, tempC));
                    }
                }
            }

            if (temps.Count >= 2)
            {
                AnsiConsole.MarkupLine($"[green]   Battery Module Temperatures:[/]");
                foreach (var (sensor, tempC) in temps)
                {
                    var (color, status) = tempC switch
                    {
                        < 0 => ("cyan", "COLD"),
                        < 10 => ("blue", "Cool"),
                        < 25 => ("green", "Good"),
                        < 40 => ("yellow", "Warm"),
                        _ => ("red", "HOT!")
                    };
                    AnsiConsole.MarkupLine($"   Module {sensor}: [{color}]{tempC,3}°C  {status}[/]");
                }

                var minTemp = temps.Min(t => t.TempC);
                var maxTemp = temps.Max(t => t.TempC);
                var avgTemp = temps.Average(t => t.TempC);
                var spread = maxTemp - minTemp;

                AnsiConsole.MarkupLine($"   [cyan]Range: {minTemp}°C to {maxTemp}°C  |  Avg: {avgTemp:F1}°C  |  Spread: {spread}°C[/]");

                // Temperature assessment
                if (avgTemp < 0)
                    AnsiConsole.MarkupLine($"   [cyan]Status: Below freezing - reduced regen/power until warmed[/]");
                else if (avgTemp < 10)
                    AnsiConsole.MarkupLine($"   [cyan]Status: Cold - excellent for longevity[/]");
                else if (avgTemp < 25)
                    AnsiConsole.MarkupLine($"   [green]Status: Optimal temperature range[/]");
                else if (avgTemp < 40)
                    AnsiConsole.MarkupLine($"   [yellow]Status: Warm - normal after driving/charging[/]");
                else
                    AnsiConsole.MarkupLine($"   [red]Status: Hot - allow cooling before fast charging[/]");

                // Balance assessment  
                if (spread <= 2)
                    AnsiConsole.MarkupLine($"   [green]Balance: Excellent (uniform cooling)[/]");
                else if (spread <= 5)
                    AnsiConsole.MarkupLine($"   [green]Balance: Good[/]");
                else
                    AnsiConsole.MarkupLine($"   [yellow]Balance: Fair - monitor cooling system[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]   Unable to parse temperature data - format unknown[/]");
                AnsiConsole.MarkupLine($"[grey]   Expected temps at byte positions: 0, 2, 10, 12[/]");
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
                "-- Passive CAN Monitor --",
                "Monitor live battery data (0x1DB/0x5BC)",
                "-- Tools --",
                "Send wakeup sequence",
                "Send custom Mode 21 query",
                "Send custom Mode 22 query",
                "Send raw AT command",
                "-- Session --",
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
        
        [red]IMPORTANT: Car MUST be in READY mode[/]
        (Foot on brake + press Start button until READY light)
        
        [grey]Press any key to stop monitoring...[/]
        """)
            .Header("[cyan]Passive Monitor Mode[/]")
            .Border(BoxBorder.Rounded));

        AnsiConsole.WriteLine();

        // Configure ELM327 for passive monitoring
        await sendAsync("ATZ", TimeSpan.FromSeconds(3));
        await Task.Delay(500);
        await sendAsync("ATE0", TimeSpan.FromSeconds(2));
        await sendAsync("ATH1", TimeSpan.FromSeconds(2));  // Show headers
        await sendAsync("ATS0", TimeSpan.FromSeconds(2));  // No spaces
        await sendAsync("ATSP6", TimeSpan.FromSeconds(2)); // CAN 500kbps 11-bit
        await sendAsync("ATCAF0", TimeSpan.FromSeconds(2)); // Disable auto-formatting

        // REMOVE THE FILTER - accept ALL frames
        AnsiConsole.MarkupLine("[grey]Removing CAN filters to see all traffic...[/]");
        await sendAsync("ATCF000", TimeSpan.FromSeconds(2)); // Filter = 0x000  
        await sendAsync("ATCM000", TimeSpan.FromSeconds(2)); // Mask = 0x000 (accept all)

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[cyan]Starting CAN monitor (looking for battery frames)...[/]");
        AnsiConsole.MarkupLine("[grey]Waiting for CAN traffic...[/]");
        AnsiConsole.WriteLine();

        var frameCount = 0;
        var startTime = DateTime.Now;
        var lastFrameTime = DateTime.Now;
        var displayedWarning = false;

        // Track last values to avoid redundant updates
        var lastValues = new Dictionary<int, string>();

        // Start monitor mode
        transport.DrainBuffer();
        await transport.WriteAsync("ATMA\r"); // Monitor All
        await Task.Delay(200); // Give it time to start

        try
        {
            var cts = new CancellationTokenSource();

            // Start key listener
            _ = Task.Run(() =>
            {
                Console.ReadKey(true);
                cts.Cancel();
            });

            var lineBuffer = new System.Text.StringBuilder();
            // After the frame parsing section, add this:
            var seenFrameIds = new HashSet<int>();


            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    // Read with short timeout to stay responsive
                    var chunk = await transport.ReadUntilAsync("\r", TimeSpan.FromMilliseconds(100));

                    if (string.IsNullOrEmpty(chunk))
                        continue;

                    var trimmed = chunk.Trim();

                    // Handle control messages
                    if (trimmed == ">" || trimmed == "STOPPED")
                        continue;

                    if (trimmed == "BUFFER FULL")
                    {
                        AnsiConsole.MarkupLine("[yellow]⚠ Buffer overflow detected[/]");
                        continue;
                    }

                    if (trimmed == "NO DATA")
                        continue;

                    // Look for hex data (CAN frames look like: 1DB8010003FF0...)
                    if (trimmed.Length >= 5 && trimmed.All(c => char.IsDigit(c) || (c >= 'A' && c <= 'F')))
                    {
                        // Parse CAN ID (first 3 hex chars)
                        if (int.TryParse(trimmed.Substring(0, 3), System.Globalization.NumberStyles.HexNumber, null, out var canId))
                        {
                            // Get data bytes (rest of the string)
                            var dataHex = trimmed.Substring(3);

                            if (dataHex.Length >= 10) // At least 5 bytes
                            {
                                var data = ParseHexString(dataHex);

                                if (data.Length >= 5)
                                {
                                    frameCount++;
                                    lastFrameTime = DateTime.Now;

                                    logToSession($"CAN: 0x{canId:X3} {BitConverter.ToString(data)}");

                                    // Parse known frames
                                    switch (canId)
                                    {
                                        case 0x1DB when data.Length >= 7:
                                            {
                                                var (current, voltage, soc) = Parse1DB(data);
                                                var key1DB = $"{current:F1}|{voltage:F1}|{soc}";
                                                if (!lastValues.TryGetValue(0x1DB, out var last1DB) || last1DB != key1DB)
                                                {
                                                    Display1DB(current, voltage, soc);
                                                    lastValues[0x1DB] = key1DB;
                                                }
                                                break;
                                            }

                                        case 0x1DC when data.Length >= 4:
                                            {
                                                var (dischargeRaw, regenRaw, chargeRaw) = Parse1DC(data);
                                                var key1DC = $"{dischargeRaw:X2}|{regenRaw:X2}|{chargeRaw:X2}";
                                                if (!lastValues.TryGetValue(0x1DC, out var last1DC) || last1DC != key1DC)
                                                {
                                                    Display1DC(dischargeRaw, regenRaw, chargeRaw);
                                                    lastValues[0x1DC] = key1DC;
                                                }
                                                break;
                                            }

                                        case 0x5BC when data.Length >= 6:
                                            {
                                                var (gids, kwh, sohPct, hxPct) = Parse5BC(data);
                                                var key5BC = $"{gids}|{kwh:F2}|{sohPct:F2}|{hxPct:F2}";
                                                if (!lastValues.TryGetValue(0x5BC, out var last5BC) || last5BC != key5BC)
                                                {
                                                    Display5BC(gids, kwh, sohPct, hxPct);
                                                    lastValues[0x5BC] = key5BC;
                                                }
                                                break;
                                            }

                                        case 0x55B when data.Length >= 3:
                                            {
                                                var (socPct, socRaw10Bits, b0b1, b2b3, b6b7) = Parse55B(data);
                                                var key55B = $"{socPct:F1}|{socRaw10Bits}|{b0b1:X4}|{b2b3:X4}|{b6b7:X4}";
                                                if (!lastValues.TryGetValue(0x55B, out var last55B) || last55B != key55B)
                                                {
                                                    Display55B(socPct, socRaw10Bits, b0b1, b2b3, b6b7);
                                                    lastValues[0x55B] = key55B;
                                                }
                                                break;
                                            }

                                        default:
                                            // Show other frames for debugging
                                            AnsiConsole.MarkupLine($"[grey]0x{canId:X3}: {BitConverter.ToString(data).Replace("-", " ")}[/]");
                                            break;
                                    }
                                }
                            }
                        }

                        // Inside the while loop, after parsing a frame:
                        seenFrameIds.Add(canId);
                    }


                }
                catch (TimeoutException)
                {
                    var timeSinceLastFrame = (DateTime.Now - lastFrameTime).TotalSeconds;

                    if (frameCount > 5 && timeSinceLastFrame > 3 && !displayedWarning)
                    {
                        var wantedFrames = new[] { 0x1DB, 0x1DC, 0x55B, 0x5BC };
                        var missingFrames = wantedFrames.Where(id => !seenFrameIds.Contains(id)).ToList();

                        if (missingFrames.Count > 0)
                        {
                            AnsiConsole.WriteLine();
                            AnsiConsole.MarkupLine("[yellow]══════════════════════════════════[/]");
                            AnsiConsole.MarkupLine($"[yellow]⚠ Getting CAN traffic but missing battery frames:[/]");
                            AnsiConsole.MarkupLine($"[red]  Missing: {string.Join(", ", missingFrames.Select(id => $"0x{id:X3}"))}[/]");
                            AnsiConsole.MarkupLine("");
                            AnsiConsole.MarkupLine("[white]Try these:[/]");
                            AnsiConsole.MarkupLine("[cyan]• Press accelerator pedal (put load on battery)[/]");
                            AnsiConsole.MarkupLine("[cyan]• Ensure car is in READY mode (not just ACC)[/]");
                            AnsiConsole.MarkupLine("[cyan]• Turn on headlights or climate control[/]");
                            AnsiConsole.MarkupLine("[yellow]══════════════════════════════════[/]");
                            AnsiConsole.WriteLine();
                            displayedWarning = true;
                        }
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
            // --- 1) Stop ATMA as cleanly as possible ---
            AnsiConsole.MarkupLine("[grey]Stopping monitor...[/]");

            // Any char stops ATMA; send space + CR to be extra explicit.
            await transport.WriteAsync(" \r");
            await Task.Delay(150);

            // Try to read until prompt; ignore timeouts (some clones are flaky here).
            try { await transport.ReadUntilAsync(">", TimeSpan.FromSeconds(3)); } catch { }

            // Drain anything still in the BLE pipe (ATMA can leave trailing frames).
            transport.DrainBuffer();
            await Task.Delay(50);
            transport.DrainBuffer();

            // --- 2) Return ELM to a known diagnostic baseline ---
            // ATD resets many formatting knobs (spaces/headers/linefeeds/etc).
            // ATWS warm-start is often more reliable than ATZ over BLE (keeps link stable).
            // Then we explicitly set the format your diagnostic parser expects.

            // Reset to defaults, warm start, then enforce your preferred diagnostic format.
            await Cmd("ATD", msDelay: 150, timeoutMs: 2000);
            await Cmd("ATWS", msDelay: 800, timeoutMs: 3000);

            // Your diagnostic “known good” config (matches the earlier successful 2101 parse).
            await Cmd("ATE0");     // echo off
            await Cmd("ATL0");     // linefeeds off (prevents extra formatting surprises)
            await Cmd("ATS0");     // spaces off
            await Cmd("ATH1");     // headers on (you parse 7BB...)
            await Cmd("ATCAF1");   // IMPORTANT: auto-format ON (undo ATCAF0)
            await Cmd("ATSP6");    // ISO15765-4 CAN 11-bit 500k
            await Cmd("ATAT2");    // adaptive timing (optional but helps after mode switches)

            // One more drain for safety.
            transport.DrainBuffer();

            AnsiConsole.MarkupLine("[green]✓ ELM327 restored for diagnostics[/]");
        }

        async Task Cmd(string cmd, int msDelay = 120, int timeoutMs = 1500)
        {
            await transport.WriteAsync(cmd + "\r");
            if (msDelay > 0) await Task.Delay(msDelay);
            try { await transport.ReadUntilAsync(">", TimeSpan.FromMilliseconds(timeoutMs)); } catch { }
            transport.DrainBuffer();
        }
    }

    /// <summary>
    /// Parse 0x1DB frame and return values.
    /// </summary>
    private static (double Current, double Voltage, int Soc) Parse1DB(byte[] data)
    {
        // LB_Current: bits 7-17 (11 bits), signed, factor 0.5
        int currentRaw = ((data[0] & 0x7F) << 4) | ((data[1] & 0xF0) >> 4);
        if ((currentRaw & 0x400) != 0) // Sign extend
            currentRaw |= unchecked((int)0xFFFFF800);
        var currentAmps = currentRaw * 0.5;

        // LB_Total_Voltage: bits 23-32 (10 bits), factor 0.5
        int voltageRaw = ((data[2] & 0x03) << 8) | data[3];
        var voltage = voltageRaw * 0.5;

        // LB_Usable_SOC: bits 32-38 (7 bits)
        var soc = data[4] & 0x7F;

        return (currentAmps, voltage, soc);
    }

    /// <summary>
    /// Display 0x1DB data.
    /// </summary>
    private static void Display1DB(double current, double voltage, int soc)
    {
        var (dirColor, dirSymbol) = current switch
        {
            > 0.5 => ("red", "Discharge"),
            < -0.5 => ("green", "Charge   "),
            _ => ("grey", "Idle     ")
        };

        var power = Math.Abs(current * voltage / 1000.0); // kW

        AnsiConsole.MarkupLine(
            $"[cyan]0x1DB[/] | " +
            $"[{dirColor}]{dirSymbol}[/] | " +
            $"{Math.Abs(current),5:F1}A | " +
            $"[yellow]{voltage,5:F1}V[/] | " +
            $"[yellow]{power,5:F2}kW[/] | " +
            $"SOC: [green]{soc,3}%[/]");
    }

    /// <summary>
    /// Parse 0x1DC - Power Limits (exact scaling varies by model/year).
    /// We expose raw bytes so you can correlate with LeafSpy/power bubbles.
    /// </summary>
    private static (byte dischargeLimitRaw, byte regenLimitRaw, byte chargeLimitRaw) Parse1DC(byte[] data)
    {
        var discharge = data[0];
        var regen = data.Length > 1 ? data[1] : (byte)0;
        var charge = data.Length > 2 ? data[2] : (byte)0;
        return (discharge, regen, charge);
    }

    private static void Display1DC(byte dischargeLimitRaw, byte regenLimitRaw, byte chargeLimitRaw)
    {
        AnsiConsole.MarkupLine(
            $"[silver]1DC[/] PowerLimits  Dischg:[yellow]{dischargeLimitRaw:X2}[/]  Regen:[aqua]{regenLimitRaw:X2}[/]  Charge:[green]{chargeLimitRaw:X2}[/]");
    }

    /// <summary>
    /// Parse 0x5BC - GIDs / SOH / Hx (best-effort).
    /// GIDs is commonly packed as 10 bits in b0..b1 (same pattern as 55B).
    /// SOH/Hx placements vary; decode as 0.01% scaled uint16s by default.
    /// </summary>
    private static (int gids, double kwh, double sohPct, double hxPct) Parse5BC(byte[] data)
    {
        // 10-bit packed
        var gidsCandidate = (data[0] << 2) | ((data[1] & 0xC0) >> 6);
        var gids = gidsCandidate == 1023 ? -1 : gidsCandidate;

        // Common approximation: 1 GID ≈ 0.08 kWh (80 Wh)
        var kwh = gids >= 0 ? gids * 0.08 : double.NaN;

        // Best-effort SOH/Hx (adjust offsets if your numbers look wrong)
        var sohRaw = (ushort)((data[2] << 8) | data[3]);
        var hxRaw = (ushort)((data[4] << 8) | data[5]);

        var sohPct = sohRaw / 100.0;
        var hxPct = hxRaw / 100.0;

        return (gids, kwh, sohPct, hxPct);
    }

    private static void Display5BC(int gids, double kwh, double sohPct, double hxPct)
    {
        var gidsText = gids >= 0 ? gids.ToString() : "n/a";
        var kwhText = double.IsNaN(kwh) ? "n/a" : $"{kwh:F2} kWh";
        AnsiConsole.MarkupLine(
            $"[silver]5BC[/] GIDs:[yellow]{gidsText}[/]  Energy:[aqua]{kwhText}[/]  SOH:[green]{sohPct:F2}%[/]  Hx:[green]{hxPct:F2}%[/]");
    }

    /// <summary>
    /// Parse 0x55B - High-res SOC (verification-friendly).
    ///
    /// Primary decode:
    ///   raw10 = (b0<<2) | (b1>>6)
    ///   socPct = raw10 * 0.1
    ///
    /// Also returns some 16-bit raw groupings so you can quickly sanity-check against LeafSpy.
    /// Example from your log: 55B 7D C0 AA 00 E5 00 11 7A :contentReference[oaicite:3]{index=3}
    /// b0b1 = 0x7DC0, b2b3 = 0xAA00, b6b7 = 0x117A
    /// </summary>
    private static (double socPct, int socRaw10Bits, ushort b0b1, ushort b2b3, ushort b6b7) Parse55B(byte[] data)
    {
        var raw10 = (data[0] << 2) | ((data[1] & 0xC0) >> 6);
        var socPct = raw10 * 0.1;

        var b0b1 = (ushort)((data[0] << 8) | data[1]);
        var b2b3 = data.Length >= 4 ? (ushort)((data[2] << 8) | data[3]) : (ushort)0;
        var b6b7 = data.Length >= 8 ? (ushort)((data[6] << 8) | data[7]) : (ushort)0;

        return (socPct, raw10, b0b1, b2b3, b6b7);
    }

    private static void Display55B(double socPct, int socRaw10Bits, ushort b0b1, ushort b2b3, ushort b6b7)
    {
        AnsiConsole.MarkupLine(
            $"[silver]55B[/] SoC:[yellow]{socPct:F1}%[/] (raw10={socRaw10Bits})  raw16(b0b1)=[aqua]{b0b1:X4}[/]  raw16(b2b3)=[aqua]{b2b3:X4}[/]  raw16(b6b7)=[aqua]{b6b7:X4}[/]");
    }

    /// <summary>
    /// Simple CAN frame structure.
    /// </summary>
    private record CanFrame(int Id, byte[] Data);
}
