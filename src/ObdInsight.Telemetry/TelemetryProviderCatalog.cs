using ObdInsight.Core.Vehicles;
using ObdInsight.Telemetry.Providers;

namespace ObdInsight.Telemetry;

/// <summary>
/// Builds the provider set for a connected vehicle from whichever capabilities its
/// command set registers. Vehicle-agnostic: any vehicle implementing the capability
/// interfaces gets telemetry for free.
/// </summary>
public static class TelemetryProviderCatalog
{
    public static IReadOnlyList<ITelemetryProvider> FromVehicle(IVehicleCommandSet commands)
    {
        var providers = new List<ITelemetryProvider>();

        if (commands.TryGet<IBatteryManagementSystem>(out var bms))
        {
            providers.Add(new BatteryStatusTelemetryProvider(bms));
            providers.Add(new CellVoltagesTelemetryProvider(bms));
        }

        if (commands.TryGet<IAntilockBrakingSystem>(out var abs))
        {
            providers.Add(new SpeedTelemetryProvider(abs));
        }

        if (commands.TryGet<IHvac>(out var hvac))
        {
            providers.Add(new HvacTelemetryProvider(hvac));
        }

        if (commands.TryGet<IVcm>(out var vcm))
        {
            providers.Add(new RangeTelemetryProvider(vcm));
        }

        return providers;
    }
}
