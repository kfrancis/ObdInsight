using System.Globalization;

namespace ObdInsight.Core.Protocols;

/// <summary>
///     Utilities for parsing ISO-TP (ISO 15765-2) responses from ELM327
/// </summary>
public static class IsoTpParser
{
    /// <summary>Width of one frame in unspaced ELM327 output: 3-char CAN ID + 8 data bytes.</summary>
    private const int UnspacedFrameLength = 19;

    /// <summary>
    ///     Parse ISO-TP response, handling multi-frame messages.
    ///     Handles both spaced and concatenated hex formats from ELM327.
    ///     Also handles frames concatenated together on a single line (e.g., "7BB25...7BB26...").
    /// </summary>
    public static List<byte> ParseIsoTpResponse(string response)
    {
        var bytes = new List<byte>();

        if (string.IsNullOrWhiteSpace(response))
        {
            return bytes;
        }

        var cleaned = response
            .Replace("\r", "\n")
            .Replace(">", "")
            .Trim();

        var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Adapters may run several frames together on one line. ELM327 output with spaces off has
        // fixed geometry - a 3-char CAN ID plus 8 data bytes, 19 chars per frame - and every frame
        // of a response carries the same responder ID, so split on that geometry. Scanning for
        // anything CAN-ID-shaped instead corrupts payloads: response hex routinely spells a valid
        // looking ID (e.g. "7BB27676BE7F10D46D8" contains "7F1" followed by '0'). See
        // IsoTpParserPropertyTests.
        var allFrames = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 6)
            {
                continue;
            }

            if (!IsCanIdPrefixForIsoTp(trimmed))
            {
                continue;
            }

