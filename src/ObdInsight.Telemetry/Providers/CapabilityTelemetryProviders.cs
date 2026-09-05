using ObdInsight.Core.Vehicles;

namespace ObdInsight.Telemetry.Providers;

/// <summary>
///     Battery status signals from one BMS UDS exchange (SOC, pack V/A/kW, temp, SoH).
/// </summary>
public sealed class BatteryStatusTelemetryProvider : ITelemetryProvider
{
    private static readonly TelemetrySignal[] Provided =
    [
        TelemetrySignal.StateOfCharge,
        TelemetrySignal.PackVoltage,
        TelemetrySignal.PackCurrent,
        TelemetrySignal.PackPower,
        TelemetrySignal.PackTemperature,
        TelemetrySignal.StateOfHealth
    ];

    private readonly IBatteryManagementSystem _bms;

    public BatteryStatusTelemetryProvider(IBatteryManagementSystem bms) => _bms = bms;

    public IReadOnlyCollection<TelemetrySignal> Signals => Provided;

    public bool IsCacheOnly => false;

    public async ValueTask<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>> ReadAsync(
        IReadOnlySet<TelemetrySignal> requested, CancellationToken ct)
    {
        var status = await _bms.GetStatusAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<TelemetrySignal, TelemetryValue>();
        foreach (var signal in Provided)
        {
            if (!requested.Contains(signal))
            {
                continue;
            }

            result[signal] = signal switch
            {
                TelemetrySignal.StateOfCharge => TelemetryValue.FromDouble(status?.SocPercent),
                TelemetrySignal.PackVoltage => TelemetryValue.FromDouble(status?.VoltageVolts),
                TelemetrySignal.PackCurrent => TelemetryValue.FromDouble(status?.CurrentAmps),
                TelemetrySignal.PackPower => TelemetryValue.FromDouble(status?.PowerWatts / 1000.0),
                TelemetrySignal.PackTemperature => TelemetryValue.FromDouble(status?.TemperatureC),
                TelemetrySignal.StateOfHealth => TelemetryValue.FromDouble(status?.StateOfHealthPercent),
                _ => TelemetryValue.Empty
            };
            result[signal] = result[signal].WithObservation(signal switch
            {
                TelemetrySignal.StateOfCharge => status?.SocObservation ?? default,
                TelemetrySignal.PackVoltage => status?.VoltageObservation ?? default,
                TelemetrySignal.PackCurrent => status?.CurrentObservation ?? default,
                TelemetrySignal.PackPower => status?.PowerObservation ?? default,
                TelemetrySignal.PackTemperature => status?.TemperatureObservation ?? default,
                TelemetrySignal.StateOfHealth => status?.StateOfHealthObservation ?? default,
                _ => default
            });
        }

        return result;
    }
}

/// <summary>Per-cell voltages (one BMS UDS exchange), normalized mV → V.</summary>
public sealed class CellVoltagesTelemetryProvider : ITelemetryProvider
{
    private static readonly TelemetrySignal[] Provided =
    [
        TelemetrySignal.CellVoltages,
        TelemetrySignal.CellVoltageMin,
        TelemetrySignal.CellVoltageMax,
        TelemetrySignal.CellVoltageAverage
    ];

    private readonly IBatteryManagementSystem _bms;

    public CellVoltagesTelemetryProvider(IBatteryManagementSystem bms) => _bms = bms;

    public IReadOnlyCollection<TelemetrySignal> Signals => Provided;

    public bool IsCacheOnly => false;

