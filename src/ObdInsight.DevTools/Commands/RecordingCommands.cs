using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
/// Recording commands - STUB implementation.
/// TODO: Refactor recording/replay functionality for new architecture.
/// </summary>
public static class RecordingCommands
{
    public static Task RecordSessionAsync(DevToolsSession session)
    {
        AnsiConsole.MarkupLine("[yellow]Session recording not yet implemented for new architecture.[/]");
        AnsiConsole.MarkupLine("[grey]TODO: Implement recording decorator around ElmFramer[/]");
        return Task.CompletedTask;
    }
}