            allFrames.AddRange(SplitRunTogetherFrames(trimmed));
        }

        var frameSequence = new List<(int Type, int Seq, byte[] Data, int TotalLen)>();
        var expectedTotalLength = 0;

        foreach (var frame in allFrames)
        {
            if (frame.Length < 6)
            {
                continue;
            }

            if (!IsCanIdPrefixForIsoTp(frame))
            {
                continue;
            }

            var frameHex = frame[3..];

            if (frameHex.Length < 2)
            {
                continue;
            }

            if (!byte.TryParse(frameHex[..2], NumberStyles.HexNumber, null, out var frameTypeByte))
            {
                continue;
            }

            var frameType = (frameTypeByte & 0xF0) >> 4;
            var frameInfo = frameTypeByte & 0x0F;

            byte[] frameData;

            switch (frameType)
            {
                case 0: // Single Frame
                    var sfLen = frameInfo;
                    var sfDataHex = frameHex[2..];
                    frameData = ParseHexString(sfDataHex);
                    if (frameData.Length > sfLen)
                    {
                        frameData = frameData[..sfLen];
                    }

                    frameSequence.Add((0, 0, frameData, sfLen));
                    break;

                case 1: // First Frame
                    if (frameHex.Length < 4)
                    {
                        continue;
                    }

                    if (!byte.TryParse(frameHex[2..4], NumberStyles.HexNumber, null, out var lenLowByte))
                    {
                        continue;
                    }

                    expectedTotalLength = (frameInfo << 8) | lenLowByte;
                    var ffDataHex = frameHex[4..];
                    frameData = ParseHexString(ffDataHex);
                    frameSequence.Add((1, 0, frameData, expectedTotalLength));
                    break;

                case 2: // Consecutive Frame
                    var seqNum = frameInfo;
                    var cfDataHex = frameHex[2..];
                    frameData = ParseHexString(cfDataHex);
                    frameSequence.Add((2, seqNum, frameData, 0));
                    break;

                default:
                    frameData = ParseHexString(frameHex);
                    if (frameData.Length > 0)
                    {
                        frameSequence.Add((-1, 0, frameData, 0));
                    }

                    break;
            }
        }

        // Add first frame or single frame
        var firstFrame = frameSequence.FirstOrDefault(f => f.Type == 0 || f.Type == 1);
        if (firstFrame.Data != null)
        {
            bytes.AddRange(firstFrame.Data);
            expectedTotalLength = firstFrame.TotalLen;
        }

        // Add consecutive frames
        var consecutiveFrames = frameSequence.Where(f => f.Type == 2).ToList();
        foreach (var cf in consecutiveFrames)
        {
            bytes.AddRange(cf.Data);
        }

        // Trim to expected length
        if (expectedTotalLength > 0 && bytes.Count > expectedTotalLength)
        {
            bytes = bytes.Take(expectedTotalLength).ToList();
        }

        // Fallback: parse as raw hex
        if (bytes.Count == 0)
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.All(Uri.IsHexDigit))
                {
                    bytes.AddRange(ParseHexString(trimmed));
                }
            }
        }

        return bytes;
    }

    /// <summary>
    ///     Splits a line that carries more than one frame run together, using the fixed unspaced
    ///     ELM327 frame width and the responder ID established by the line's first frame.
    /// </summary>
    /// <remarks>
    ///     Returns the line unchanged when it does not fit that geometry, so an unrecognised layout
    ///     degrades to "one frame per line" rather than to a mis-split payload.
    /// </remarks>
    private static List<string> SplitRunTogetherFrames(string line)
    {
        var canId = line[..3];

        // Whole multiples of the frame width, every frame carrying the same responder ID.
        if (line.Length % UnspacedFrameLength == 0 && StartsEveryFrame(line, canId))
        {
            return SplitOnFrameWidth(line, 0);
        }

        // First frame shortened (adapter trimmed padding); the rest still end on the width grid.
        for (var i = 4; i <= line.Length - 6; i++)
        {
            if ((line.Length - i) % UnspacedFrameLength != 0)
            {
                continue;
            }

            if (string.CompareOrdinal(line, i, canId, 0, 3) != 0)
            {
                continue;
            }

            if (!StartsEveryFrame(line[i..], canId))
            {
                continue;
            }

            var frames = SplitOnFrameWidth(line, i);
            frames.Insert(0, line[..i]);
            return frames;
        }

        return [line];
    }

    /// <summary>Cuts <paramref name="line"/> from <paramref name="start"/> into frame-width pieces.</summary>
    private static List<string> SplitOnFrameWidth(string line, int start)
    {
        var frames = new List<string>((line.Length - start) / UnspacedFrameLength);
        for (var i = start; i < line.Length; i += UnspacedFrameLength)
        {
            frames.Add(line.Substring(i, UnspacedFrameLength));
        }

        return frames;
    }

    /// <summary>True when every frame-width position in <paramref name="s"/> starts with <paramref name="canId"/>.</summary>
    private static bool StartsEveryFrame(string s, string canId)
    {
        for (var i = 0; i < s.Length; i += UnspacedFrameLength)
        {
            if (i + 3 > s.Length || string.CompareOrdinal(s, i, canId, 0, 3) != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Checks if a string starts with a valid CAN ID prefix for ISO-TP frames.
    /// </summary>
    private static bool IsCanIdPrefixForIsoTp(string s)
    {
        if (s.Length < 3)
        {
            return false;
        }

        var prefix = s[..3];
        if (!prefix.All(Uri.IsHexDigit))
        {
            return false;
        }

        if (!int.TryParse(prefix, NumberStyles.HexNumber, null, out var id))
        {
            return false;
        }

        // Accept standard OBD-II and Nissan Leaf extended ranges
        return id is >= 0x700 and <= 0x7FF or >= 0x790 and <= 0x79F;
    }

    /// <summary>
    ///     Parse hex string to byte array
    /// </summary>
    public static byte[] ParseHexString(string hex)
    {
        var result = new List<byte>();
        for (var i = 0; i + 1 < hex.Length; i += 2)
        {
            if (byte.TryParse(hex.Substring(i, 2), NumberStyles.HexNumber, null, out var b))
            {
                result.Add(b);
            }
            else
            {
                break;
            }
        }

        return result.ToArray();
    }
}
