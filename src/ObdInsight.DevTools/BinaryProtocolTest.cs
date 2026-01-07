using ObdInsight.Core.Transports.Ble;
using ObdInsight.DevTools.Commands;
using Spectre.Console;

namespace ObdInsight.DevTools;

/// <summary>
/// Test harness for exploring the Veepeak binary protocol (service 6287).
/// </summary>
public static class BinaryProtocolTest
{
    /// <summary>
    /// Run the binary protocol test using the current session.
    /// </summary>
    public static async Task RunAsync(DevToolsSession session)
    {
        if (string.IsNullOrEmpty(session.DeviceAddress))
        {
            AnsiConsole.MarkupLine("[red]No device selected. Please scan or set a device first.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[cyan]Binary Protocol Explorer[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        AnsiConsole.Write(new Panel(
            """
            [yellow]Binary Protocol Mode[/]
            
            This mode connects using the binary service (6287)
            instead of the ASCII ELM327 service (FFF0).
            
            Binary protocols typically provide:
            [green]• Lower latency[/] - no ASCII encoding overhead
            [green]• Direct CAN access[/] - raw frame communication
            [green]• Faster multi-PID[/] - batch requests possible
            
            [grey]The protocol format varies by adapter - we'll probe to discover it.[/]
            """)
            .Header("[cyan]About Binary Mode[/]")
            .Border(BoxBorder.Rounded));

        AnsiConsole.WriteLine();

        var profile = BleDeviceProfile.VeepeakBinary;
        AnsiConsole.MarkupLine($"[cyan]Using profile:[/] {profile.Name}");
        AnsiConsole.MarkupLine($"[grey]Service:[/] {profile.ServiceUuid}");
        AnsiConsole.MarkupLine($"[grey]Write:[/] {profile.WriteCharacteristicUuid}");
        AnsiConsole.MarkupLine($"[grey]Notify:[/] {profile.NotifyCharacteristicUuid}");
        AnsiConsole.WriteLine();

        // Use session's binary connection
        if (!await session.ConnectBinaryAsync())
        {
            AnsiConsole.MarkupLine("[red]Failed to connect to binary service![/]");
            AnsiConsole.MarkupLine("[yellow]This could mean:[/]");
            AnsiConsole.MarkupLine("  • The adapter doesn't support the binary protocol");
            AnsiConsole.MarkupLine("  • The service UUID is different on your adapter");
            AnsiConsole.MarkupLine("  • The device is out of range or busy");
            return;
        }

        AnsiConsole.MarkupLine("[green]? Connected to binary service![/]");
        AnsiConsole.WriteLine();

        // Run the interactive test loop
        await RunInteractiveLoopAsync(session.BinaryTransport!);
        
        // Disconnect binary transport when done
        if (session.BinaryTransport != null)
        {
            await session.BinaryTransport.DisconnectAsync();
        }
    }

