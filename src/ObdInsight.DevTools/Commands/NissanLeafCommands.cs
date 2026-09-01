using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
///     Nissan Leaf diagnostic commands - STUB implementation.
///     TODO: Refactor for new architecture using LeafAze0 vehicle profile.
/// </summary>
public static class NissanLeafCommands
{
    public static Task RunLeafDiagnosticsAsync(DevToolsSession session)
    {
        AnsiConsole.MarkupLine("[yellow]Nissan Leaf diagnostics not yet implemented for new architecture.[/]");
        AnsiConsole.MarkupLine("[grey]TODO: Refactor to use LeafAze0CommandSet and VehicleSession[/]");
        return Task.CompletedTask;
    }

    public static Task RunInteractiveAsync(DevToolsSession session)
    {
        AnsiConsole.MarkupLine("[yellow]Nissan Leaf interactive mode not yet implemented for new architecture.[/]");
        AnsiConsole.MarkupLine("[grey]TODO: Refactor to use VehicleSession.QueryAsync()[/]");
        return Task.CompletedTask;
    }
}
