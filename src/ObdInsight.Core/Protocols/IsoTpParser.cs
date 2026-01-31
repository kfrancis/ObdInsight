using Serilog;

namespace ObdInsight.Core.Protocols;

/// <summary>
/// Utilities for parsing ISO-TP (ISO 15765-2) responses from ELM327
/// </summary>
public static class IsoTpParser
{
    /// <summary>
    /// Parse ISO-TP response, handling multi-frame messages.
    /// Handles both spaced and concatenated hex formats from ELM327.
    /// Also handles frames concatenated together on a single line (e.g., "7BB25...7BB26...").
    /// </summary>
    public static List<byte> ParseIsoTpResponse(string response)
    {
        var bytes = new List<byte>();

        if (string.IsNullOrWhiteSpace(response))
            return bytes;

        var cleaned = response
            .Replace("\r", "\n")
            .Replace(">", "")
            .Trim();

        var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // First, split any concatenated frames (e.g., "7BB25...7BB26..." becomes two separate frames)
        var allFrames = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 6) continue;

            // Split concatenated frames by finding CAN ID patterns (3 hex chars followed by frame data)
            var remaining = trimmed;
            while (remaining.Length >= 6)
            {
                if (!IsCanIdPrefixForIsoTp(remaining))
                {
                    break;
                }

                // Find the next CAN ID prefix in the string (if any)
                var nextFrameStart = -1;
                for (var i = 7; i <= remaining.Length - 6; i++)
                {
                    var potentialCanId = remaining.Substring(i, 3);
                    if (!potentialCanId.All(Uri.IsHexDigit)) continue;
                    if (!int.TryParse(potentialCanId, System.Globalization.NumberStyles.HexNumber, null, out var id)) continue;

                    if (!(id is >= 0x700 and <= 0x7FF or >= 0x790 and <= 0x79F)) continue;

                    if (i + 3 < remaining.Length)
                    {
                        var frameTypeChar = remaining[i + 3];
                        if (frameTypeChar == '0' || frameTypeChar == '1' || frameTypeChar == '2' || frameTypeChar == '3')
                        {
                            nextFrameStart = i;
                            break;
                        }
                    }
                }

                if (nextFrameStart > 0)
                {
                    allFrames.Add(remaining[..nextFrameStart]);
                    remaining = remaining[nextFrameStart..];
                }
                else
                {
                    allFrames.Add(remaining);
                    break;
                }
            }
        }

        Log.Debug("ParseIsoTpResponse: Split into {FrameCount} raw frames from {LineCount} lines", allFrames.Count, lines.Length);

        var frameSequence = new List<(int Type, int Seq, byte[] Data, int TotalLen)>();
        var expectedTotalLength = 0;

        foreach (var frame in allFrames)
        {
            if (frame.Length < 6) continue;

            if (!IsCanIdPrefixForIsoTp(frame))
                continue;

            var frameHex = frame[3..];

            if (frameHex.Length < 2) continue;

            if (!byte.TryParse(frameHex[..2], System.Globalization.NumberStyles.HexNumber, null, out var frameTypeByte))
                continue;

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
                        frameData = frameData[..sfLen];
                    frameSequence.Add((0, 0, frameData, sfLen));
                    break;

                case 1: // First Frame
                    if (frameHex.Length < 4) continue;
                    if (!byte.TryParse(frameHex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var lenLowByte))
                        continue;
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
                        frameSequence.Add((-1, 0, frameData, 0));
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

        Log.Debug("ParseIsoTpResponse: Parsed {ByteCount} bytes from {FrameCount} frames (expected {ExpectedLen})",
            bytes.Count, frameSequence.Count, expectedTotalLength);

        return bytes;
    }

    /// <summary>
    /// Checks if a string starts with a valid CAN ID prefix for ISO-TP frames.
    /// </summary>
    private static bool IsCanIdPrefixForIsoTp(string s)
    {
        if (s.Length < 3) return false;
        var prefix = s[..3];
        if (!prefix.All(Uri.IsHexDigit)) return false;
        if (!int.TryParse(prefix, System.Globalization.NumberStyles.HexNumber, null, out var id)) return false;

        // Accept standard OBD-II and Nissan Leaf extended ranges
        return id is >= 0x700 and <= 0x7FF or >= 0x790 and <= 0x79F;
    }

    /// <summary>
    /// Parse hex string to byte array
    /// </summary>
    public static byte[] ParseHexString(string hex)
    {
        var result = new List<byte>();
        for (var i = 0; i + 1 < hex.Length; i += 2)
        {
            if (byte.TryParse(hex.Substring(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
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
