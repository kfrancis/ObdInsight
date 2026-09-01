using System.Globalization;

namespace ObdInsight.Core.Protocols;

/// <summary>
///     Parses a single ELM327 ATMA/monitor-mode output line into a <see cref="RawCanFrame" />.
///     Handles both 11-bit (3 hex digit) and 29-bit (8 hex digit) CAN IDs, and both the
///     space-separated ("AT S1") and contiguous ("AT S0") byte formats.
/// </summary>
/// <remarks>
///     Pure and side-effect free — does not filter adapter status/error lines (e.g.
///     "BUFFER FULL", "CAN ERROR"); callers reading a live stream should skip those before
///     calling <see cref="TryParse" />.
/// </remarks>
public static class RawCanFrameParser
{
    private const int MaxDataBytes = 8;

    /// <summary>
    ///     Attempts to parse a monitor-mode line such as "7E8 03 41 00 00 00 00 00 00"
    ///     (11-bit, spaced), "7E80341000000000000" (11-bit, contiguous),
    ///     "18DAF110 02 10 03" (29-bit, spaced), or "18DAF11002100301..." (29-bit, contiguous).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> line, out RawCanFrame frame)
    {
        frame = default;
        line = line.Trim();
        if (line.IsEmpty)
        {
            return false;
        }

        var spaceIndex = line.IndexOf(' ');
        if (spaceIndex > 0)
        {
            var idPart = line[..spaceIndex];
            var dataPart = line[(spaceIndex + 1)..];

            if (!TryParseId(idPart, out var canId))
            {
                return false;
            }

            if (!TryParseSpacedBytes(dataPart, out var data))
            {
                return false;
            }

            frame = new RawCanFrame(canId, data);
            return true;
        }

        // Contiguous (no-space) format. ELM327 always pads the ID to a fixed width, so the
        // total hex-digit parity alone identifies it: 11-bit = 3 (odd) + 2N (even) = odd
        // total; 29-bit = 8 (even) + 2N (even) = even total. No ambiguity for 0-8 data bytes.
        if (!IsAllHexDigits(line))
        {
            return false;
        }

        var idLength = line.Length % 2 == 0 ? 8 : 3;
        if (line.Length < idLength)
        {
            return false;
        }

        if (!TryParseId(line[..idLength], out var contiguousCanId))
        {
            return false;
        }

        if (!TryParseContiguousBytes(line[idLength..], out var contiguousData))
        {
            return false;
        }

        frame = new RawCanFrame(contiguousCanId, contiguousData);
        return true;
    }

    private static bool TryParseId(ReadOnlySpan<char> idPart, out int canId)
    {
        canId = 0;

        // ELM327 pads 11-bit IDs to 3 hex digits and 29-bit IDs to 8 hex digits — any other
        // width is not a CAN ID this parser recognizes.
        if (idPart.Length != 3 && idPart.Length != 8)
        {
            return false;
        }

        if (!int.TryParse(idPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        var maxValue = idPart.Length == 3 ? 0x7FF : 0x1FFFFFFF;
        if (value < 0 || value > maxValue)
        {
            return false;
        }

        canId = value;
        return true;
    }

    private static bool TryParseSpacedBytes(ReadOnlySpan<char> dataPart, out ReadOnlyMemory<byte> data)
    {
        data = default;
        var bytes = new List<byte>(MaxDataBytes);

        foreach (var range in dataPart.Split(' '))
        {
            var token = dataPart[range];
            if (token.IsEmpty)
            {
                continue;
            }

            if (token.Length != 2 ||
                !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                return false;
            }

            if (bytes.Count >= MaxDataBytes)
            {
                return false;
            }

            bytes.Add(b);
        }

        data = bytes.ToArray();
        return true;
    }

    private static bool TryParseContiguousBytes(ReadOnlySpan<char> dataPart, out ReadOnlyMemory<byte> data)
    {
        data = default;

        if (dataPart.Length % 2 != 0)
        {
            return false;
        }

        var byteCount = dataPart.Length / 2;
        if (byteCount > MaxDataBytes)
        {
            return false;
        }

        var bytes = new byte[byteCount];
        for (var i = 0; i < byteCount; i++)
        {
            if (!byte.TryParse(dataPart.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                    out bytes[i]))
            {
                return false;
            }
        }

        data = bytes;
        return true;
    }

    private static bool IsAllHexDigits(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
