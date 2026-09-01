namespace ObdInsight.Telemetry;

/// <summary>
///     A typed handle on a <see cref="TelemetrySignal" />: it carries the CLR type that signal's
///     values actually have, so <see cref="ITelemetrySession.Stream{T}" /> can hand back real
///     values instead of a <see cref="TelemetryValue" /> the caller has to pick apart.
/// </summary>
/// <remarks>
///     Obtain these from <see cref="Signals" /> — there is deliberately no public constructor, so a
///     handle can never claim a type the underlying signal does not produce.
/// </remarks>
/// <typeparam name="T">The value type this signal produces (decimal, bool, or a decimal list).</typeparam>
public sealed class TelemetrySignal<T>
{
    private readonly Func<TelemetryValue, (bool HasValue, T Value)> _read;

    internal TelemetrySignal(TelemetrySignal signal, Func<TelemetryValue, (bool HasValue, T Value)> read)
    {
        Signal = signal;
        _read = read;
    }

    /// <summary>The underlying signal this handle refers to.</summary>
    public TelemetrySignal Signal { get; }

    /// <summary>Extracts this signal's value, if the sample carries one.</summary>
    internal bool TryRead(TelemetryValue value, out T result)
    {
        var (hasValue, read) = _read(value);
        result = read;
        return hasValue;
    }

    public override string ToString()
    {
        return Signal.ToString();
    }
}

/// <summary>
///     Typed handles for every <see cref="TelemetrySignal" />. Pass one to
///     <see cref="ITelemetrySession.Stream{T}" /> to get a stream of that signal's values:
///     <c>session.Stream(Signals.StateOfCharge)</c> yields <c>TelemetrySample&lt;decimal&gt;</c>.
/// </summary>
public static class Signals
{
    /// <summary>State of charge (%).</summary>
    public static TelemetrySignal<decimal> StateOfCharge { get; } = Scalar(TelemetrySignal.StateOfCharge);

    /// <summary>Battery pack voltage (V).</summary>
    public static TelemetrySignal<decimal> PackVoltage { get; } = Scalar(TelemetrySignal.PackVoltage);

    /// <summary>Battery pack current (A; positive = discharge).</summary>
    public static TelemetrySignal<decimal> PackCurrent { get; } = Scalar(TelemetrySignal.PackCurrent);

    /// <summary>Battery pack power (kW; positive = discharge).</summary>
    public static TelemetrySignal<decimal> PackPower { get; } = Scalar(TelemetrySignal.PackPower);

    /// <summary>Battery pack temperature (°C).</summary>
    public static TelemetrySignal<decimal> PackTemperature { get; } = Scalar(TelemetrySignal.PackTemperature);

    /// <summary>Battery state of health (%).</summary>
    public static TelemetrySignal<decimal> StateOfHealth { get; } = Scalar(TelemetrySignal.StateOfHealth);

    /// <summary>Minimum cell voltage (V).</summary>
    public static TelemetrySignal<decimal> CellVoltageMin { get; } = Scalar(TelemetrySignal.CellVoltageMin);

    /// <summary>Maximum cell voltage (V).</summary>
    public static TelemetrySignal<decimal> CellVoltageMax { get; } = Scalar(TelemetrySignal.CellVoltageMax);

    /// <summary>Average cell voltage (V).</summary>
    public static TelemetrySignal<decimal> CellVoltageAverage { get; } = Scalar(TelemetrySignal.CellVoltageAverage);

    /// <summary>Full per-cell voltage set (V).</summary>
    public static TelemetrySignal<IReadOnlyList<decimal>> CellVoltages { get; } =
        new(TelemetrySignal.CellVoltages, v => (v.Vector is not null, v.Vector!));

    /// <summary>Vehicle speed (km/h).</summary>
    public static TelemetrySignal<decimal> VehicleSpeed { get; } = Scalar(TelemetrySignal.VehicleSpeed);

    /// <summary>Remaining range estimate (km).</summary>
    public static TelemetrySignal<decimal> RemainingRange { get; } = Scalar(TelemetrySignal.RemainingRange);

    /// <summary>Cabin/interior temperature (°C).</summary>
    public static TelemetrySignal<decimal> CabinTemperature { get; } = Scalar(TelemetrySignal.CabinTemperature);

    /// <summary>Whether HVAC/climate control is active.</summary>
    public static TelemetrySignal<bool> HvacActive { get; } =
        new(TelemetrySignal.HvacActive, v => (v.Boolean.HasValue, v.Boolean ?? false));

    /// <summary>Odometer (km). Reserved — no provider yet (roadmap B13).</summary>
    public static TelemetrySignal<decimal> Odometer { get; } = Scalar(TelemetrySignal.Odometer);

    /// <summary>Charge cycle count. Reserved — no provider yet (roadmap B14).</summary>
    public static TelemetrySignal<decimal> ChargeCycleCount { get; } = Scalar(TelemetrySignal.ChargeCycleCount);

    private static TelemetrySignal<decimal> Scalar(TelemetrySignal signal)
    {
        return new TelemetrySignal<decimal>(signal, v => (v.Scalar.HasValue, v.Scalar ?? 0m));
    }
}

/// <summary>
///     One sampled value of a typed signal. The value is always present — samples where the
///     signal was unavailable are skipped by <see cref="ITelemetrySession.Stream{T}" /> rather
///     than emitted empty (consult <see cref="ITelemetrySession.Availability" /> for why a signal
///     is quiet).
/// </summary>
public sealed record TelemetrySample<T>(
    TelemetrySignal Signal,
    T Value,
    DateTimeOffset TimestampUtc,
    CadenceTier Tier);
