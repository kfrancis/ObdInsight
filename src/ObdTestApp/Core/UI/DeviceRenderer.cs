using ObdTestApp.Core.Communication.Bluetooth;
using Spectre.Console;

namespace ObdTestApp.Core.UI;

/// <summary>
/// Handles rendering of BLE device information to the console
/// </summary>
public static class DeviceRenderer
{
    /// <summary>
    /// Renders a table of BLE devices with their properties
    /// </summary>
    /// <param name="devices">List of devices to display</param>
    /// <param name="preferences">Device preferences for favorite/saved indicators</param>
    public static void RenderDeviceTable(IReadOnlyList<BleDeviceInfo> devices, DevicePreferences preferences)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("#")
            .AddColumn("Name")
            .AddColumn("Address")
            .AddColumn("RSSI")
            .AddColumn("Tags");

        for (var i = 0; i < devices.Count; i++)
        {
            var device = devices[i];
            var tags = string.Concat(
                preferences.IsFavorite(device) ? "[yellow]★[/]" : string.Empty,
                preferences.IsSaved(device) ? "[green]✔[/]" : string.Empty);

            if (string.IsNullOrEmpty(tags))
                tags = "-";

            table.AddRow(
                (i + 1).ToString(),
                device.Name.EscapeMarkup(),
                $"[cyan]{device.Address}[/]",
                $"[{ConsoleHelpers.GetRssiColor(device.Rssi)}]{device.Rssi} dBm[/]",
                tags);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Found {devices.Count} devices ([yellow]★[/]=favorite, [green]✔[/]=saved)[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Renders connection information panel
    /// </summary>
    public static void RenderConnectionInfo(BleDeviceInfo device, DateTime sessionStart, TimeSpan commandTimeout)
    {
        var infoPanel = new Panel(new Markup(
            $"[cyan]Device:[/] {device.Name.EscapeMarkup()}\n" +
            $"[cyan]Address:[/] {device.Address.EscapeMarkup()}\n" +
            $"[cyan]RSSI:[/] {device.Rssi} dBm\n" +
            $"[cyan]Debug Logging:[/] Enabled\n" +
            $"[cyan]Command Timeout:[/] {commandTimeout.TotalSeconds}s\n" +
            $"[cyan]Session Start:[/] {sessionStart:HH:mm:ss}"))
        {
            Header = new PanelHeader("[green]Connection Established[/]"),
            Border = BoxBorder.Rounded
        };
        AnsiConsole.Write(infoPanel);
    }

    /// <summary>
    /// Renders session statistics panel
    /// </summary>
    public static void RenderSessionStats(
        TimeSpan totalUptime,
        int monitoringFrameCount,
        int monitoringUniqueCanIds,
        TimeSpan monitoringDuration,
        int successfulQueries,
        int invalidResponseQueries,
        int failedQueries)
    {
        var totalQueries = successfulQueries + failedQueries + invalidResponseQueries;
        var finalSuccessRate = totalQueries > 0 ? (double)successfulQueries / totalQueries * 100 : 0;

        var statsPanel = new Panel(new Markup(
            $"[cyan]Total Uptime:[/] {totalUptime:hh\\:mm\\:ss}\n" +
            $"[cyan]Monitoring Frames:[/] {monitoringFrameCount} ({monitoringUniqueCanIds} unique CAN IDs)\n" +
            $"[cyan]Monitoring Duration:[/] {monitoringDuration.TotalSeconds:F1}s\n" +
            $"[cyan]Successful Queries:[/] {successfulQueries}\n" +
            $"[cyan]Invalid Response Queries:[/] {invalidResponseQueries}\n" +
            $"[cyan]Failed Queries:[/] {failedQueries}\n" +
            $"[cyan]Query Success Rate:[/] {finalSuccessRate:F1}%\n" +
            $"[cyan]Queries/Min:[/] {(totalQueries / totalUptime.TotalMinutes):F1}"))
        {
            Header = new PanelHeader("[yellow]Session Statistics[/]"),
            Border = BoxBorder.Rounded
        };
        
        AnsiConsole.WriteLine();
        AnsiConsole.Write(statsPanel);
    }
}
