namespace ObdInsight.Core.Protocols;

public enum ObservationSource { Unknown, CanBroadcast, DiagnosticQuery }
public enum ObservationQuality { Unknown, Valid, Partial, Missing, Invalid, Unsupported, TimedOut }

/// <summary>
/// Host receipt/query-completion evidence, not an ECU hardware timestamp. Default means
/// unknown acquisition, never "now". Query/CAN identity is optional provenance.
/// </summary>
public readonly record struct ObservationMetadata(
    DateTimeOffset? ObservedAtUtc = null,
    ObservationSource Source = ObservationSource.Unknown,
    ObservationQuality Quality = ObservationQuality.Unknown,
    int? CanId = null,
    string? Query = null,
    bool IsDerived = false)
{
    private TimeProvider? Clock { get; init; }
    private long ReceiptTimestamp { get; init; }

    public static ObservationMetadata Capture(TimeProvider clock, ObservationSource source,
        int? canId = null, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return new(clock.GetUtcNow(), source, ObservationQuality.Valid, canId, query)
        { Clock = clock, ReceiptTimestamp = clock.GetTimestamp() };
    }

    /// <summary>Uses monotonic elapsed time for in-process evidence from the same clock; otherwise UTC.</summary>
    public TimeSpan? AgeAt(TimeProvider clock, DateTimeOffset publishedAtUtc) =>
        ObservedAtUtc is null ? null : ReferenceEquals(Clock, clock)
            ? clock.GetElapsedTime(ReceiptTimestamp) : publishedAtUtc - ObservedAtUtc.Value;

    /// <summary>A derived value is no newer than its oldest contributing observation.</summary>
    public ObservationMetadata Combine(ObservationMetadata other)
    {
        var quality = Quality == other.Quality ? Quality :
            Quality == ObservationQuality.Valid ? other.Quality :
            other.Quality == ObservationQuality.Valid ? Quality : ObservationQuality.Unknown;
        if (ObservedAtUtc is null || other.ObservedAtUtc is null) return new(Quality: quality, IsDerived: true);
        var oldest = Clock is not null && ReferenceEquals(Clock, other.Clock)
            ? (ReceiptTimestamp <= other.ReceiptTimestamp ? this : other)
            : (ObservedAtUtc <= other.ObservedAtUtc ? this : other);
        return oldest with { IsDerived = true, Quality = quality, Source = Source == other.Source ? Source : ObservationSource.Unknown,
            CanId = CanId == other.CanId ? CanId : null, Query = Query == other.Query ? Query : null };
    }

    // Local clock bookkeeping is not persisted evidence and is excluded from value equality.
    public bool Equals(ObservationMetadata other) => ObservedAtUtc == other.ObservedAtUtc &&
        Source == other.Source && Quality == other.Quality && CanId == other.CanId &&
        Query == other.Query && IsDerived == other.IsDerived;
    public override int GetHashCode() => HashCode.Combine(ObservedAtUtc, Source, Quality, CanId, Query, IsDerived);
}

/// <summary>A value and its acquisition evidence travel together through asynchronous boundaries.</summary>
public sealed record Observed<T>(T Value, ObservationMetadata Observation);
