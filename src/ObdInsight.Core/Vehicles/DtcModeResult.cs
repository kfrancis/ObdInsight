namespace ObdInsight.Core.Vehicles;

/// <summary>Outcome of one diagnostic mode. None implies whole-vehicle coverage.</summary>
public enum DtcReadStatus
{
    Succeeded,
    Partial,
    NoData,
    InvalidResponse,
    QueryFailed,
    Timeout
}

/// <summary>One observed CAN responder; null codes mean its response was invalid.</summary>
public sealed class DtcResponderResult
{
    public DtcResponderResult(int canId, IEnumerable<string>? codes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(canId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(canId, 0x7FF);
        CanId = canId;
        Codes = codes is null ? null : Array.AsReadOnly(codes.Distinct(StringComparer.Ordinal).ToArray());
    }

    public int CanId { get; }
    /// <summary>Validated codes, or null for an invalid/incomplete/negative response.</summary>
    public IReadOnlyList<string>? Codes { get; }
}

/// <summary>
///     Immutable outcome for one mode. Codes exist only for a successful read;
///     partial reads retain usable evidence in Responders. Observed responders
///     are not a census of installed ECUs.
/// </summary>
public sealed class DtcModeResult
{
    private DtcModeResult(DtcReadStatus status, IReadOnlyList<DtcResponderResult> responders)
    {
        Status = status;
        Responders = responders;
        Codes = status == DtcReadStatus.Succeeded
            ? Array.AsReadOnly(responders.SelectMany(r => r.Codes!).Distinct(StringComparer.Ordinal).ToArray())
            : null;
    }

    public DtcReadStatus Status { get; }
    public IReadOnlyList<DtcResponderResult> Responders { get; }
    /// <summary>
    ///     Deduplicated codes only when all observed responses are valid. Empty means
    ///     no codes reported by those responders; null means the mode did not succeed.
    /// </summary>
    public IReadOnlyList<string>? Codes { get; }

    /// <summary>Creates an outcome from responders and any unattributed corruption.</summary>
    public static DtcModeResult FromResponses(IEnumerable<DtcResponderResult> responders, bool hasInvalidData = false)
    {
        ArgumentNullException.ThrowIfNull(responders);
        var copy = responders.ToArray();
        if (copy.Any(r => r is null))
            throw new ArgumentException("Responders cannot contain null.", nameof(responders));
        if (copy.Select(r => r.CanId).Distinct().Count() != copy.Length)
            throw new ArgumentException("Responders must have unique CAN IDs.", nameof(responders));
        var validCount = copy.Count(r => r.Codes is not null);
        var status = validCount > 0
            ? validCount == copy.Length && !hasInvalidData ? DtcReadStatus.Succeeded : DtcReadStatus.Partial
            : copy.Length > 0 || hasInvalidData ? DtcReadStatus.InvalidResponse : DtcReadStatus.NoData;
        return new(status, Array.AsReadOnly(copy));
    }

    /// <summary>Creates a read with no trustworthy response evidence.</summary>
    public static DtcModeResult Failed(DtcReadStatus status)
    {
        if (status is not (DtcReadStatus.NoData or DtcReadStatus.InvalidResponse or DtcReadStatus.QueryFailed or DtcReadStatus.Timeout))
            throw new ArgumentOutOfRangeException(nameof(status));
        return new(status, Array.Empty<DtcResponderResult>());
    }
}
