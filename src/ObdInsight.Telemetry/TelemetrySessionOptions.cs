namespace ObdInsight.Telemetry;

/// <summary>Cadence configuration for a <see cref="TelemetrySession"/>.</summary>
public sealed record TelemetrySessionOptions
{
    /// <summary>High-tier period (default 1.5 s).</summary>
    public TimeSpan HighPeriod { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Medium-tier period (default 7.5 s).</summary>
    public TimeSpan MediumPeriod { get; init; } = TimeSpan.FromSeconds(7.5);

    /// <summary>Low-tier period (default 45 s).</summary>
    public TimeSpan LowPeriod { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Upper bound for a cache-only provider read. Capabilities wait up to ~4 s for a
    /// cold cache; inside the scheduler that wait is cut to this bound and mapped to
    /// empty values so an absent broadcast frame cannot stall a tier (default 250 ms).
    /// </summary>
    public TimeSpan CacheReadTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Per-subscriber batch buffer; slow consumers drop oldest (default 16).</summary>
    public int SubscriberBufferSize { get; init; } = 16;

    /// <summary>Plausibility validation: out-of-range values become null (default on).</summary>
    public bool ValidateRanges { get; init; } = true;

    public TimeSpan PeriodFor(CadenceTier tier) => tier switch
    {
        CadenceTier.High => HighPeriod,
        CadenceTier.Medium => MediumPeriod,
        CadenceTier.Low => LowPeriod,
        _ => HighPeriod,
    };
}

/// <summary>
/// The signal set a consumer wants, each mapped to a cadence tier.
/// </summary>
public sealed class TelemetrySubscription
{
    private readonly Dictionary<TelemetrySignal, CadenceTier> _map;

    public TelemetrySubscription(IReadOnlyDictionary<TelemetrySignal, CadenceTier> map)
    {
        _map = new Dictionary<TelemetrySignal, CadenceTier>(map);
    }

    public IReadOnlyDictionary<TelemetrySignal, CadenceTier> Map => _map;

    public IEnumerable<TelemetrySignal> SignalsFor(CadenceTier tier) =>
        _map.Where(kv => kv.Value == tier).Select(kv => kv.Key);

    /// <summary>
    /// The EvTestDrive default: battery/speed at high cadence, comfort at medium,
    /// counters at low.
    /// </summary>
    public static TelemetrySubscription Default { get; } = new(
        new Dictionary<TelemetrySignal, CadenceTier>
        {
            [TelemetrySignal.StateOfCharge] = CadenceTier.High,
            [TelemetrySignal.PackVoltage] = CadenceTier.High,
            [TelemetrySignal.PackCurrent] = CadenceTier.High,
            [TelemetrySignal.PackPower] = CadenceTier.High,
            [TelemetrySignal.PackTemperature] = CadenceTier.High,
            [TelemetrySignal.VehicleSpeed] = CadenceTier.High,
            [TelemetrySignal.CellVoltageMin] = CadenceTier.High,
            [TelemetrySignal.CellVoltageMax] = CadenceTier.High,
            [TelemetrySignal.CellVoltageAverage] = CadenceTier.High,
            [TelemetrySignal.CellVoltages] = CadenceTier.High,
            [TelemetrySignal.RemainingRange] = CadenceTier.Medium,
            [TelemetrySignal.CabinTemperature] = CadenceTier.Medium,
            [TelemetrySignal.HvacActive] = CadenceTier.Medium,
            [TelemetrySignal.StateOfHealth] = CadenceTier.Low,
            [TelemetrySignal.Odometer] = CadenceTier.Low,
            [TelemetrySignal.ChargeCycleCount] = CadenceTier.Low,
        });
}
