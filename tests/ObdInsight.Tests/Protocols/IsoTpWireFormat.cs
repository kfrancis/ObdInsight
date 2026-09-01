using System.Text;

namespace OdbTestApp.Tests.Protocols;

/// <summary>
///     Encodes a payload into the ISO-TP (ISO 15765-2) wire lines an ELM327 emits with spaces off:
///     a 3-hex-digit CAN ID followed by 8 data bytes per frame, no separators.
/// </summary>
/// <remarks>
///     This is the inverse of <see cref="ObdInsight.Core.Protocols.IsoTpParser" />, not a second copy
///     of it: encoding is the ~30 lines of ISO-TP framing straight from the spec, and round-tripping
///     it through the production parser is the property under test. Sanctioned narrowly for that
///     reason — no parsing logic lives here.
/// </remarks>
internal static class IsoTpWireFormat
{
    /// <summary>Bytes carried per CAN frame.</summary>
    private const int FrameBytes = 8;

    /// <summary>
    ///     Frames <paramref name="payload" /> as a single frame (≤7 bytes) or a first frame plus
    ///     consecutive frames, padded to 8 bytes with <paramref name="padding" />.
    /// </summary>
    public static string[] Encode(byte[] payload, int canId, byte padding)
    {
        var lines = new List<string>();
        var prefix = canId.ToString("X3");

        if (payload.Length <= FrameBytes - 1)
        {
            // Single frame: 0{len} + data.
            lines.Add(prefix + Hex([(byte)payload.Length, .. payload], padding));
            return [.. lines];
        }

        // First frame: 1{len:X3} + first 6 bytes.
        var header = new[] { (byte)(0x10 | ((payload.Length >> 8) & 0x0F)), (byte)(payload.Length & 0xFF) };
        var firstData = payload.Take(FrameBytes - 2).ToArray();
        lines.Add(prefix + Hex([.. header, .. firstData], padding));

        // Consecutive frames: 2{seq} + next 7 bytes, sequence wrapping 1..F,0,1...
        var offset = FrameBytes - 2;
        var seq = 1;
        while (offset < payload.Length)
        {
            var data = payload.Skip(offset).Take(FrameBytes - 1).ToArray();
            lines.Add(prefix + Hex([(byte)(0x20 | (seq & 0x0F)), .. data], padding));
            offset += FrameBytes - 1;
            seq++;
        }

        return [.. lines];
    }

    private static string Hex(byte[] frame, byte padding)
    {
        var sb = new StringBuilder(FrameBytes * 2);
        for (var i = 0; i < FrameBytes; i++)
        {
            sb.Append((i < frame.Length ? frame[i] : padding).ToString("X2"));
        }

        return sb.ToString();
    }
}
