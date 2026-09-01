using ObdInsight.Core.Vehicles;
using Spectre.Console;

namespace ObdInsight.DevTools.Commands;

/// <summary>
///     Diagnostic commands - STUB implementation.
///     TODO: Refactor for new architecture (ElmSession, VehicleProfile, etc.)
/// </summary>
public static class DiagnosticCommands
{
    public static Task RunCommandLoopAsync(DevToolsSession session)
    {
        AnsiConsole.MarkupLine("[yellow]OBD command console not yet implemented for new architecture.[/]");
        AnsiConsole.MarkupLine("[grey]TODO: Refactor to use ElmSession.QueryAsync()[/]");
        return Task.CompletedTask;
    }

    public static Task RunWithVehicleDetectionAsync(DevToolsSession session)
    {
        AnsiConsole.MarkupLine("[yellow]Vehicle detection not yet implemented for new architecture.[/]");
        AnsiConsole.MarkupLine("[grey]TODO: Refactor to use VehicleProfileRegistry and VehicleSession[/]");
        return Task.CompletedTask;
    }

    public static void ListSupportedVehicles()
    {
        AnsiConsole.MarkupLine("[cyan]Supported Vehicles:[/]");
        AnsiConsole.WriteLine();

        try
        {
            var profiles = VehicleProfileRegistry.AllProfiles.ToList();

            if (profiles.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No vehicle profiles found.[/]");
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Make")
                .AddColumn("Model")
                .AddColumn("Variants");

            foreach (var profile in profiles.OrderBy(p => p.Make).ThenBy(p => p.Model))
            {
                var variantCount = profile.Variants.Count;
                table.AddRow(
                    profile.Make,
                    profile.Model,
                    $"{variantCount} variant(s)"
                );
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error listing vehicles: {ex.Message.EscapeMarkup()}[/]");
        }
    }
}
