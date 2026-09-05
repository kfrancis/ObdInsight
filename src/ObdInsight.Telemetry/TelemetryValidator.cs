namespace ObdInsight.Telemetry;

/// <summary>
///     Static plausibility ranges per signal; out-of-range values become
///     <see cref="TelemetryValue.Empty" /> so implausible data can never reach a report.
///     Lives here (not Core) by design: <c>[CanSignal]</c> Min/Max metadata is
///     documentation-only and not reachable at runtime without reflection (iOS AOT hostile).
/// </summary>
internal static class TelemetryValidator
{
    private static readonly Dictionary<TelemetrySignal, (decimal Min, decimal Max)> Ranges = new()
    {
        [TelemetrySignal.StateOfCharge] = (0m, 100m),
        [TelemetrySignal.PackVoltage] = (100m, 500m),
        [TelemetrySignal.PackCurrent] = (-500m, 500m),
        [TelemetrySignal.PackPower] = (-250m, 250m),
        [TelemetrySignal.PackTemperature] = (-40m, 85m),
        [TelemetrySignal.StateOfHealth] = (0m, 150m),
        [TelemetrySignal.CellVoltageMin] = (1.5m, 5m),
        [TelemetrySignal.CellVoltageMax] = (1.5m, 5m),
        [TelemetrySignal.CellVoltageAverage] = (1.5m, 5m),
        [TelemetrySignal.CellVoltages] = (1.5m, 5m),
        [TelemetrySignal.VehicleSpeed] = (0m, 250m),
        [TelemetrySignal.RemainingRange] = (0m, 800m),
        [TelemetrySignal.CabinTemperature] = (-40m, 85m),
        [TelemetrySignal.Odometer] = (0m, 1_500_000m),
        [TelemetrySignal.ChargeCycleCount] = (0m, 100_000m)
    };

    /// <summary>
    ///     Returns the value unchanged when plausible; <see cref="TelemetryValue.Empty" />
    ///     otherwise. Vector entries retain their physical index; invalid entries become
    ///     null rather than being removed or presented as valid measurements.
    /// </summary>
    public static TelemetryValue Validate(TelemetrySignal signal, TelemetryValue value)
    {
        if (value.IsEmpty || !Ranges.TryGetValue(signal, out var range))
        {
            return value;
        }

        if (value.Scalar is { } s)
        {
            return s >= range.Min && s <= range.Max ? value : TelemetryValue.Empty with
            { Observation = value.Observation with { Quality = Core.Protocols.ObservationQuality.Invalid } };
        }

        if (value.Vector is { } vec)
        {
            var validated = vec.Select(element => element is { } v && v >= range.Min && v <= range.Max
                ? element : null).ToArray();
            return value with { Vector = Array.AsReadOnly(validated), Observation = value.Observation with
            { Quality = value.Observation.Quality == Core.Protocols.ObservationQuality.Partial || validated.Any(v => v is null)
                ? Core.Protocols.ObservationQuality.Partial : Core.Protocols.ObservationQuality.Valid } };
        }

        return value;
    }
}
