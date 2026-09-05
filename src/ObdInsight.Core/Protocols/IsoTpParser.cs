using System.Globalization;

namespace ObdInsight.Core.Protocols;

/// <summary>Why an observed ISO-TP response could not be trusted.</summary>
public enum IsoTpError
{
    None,
    InvalidFrame,
    UnexpectedFrame,
    SequenceMismatch,
    Incomplete
}

/// <summary>An observed classical CAN responder. Failed responses expose no partial payload.</summary>
public sealed class IsoTpResponse
{
    internal IsoTpResponse(int canId, int? expectedLength, IsoTpError error, byte[] payload)
    {
        CanId = canId;
        ExpectedLength = expectedLength;
        Error = error;
        Payload = payload;
    }

    public int CanId { get; }
    public int? ExpectedLength { get; }
    public IsoTpError Error { get; }
    /// <summary>Owned, unpooled payload; empty unless Error is None. Do not mutate its backing storage.</summary>
    public ReadOnlyMemory<byte> Payload { get; }
}

/// <summary>All observed responders plus corruption that could not be assigned to an ECU.</summary>
public sealed class IsoTpParseResult
{
    internal IsoTpParseResult(IsoTpResponse[] responses, bool hasInvalidData)
    {
        Responses = Array.AsReadOnly(responses);
        HasUnattributedErrors = hasInvalidData;
    }

    public IReadOnlyList<IsoTpResponse> Responses { get; }
    /// <summary>Invalid evidence outside individual responder outcomes; also check each response's Error.</summary>
    public bool HasUnattributedErrors { get; }
}

/// <summary>
///     Strict ELM line decoding for classical 11-bit CAN with ISO-TP PCI bytes.
///     Supports spaced lines and fixed-width concatenated full frames. No raw-hex,
///     headerless, odd-nibble, or damaged-frame repair heuristics are applied.
/// </summary>
public static class IsoTpParser
{
    /// <summary>
    ///     Parses independently by responder. Unknown text is invalid evidence; only
    ///     blank lines, prompts, SEARCHING..., and the explicitly supplied command echo
    ///     are ignored. NO DATA alone means no responders, not successful empty data.
    /// </summary>
    public static IsoTpParseResult ParseResponses(IEnumerable<string> lines, string? commandEcho = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var states = new Dictionary<int, AssemblyState>();
        var invalid = false;
        var noData = false;
        foreach (var raw in lines)
        {
            if (raw is null) { invalid = true; continue; }
            foreach (var part in raw.Replace('\r', '\n').Split('\n'))
            {
                var line = part.Replace(" ", "").Trim();
                if (line.Length == 0 || line == ">" || line == "SEARCHING..." || line == commandEcho)
                    continue;
                // A final prompt is a framing delimiter, not payload data.
                if (line.EndsWith('>')) line = line[..^1];
                if (line == "NODATA") { noData = true; continue; }
                if (line.Length > 19 && line.Length % 19 == 0)
                {
                    for (var offset = 0; offset < line.Length; offset += 19)
                        AddFrame(line.AsSpan(offset, 19), states, ref invalid);
                }
                else
                    AddFrame(line, states, ref invalid);
            }
        }

        return new IsoTpParseResult(states.Select(entry => entry.Value.Finish(entry.Key)).ToArray(),
            invalid || (noData && states.Count > 0));
    }

    /// <summary>
    ///     Gets a copied payload only when exactly one responder succeeded and no other
    ///     evidence was invalid. An optional expected responder must be an exact 3-digit
    ///     hex CAN ID, not a wildcard filter. Ambiguity and wrong responders fail closed.
    /// </summary>
    public static bool TryReadPayload(IEnumerable<string> lines, out byte[] payload,
        string? expectedResponder = null, string? commandEcho = null)
    {
        payload = [];
        var result = ParseResponses(lines, commandEcho);
        if (result.HasUnattributedErrors || result.Responses.Count != 1) return false;
        var response = result.Responses[0];
        if (response.Error != IsoTpError.None) return false;
        if (expectedResponder is not null &&
            (expectedResponder.Length != 3 ||
             !int.TryParse(expectedResponder, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) ||
             id != response.CanId))
            return false;
        payload = response.Payload.ToArray();
        return true;
    }

    /// <summary>Compatibility convenience: returns no bytes for invalid or ambiguous responses.</summary>
    public static List<byte> ParseIsoTpResponse(string response) =>
        TryReadPayload([response], out var payload) ? [.. payload] : [];

    /// <summary>Strict raw hex utility: malformed input returns no bytes, never a valid prefix.</summary>
    public static byte[] ParseHexString(string hex)
    {
        if (hex is null || hex.Length % 2 != 0) return [];
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out bytes[i]))
                return [];
        return bytes;
    }

    private static void AddFrame(ReadOnlySpan<char> line, Dictionary<int, AssemblyState> states, ref bool invalid)
    {
        if (line.Length < 3 || !int.TryParse(line[..3], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var id) || id > 0x7FF)
        {
            invalid = true;
            return;
        }
        if (!states.TryGetValue(id, out var state)) states.Add(id, state = new AssemblyState());
        if (line.Length is < 5 or > 19 || (line.Length - 3) % 2 != 0)
        {
            state.Fail(IsoTpError.InvalidFrame);
            return;
        }
        Span<byte> bytes = stackalloc byte[8];
        var length = (line.Length - 3) / 2;
        for (var i = 0; i < length; i++)
        {
            if (!byte.TryParse(line.Slice(3 + i * 2, 2), NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture, out bytes[i]))
            {
                state.Fail(IsoTpError.InvalidFrame);
                return;
            }
        }
        state.Add(bytes[..length]);
    }

    private sealed class AssemblyState
    {
        private readonly List<byte> _data = [];
        private int _expected;
        private int _nextSequence = 1;
        private IsoTpError _error;

        public void Fail(IsoTpError error)
        {
            if (_error == IsoTpError.None) _error = error;
        }

        public void Add(ReadOnlySpan<byte> bytes)
        {
            if (_error != IsoTpError.None) return;
            switch (bytes[0] >> 4)
            {
                case 0 when _expected == 0:
                    _expected = bytes[0] & 15;
                    if (_expected is < 1 or > 7 || bytes.Length < _expected + 1)
                    { Fail(IsoTpError.InvalidFrame); return; }
                    Append(bytes.Slice(1, _expected));
                    break;
                case 1 when _expected == 0 && bytes.Length == 8:
                    _expected = ((bytes[0] & 15) << 8) | bytes[1];
                    if (_expected <= 7) { Fail(IsoTpError.InvalidFrame); return; }
                    Append(bytes[2..]);
                    break;
                case 2 when _expected > 7 && _data.Count < _expected:
                    if ((bytes[0] & 15) != _nextSequence)
                    { Fail(IsoTpError.SequenceMismatch); return; }
                    var count = Math.Min(7, _expected - _data.Count);
                    if (bytes.Length < count + 1) { Fail(IsoTpError.InvalidFrame); return; }
                    Append(bytes.Slice(1, count));
                    _nextSequence = (_nextSequence + 1) & 15;
                    break;
                default:
                    Fail(IsoTpError.UnexpectedFrame);
                    break;
            }
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes) _data.Add(value);
        }

        public IsoTpResponse Finish(int canId)
        {
            if (_error == IsoTpError.None && (_expected == 0 || _data.Count != _expected))
                Fail(IsoTpError.Incomplete);
            return new(canId, _expected == 0 ? null : _expected, _error,
                _error == IsoTpError.None ? _data.ToArray() : []);
        }
    }
}