    private static async Task RunInteractiveLoopAsync(WindowsBinaryBleTransport transport)
    {
        while (transport.IsConnected)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]Binary Protocol Test:[/]")
                    .AddChoices(
                        "Run standard probe sequence",
                        "Send custom hex bytes",
                        "Send ASCII command (AT passthrough test)",
                        "Listen for data (5 seconds)",
                        "Show connection diagnostics",
                        "Exit binary test"
                    ));

            try
            {
                switch (choice)
                {
                    case "Run standard probe sequence":
                        await RunStandardProbeAsync(transport);
                        break;

                    case "Send custom hex bytes":
                        await SendCustomHexAsync(transport);
                        break;

                    case "Send ASCII command (AT passthrough test)":
                        await SendAsciiCommandAsync(transport);
                        break;

                    case "Listen for data (5 seconds)":
                        await ListenForDataAsync(transport);
                        break;

                    case "Show connection diagnostics":
                        AnsiConsole.MarkupLine($"[cyan]Diagnostics:[/] {transport.GetDiagnostics()}");
                        break;

                    case "Exit binary test":
                        return;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
            }

            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine("[yellow]Connection lost[/]");
    }

    private static async Task RunStandardProbeAsync(WindowsBinaryBleTransport transport)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[yellow]Standard Probe Sequence[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var results = new List<(string Name, byte[] Sent, byte[]? Received, bool Success)>();

        foreach (var (name, data) in BinaryObdCommands.ProbeCommands)
        {
            AnsiConsole.MarkupLine($"[cyan]Testing: {name}[/]");
            AnsiConsole.MarkupLine($"[grey]TX: {BitConverter.ToString(data)}[/]");

            try
            {
                var response = await transport.SendCommandAsync(data, TimeSpan.FromSeconds(2));
                
                AnsiConsole.MarkupLine($"[green]RX: {BitConverter.ToString(response)}[/]");

                // Try to interpret as ASCII
                if (BinaryObdCommands.TryInterpretAsAscii(response, out var ascii))
                {
                    var escaped = ascii.Replace("\r", "\\r").Replace("\n", "\\n");
                    AnsiConsole.MarkupLine($"[grey]   ASCII: {escaped}[/]");
                }

                results.Add((name, data, response, true));
            }
            catch (TimeoutException)
            {
                AnsiConsole.MarkupLine("[yellow](timeout - no response)[/]");
                results.Add((name, data, null, false));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
                results.Add((name, data, null, false));
            }

            await Task.Delay(300); // Brief delay between probes
        }

        // Summary table
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[green]Probe Results Summary[/]").RuleStyle("grey"));
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Command")
            .AddColumn("TX Bytes")
            .AddColumn("Response")
            .AddColumn("Status");

        foreach (var (name, sent, received, success) in results)
        {
            var rxStr = received != null
                ? (received.Length <= 16
                    ? BitConverter.ToString(received)
                    : BitConverter.ToString(received[..16]) + "...")
                : "-";

            table.AddRow(
                name,
                BitConverter.ToString(sent),
                rxStr,
                success ? "[green]OK[/]" : "[grey]No response[/]"
            );
        }

        AnsiConsole.Write(table);

        var successCount = results.Count(r => r.Success);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Result:[/] {successCount}/{results.Count} commands got responses");

        if (successCount > 0)
        {
            AnsiConsole.MarkupLine("[green]? Binary protocol appears to be active![/]");
            AnsiConsole.MarkupLine("[grey]Analyze the response patterns to determine the command format.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No responses received. Possible reasons:[/]");
            AnsiConsole.MarkupLine("  • Vehicle not in READY mode");
            AnsiConsole.MarkupLine("  • Binary service exists but uses different command format");
            AnsiConsole.MarkupLine("  • Service is for configuration only, not OBD data");
        }
    }

    private static async Task SendCustomHexAsync(WindowsBinaryBleTransport transport)
    {
        var hexInput = AnsiConsole.Ask<string>(
            "[cyan]Enter hex bytes (e.g., 01 00 or 01-00 or 0100):[/]");

        // Parse hex input
        var cleanHex = hexInput
            .Replace(" ", "")
            .Replace("-", "")
            .Replace("0x", "")
            .ToUpperInvariant();

        if (cleanHex.Length % 2 != 0 || !cleanHex.All(c => Uri.IsHexDigit(c)))
        {
            AnsiConsole.MarkupLine("[red]Invalid hex input[/]");
            return;
        }

        var bytes = new byte[cleanHex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(cleanHex.Substring(i * 2, 2), 16);
        }

        AnsiConsole.MarkupLine($"[grey]TX: {BitConverter.ToString(bytes)}[/]");

        try
        {
            var response = await transport.SendCommandAsync(bytes, TimeSpan.FromSeconds(3));
            AnsiConsole.MarkupLine($"[green]RX: {BitConverter.ToString(response)}[/]");

            if (BinaryObdCommands.TryInterpretAsAscii(response, out var ascii))
            {
                var escaped = ascii.Replace("\r", "\\r").Replace("\n", "\\n");
                AnsiConsole.MarkupLine($"[grey]ASCII: {escaped}[/]");
            }
        }
        catch (TimeoutException)
        {
            AnsiConsole.MarkupLine("[yellow](timeout - no response)[/]");
        }
    }

    private static async Task SendAsciiCommandAsync(WindowsBinaryBleTransport transport)
    {
        var cmd = AnsiConsole.Ask<string>(
            "[cyan]Enter ASCII command (e.g., ATI, ATZ, 0100):[/]");

        // Convert to bytes with CR terminator
        var bytes = System.Text.Encoding.ASCII.GetBytes(cmd + "\r");

        AnsiConsole.MarkupLine($"[grey]TX: {BitConverter.ToString(bytes)} ('{cmd}\\r')[/]");

        try
        {
            var response = await transport.SendCommandAsync(bytes, TimeSpan.FromSeconds(3));
            AnsiConsole.MarkupLine($"[green]RX: {BitConverter.ToString(response)}[/]");

            if (BinaryObdCommands.TryInterpretAsAscii(response, out var ascii))
            {
                var escaped = ascii.Replace("\r", "\\r").Replace("\n", "\\n").Replace(">", ">");
                AnsiConsole.MarkupLine($"[cyan]Response: {escaped}[/]");
            }
        }
        catch (TimeoutException)
        {
            AnsiConsole.MarkupLine("[yellow](timeout - no response)[/]");
            AnsiConsole.MarkupLine("[grey]ASCII passthrough may not be supported on binary service[/]");
        }
    }

    private static async Task ListenForDataAsync(WindowsBinaryBleTransport transport)
    {
        AnsiConsole.MarkupLine("[cyan]Listening for unsolicited data for 5 seconds...[/]");
        AnsiConsole.MarkupLine("[grey](Some adapters send periodic status or require no request)[/]");
        AnsiConsole.WriteLine();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedCount = 0;

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var data = await transport.ReadAvailableAsync(TimeSpan.FromSeconds(1), cts.Token);
                    receivedCount++;
                    AnsiConsole.MarkupLine($"[green]Received #{receivedCount}: {BitConverter.ToString(data)}[/]");
                }
                catch (TimeoutException)
                {
                    AnsiConsole.MarkupLine("[grey].[/]");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]Listening complete. Received {receivedCount} packet(s).[/]");
    }
}