    public async ValueTask<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>> ReadAsync(
        IReadOnlySet<TelemetrySignal> requested, CancellationToken ct)
    {
        var cells = await _bms.GetCellVoltagesAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<TelemetrySignal, TelemetryValue>();
        foreach (var signal in Provided)
        {
            if (!requested.Contains(signal))
            {
                continue;
            }

            result[signal] = signal switch
            {
                TelemetrySignal.CellVoltages when cells is { CellVoltagesMv.Count: > 0 } =>
                    new TelemetryValue(Vector: cells.CellVoltagesMv.Select(mv => mv / 1000m).ToArray()),
                TelemetrySignal.CellVoltageMin when cells is not null =>
                    new TelemetryValue(cells.MinVoltageMv / 1000m),
                TelemetrySignal.CellVoltageMax when cells is not null =>
                    new TelemetryValue(cells.MaxVoltageMv / 1000m),
                TelemetrySignal.CellVoltageAverage when cells is not null =>
                    new TelemetryValue(cells.AvgVoltageMv / 1000m),
                _ => TelemetryValue.Empty
            };
            result[signal] = result[signal].WithObservation(cells?.Observation ?? default);
        }

        return result;
    }
}

/// <summary>Vehicle speed from the ABS broadcast cache.</summary>
public sealed class SpeedTelemetryProvider : ITelemetryProvider
{
    private readonly IAntilockBrakingSystem _abs;

    public SpeedTelemetryProvider(IAntilockBrakingSystem abs) => _abs = abs;

    public IReadOnlyCollection<TelemetrySignal> Signals { get; } = [TelemetrySignal.VehicleSpeed];

    public bool IsCacheOnly => true;

    public async ValueTask<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>> ReadAsync(
        IReadOnlySet<TelemetrySignal> requested, CancellationToken ct)
    {
        var status = await _abs.GetStatusAsync(ct);
        return new Dictionary<TelemetrySignal, TelemetryValue>
        {
            [TelemetrySignal.VehicleSpeed] = TelemetryValue.FromDouble(status.VehicleSpeedKmh).WithObservation(status.VehicleSpeedObservation)
        };
    }
}

/// <summary>Cabin temperature + HVAC state from the HVAC broadcast cache.</summary>
public sealed class HvacTelemetryProvider : ITelemetryProvider
{
    private static readonly TelemetrySignal[] Provided =
    [
        TelemetrySignal.CabinTemperature,
        TelemetrySignal.HvacActive
    ];

    private readonly IHvac _hvac;

    public HvacTelemetryProvider(IHvac hvac) => _hvac = hvac;

    public IReadOnlyCollection<TelemetrySignal> Signals => Provided;

    public bool IsCacheOnly => true;

    public async ValueTask<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>> ReadAsync(
        IReadOnlySet<TelemetrySignal> requested, CancellationToken ct)
    {
        var status = await _hvac.GetStatusAsync(ct);
        var result = new Dictionary<TelemetrySignal, TelemetryValue>();
        if (requested.Contains(TelemetrySignal.CabinTemperature))
        {
            result[TelemetrySignal.CabinTemperature] =
                TelemetryValue.FromDouble(status.InteriorIntakeTempC).WithObservation(status.CabinTemperatureObservation);
        }

        if (requested.Contains(TelemetrySignal.HvacActive))
        {
            result[TelemetrySignal.HvacActive] =
                TelemetryValue.FromBool(status.ClimateControlOn || status.AcOn).WithObservation(status.ClimateStateObservation);
        }

        return result;
    }
}

/// <summary>Remaining range from the VCM broadcast cache (0x5A9 on the Leaf).</summary>
public sealed class RangeTelemetryProvider : ITelemetryProvider
{
    private readonly IVcm _vcm;

    public RangeTelemetryProvider(IVcm vcm) => _vcm = vcm;

    public IReadOnlyCollection<TelemetrySignal> Signals { get; } = [TelemetrySignal.RemainingRange];

    public bool IsCacheOnly => true;

    public async ValueTask<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>> ReadAsync(
        IReadOnlySet<TelemetrySignal> requested, CancellationToken ct)
    {
        var status = await _vcm.GetStatusAsync(ct);
        return new Dictionary<TelemetrySignal, TelemetryValue>
        {
            [TelemetrySignal.RemainingRange] = TelemetryValue.FromDouble(status.RangeKm).WithObservation(status.RangeObservation)
        };
    }
}
