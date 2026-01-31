using ObdInsight.DevTools.Commands;
using Spectre.Console;

namespace ObdInsight.DevTools;

/// <summary>
/// Binary protocol test - STUB implementation.
/// TODO: Refactor for new architecture.
/// </summary>
public static class BinaryProtocolTest
{
    public static Task RunAsync(DevToolsSession session)
    {
        AnsiConsole.MarkupLine("[yellow]Binary protocol test not yet implemented for new architecture.[/]");
        AnsiConsole.MarkupLine("[grey]TODO: Implement binary OBD command structures and protocol tests[/]");
        return Task.CompletedTask;
    }
}
