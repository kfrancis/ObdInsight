using ObdInsight.Core.Communication.Bluetooth;
using Spectre.Console;

namespace ObdInsight.UI;

public static class DeviceRenderer
{
    public static void RenderConnectionInfo(BleDeviceInfo device, DateTime start, TimeSpan timeout)
    {
        var panel =
            new Panel(
                $"[grey]Device:[/] [cyan]{device.Name}[/] ([grey]{device.Address}[/])\n[grey]Start:[/] {start:HH:mm:ss} UTC\n[grey]Timeout:[/] {timeout.TotalSeconds:F0}s")
            {
                Header = new PanelHeader("[green]Connection Info[/]"), Border = BoxBorder.Rounded
            };
        AnsiConsole.Write(panel);
    }

    public static void RenderDeviceTable(IReadOnlyList<BleDeviceInfo> devices, DevicePreferences preferences)
    {
        var table = new Table().Border(TableBorder.Rounded).Title("[bold cyan]BLE Devices[/]");
        table.AddColumn("#");
        table.AddColumn("Name");
        table.AddColumn("Address");
        table.AddColumn("RSSI");
        table.AddColumn("Favorite");

        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var isFav = preferences.IsFavorite(d) ? "[yellow]★[/]" : "";
            table.AddRow((i + 1).ToString(), d.Name, d.Address, d.Rssi.ToString(), isFav);
        }

        AnsiConsole.Write(table);
    }

    public static void RenderSessionStats(
        TimeSpan uptime,
        int monitorFrames,
        int uniqueCanIds,
        TimeSpan monitorDuration,
        int successfulQueries,
        int invalidResponseQueries,
        int failedQueries)
    {
        var totalQueries = successfulQueries + invalidResponseQueries + failedQueries;
        var table = new Table().Border(TableBorder.Rounded).Title("[bold cyan]Session Stats[/]");
        table.AddColumn("Metric");
        table.AddColumn("Value");

        table.AddRow("Uptime", uptime.ToString());
        table.AddRow("Monitor Frames", monitorFrames.ToString());
        table.AddRow("Unique CAN IDs", uniqueCanIds.ToString());
        table.AddRow("Monitor Duration", monitorDuration.ToString());
        table.AddRow("Queries (Success/Invalid/Failed)",
            $"{successfulQueries}/{invalidResponseQueries}/{failedQueries}");
        table.AddRow("Success Rate", totalQueries > 0 ? $"{(double)successfulQueries / totalQueries:P0}" : "N/A");

        AnsiConsole.Write(table);
    }
}
