using ObdInsight.Core.Protocols;

namespace ObdInsight.Telemetry;

public enum ObservationFreshness { Unknown, Fresh, Stale }

/// <summary>
///     A normalized signal value. Exactly one of <see cref="Scalar" />, <see cref="Vector" />,
///     or <see cref="Boolean" /> is set for a known value; all null = unavailable.
/// </summary>
public readonly record struct TelemetryValue(
    decimal? Scalar = null,
    IReadOnlyList<decimal?>? Vector = null,
    bool? Boolean = null)
{
    public ObservationMetadata Observation { get; init; }
    /// <summary>Freshness as assessed at this batch/snapshot's publication, not forever.</summary>
    public ObservationFreshness Freshness { get; init; }
    public TimeSpan? Age { get; init; }

    public TelemetryValue WithObservation(ObservationMetadata observation)
    {
        if (Observation.Quality == ObservationQuality.Invalid)
            return Empty with { Observation = observation with { Quality = ObservationQuality.Invalid } };
        if (observation.Quality is ObservationQuality.Missing or ObservationQuality.Invalid or ObservationQuality.Unsupported or ObservationQuality.TimedOut)
            return Empty with { Observation = observation };
        var quality = IsEmpty ? ObservationQuality.Missing : observation.Quality == ObservationQuality.Partial || Vector?.Any(v => v is null) == true
            ? ObservationQuality.Partial : ObservationQuality.Valid;
        return this with { Observation = observation with { Quality = quality } };
    }
    public static readonly TelemetryValue Empty = new();

    /// <summary>True when no value is present (signal unavailable at sample time).</summary>
    public bool IsEmpty => Scalar is null && Vector is null && Boolean is null;

    public static TelemetryValue FromDouble(double? value) =>
        value is null ? Empty : double.IsFinite(value.Value) && value >= (double)decimal.MinValue && value <= (double)decimal.MaxValue
            ? Convert(value.Value) : Empty with { Observation = new(Quality: ObservationQuality.Invalid) };

    private static TelemetryValue Convert(double value)
    {
        try { return new TelemetryValue(checked((decimal)value)); }
        catch (OverflowException) { return Empty with { Observation = new(Quality: ObservationQuality.Invalid) }; }
    }

    public static TelemetryValue FromBool(bool? value) =>
        value is { } b ? new TelemetryValue(Boolean: b) : Empty;
}

/// <summary>One sampled signal.</summary>
public sealed record TelemetrySample(
    TelemetrySignal Signal,
    TelemetryValue Value,
    DateTimeOffset TimestampUtc,
    CadenceTier Tier);

/// <summary>
///     All samples produced by one tier tick. Contains one sample per subscribed signal of
///     that tier — unavailable signals appear with an empty value, never as omissions.
/// </summary>
public sealed record TelemetrySampleBatch(
    CadenceTier Tier,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<TelemetrySample> Samples)
{
    /// <summary>Connection owner generation, or null for standalone telemetry.</summary>
    public long? ConnectionGeneration { get; init; }
}
