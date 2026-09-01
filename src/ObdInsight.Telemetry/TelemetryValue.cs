namespace ObdInsight.Telemetry;

/// <summary>
///     A normalized signal value. Exactly one of <see cref="Scalar" />, <see cref="Vector" />,
///     or <see cref="Boolean" /> is set for a known value; all null = unavailable.
/// </summary>
public readonly record struct TelemetryValue(
    decimal? Scalar = null,
    IReadOnlyList<decimal>? Vector = null,
    bool? Boolean = null)
{
    public static readonly TelemetryValue Empty = new();

    /// <summary>True when no value is present (signal unavailable at sample time).</summary>
    public bool IsEmpty => Scalar is null && Vector is null && Boolean is null;

    public static TelemetryValue FromDouble(double? value) =>
        value is { } v && double.IsFinite(v) ? new TelemetryValue((decimal)v) : Empty;

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
    IReadOnlyList<TelemetrySample> Samples);
