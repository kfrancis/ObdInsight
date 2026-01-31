using ObdInsight.Core.Vehicles;
using Spectre.Console;

namespace ObdInsight.Application;

/// <summary>
/// Service for selecting a vehicle profile in the console application.
/// </summary>
public class VehicleSelector
{
    /// <summary>
    /// Prompts the user to select a vehicle make and model.
    /// </summary>
    /// <returns>The selected vehicle profile, or null if cancelled.</returns>
    public IVehicleProfile? SelectVehicle()
    {
        var vehicles = VehicleProfileRegistry.GetAvailableVehicles();

        if (vehicles.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No vehicle profiles available.[/]");
            return null;
        }

        // Create a grouped selection prompt by make
        var vehiclesByMake = vehicles.GroupBy(v => v.Make).OrderBy(g => g.Key).ToList();

        // Build a list of display options
        var options = new List<string>();
        var vehicleMap = new Dictionary<string, (string Make, string Model)>();

        foreach (var makeGroup in vehiclesByMake)
        {
            foreach (var vehicle in makeGroup.OrderBy(v => v.Model))
            {
                var displayText = $"{vehicle.Make} {vehicle.Model}";
                options.Add(displayText);
                vehicleMap[displayText] = (vehicle.Make, vehicle.Model);
            }
        }

        AnsiConsole.WriteLine();
        var selectedOption = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select a vehicle:[/]")
                .PageSize(10)
                .MoreChoicesText("[grey](Move up/down to reveal more options)[/]")
                .AddChoices(options));

        if (string.IsNullOrEmpty(selectedOption) || !vehicleMap.TryGetValue(selectedOption, out var selectedVehicle))
        {
            return null;
        }

        var profile = VehicleProfileRegistry.FindProfile(selectedVehicle.Make, selectedVehicle.Model);
        if (profile == null)
        {
            AnsiConsole.MarkupLine($"[red]Could not find profile for {selectedVehicle.Make} {selectedVehicle.Model}[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] Selected: [cyan]{profile.Make} {profile.Model}[/]");
        AnsiConsole.WriteLine();

        return profile;
    }

    /// <summary>
    /// Prompts the user to select a variant for a vehicle.
    /// </summary>
    /// <param name="profile">The vehicle profile.</param>
    /// <returns>The selected variant, or null if cancelled.</returns>
    public VehicleVariant? SelectVariant(IVehicleProfile profile)
    {
        if (profile.Variants.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No variants available for this vehicle.[/]");
            return null;
        }

        if (profile.Variants.Count == 1)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Using variant: [cyan]{profile.Variants[0].DisplayName}[/]");
            AnsiConsole.WriteLine();
            return profile.Variants[0];
        }

        var variantOptions = profile.Variants
            .Select(v => new { Display = $"{v.DisplayName} ({v.Id.Value})", Variant = v })
            .ToList();

        var selectedVariantDisplay = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]Select a vehicle variant:[/]")
                .PageSize(10)
                .MoreChoicesText("[grey](Move up/down to reveal more options)[/]")
                .AddChoices(variantOptions.Select(v => v.Display)));

        var selectedVariant = variantOptions
            .FirstOrDefault(v => v.Display == selectedVariantDisplay)
            ?.Variant;

        if (selectedVariant != null)
        {
            AnsiConsole.MarkupLine($"[green]✓[/] Selected variant: [cyan]{selectedVariant.DisplayName}[/]");
            AnsiConsole.WriteLine();
        }

        return selectedVariant;
    }
}
