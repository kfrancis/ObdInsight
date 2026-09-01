using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
///     Report generation commands - STUB implementation.
///     TODO: Refactor for new architecture.
/// </summary>
public static class ReportCommands
{
    public static Task GenerateVehicleSupportReportAsync(DevToolsSession session)
    {
        AnsiConsole.MarkupLine(
            "[yellow]Vehicle support report generation not yet implemented for new architecture.[/]");
        AnsiConsole.MarkupLine("[grey]TODO: Refactor to use ElmSession and VehicleProfile[/]");
        return Task.CompletedTask;
    }
}
