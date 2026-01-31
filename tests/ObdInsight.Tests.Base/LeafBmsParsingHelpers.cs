using System.Globalization;

namespace ObdInsight.Tests.Base;

/// <summary>
/// Shared parsing utilities for Nissan Leaf BMS data.
/// These methods are extracted from LeafAze0Bms for testing.
/// </summary>
public static class BmsParsingHelpers
{
    /// <summary>
    /// Golden sample from actual Nissan Leaf AZE0 BMS Group 01 response.
    /// Captured: 2026-01-18 from 66:1E:87:02:C2:DB
    /// </summary>
    public static readonly string[] GoldenGroup01Lines =
    [
        "7BB102B6101000000EB",  // FF: len=43, [61 01 00 00 00 EB]
        "7BB21028AFFFFFD5AFF",  // CF1: [02 8A FF FF FD 5A FF]
        "7BB22FFFFFF07F220AC",  // CF2: [FF FF FF 07 F2 20 AC]
        "7BB238D52386C039201",  // CF3: [8D 52 38 6C 03 92 01]
        "7BB244E0DD80006658A",  // CF4: [4E 0D D8 00 06 65 8A]
        "7BB25000805C1800005",  // CF5: [00 08 05 C1 80 00 05]
        "7BB260000FFFFFFFFFF",  // CF6: [00 00 FF...]
    ];

    /// <summary>
    /// Parses ISO-TP frames from ELM327 response lines.
    /// Handles format like "7BB102B6101000000EB" (CAN_ID + frame bytes, no spaces).
    /// </summary>
    public static List<IsoTpFrame> ParseIsoTpFrames(string[] lines)
    {
        var frames = new List<IsoTpFrame>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 5) continue;

            var canIdHex = trimmed[..3];
            if (!int.TryParse(canIdHex, NumberStyles.HexNumber, null, out var canId))
                continue;
            if (canId < 0x700 || canId > 0x7FF)
                continue;

            var frameHex = trimmed[3..];
            if (frameHex.Length < 2) continue;

            // Handle Nissan Leaf dumps where 'H' is used in place of the high nibble '4'
            frameHex = frameHex.Replace('H', '4');

            var frameBytes = new List<byte>();
            for (var i = 0; i + 1 < frameHex.Length; i += 2)
            {
                if (byte.TryParse(frameHex.AsSpan(i, 2), NumberStyles.HexNumber, null, out var b))
                    frameBytes.Add(b);
                else
                    break;
            }

            if (frameBytes.Count == 0) continue;

            var pci = frameBytes[0];
            var frameType = (pci >> 4) & 0x0F;
            var frameInfo = pci & 0x0F;

            switch (frameType)
            {
                case 0:
                    frames.Add(new IsoTpFrame(0, frameInfo, frameBytes.Skip(1).ToArray()));
                    break;
                case 1:
                    if (frameBytes.Count >= 2)
                    {
                        var totalLen = (frameInfo << 8) | frameBytes[1];
                        frames.Add(new IsoTpFrame(1, totalLen, frameBytes.Skip(2).ToArray()));
                    }
                    break;
                case 2:
                    frames.Add(new IsoTpFrame(2, frameInfo, frameBytes.Skip(1).ToArray()));
                    break;
            }
        }

        return frames;
    }

    /// <summary>
    /// Reassembles ISO-TP payload from parsed frames.
    /// </summary>
    public static byte[] ReassembleIsoTpPayload(List<IsoTpFrame> frames)
    {
        var payload = new List<byte>();
        int expectedLength;

        var firstFrame = frames.FirstOrDefault(f => f.FrameType == 0 || f.FrameType == 1);
        if (firstFrame?.Data == null)
            return [];

        if (firstFrame.FrameType == 0)
        {
            expectedLength = firstFrame.SeqOrLen;
            var dataLen = Math.Min(expectedLength, firstFrame.Data.Length);
            payload.AddRange(firstFrame.Data.Take(dataLen));
        }
        else
        {
            expectedLength = firstFrame.SeqOrLen;
            payload.AddRange(firstFrame.Data);

            var consecutiveFrames = frames
                .Where(f => f.FrameType == 2)
                .ToList();

            foreach (var cf in consecutiveFrames)
            {
                payload.AddRange(cf.Data);
                if (payload.Count >= expectedLength)
                    break;
            }
        }

        if (expectedLength > 0 && payload.Count > expectedLength)
            return payload.Take(expectedLength).ToArray();

        return payload.ToArray();
    }

    /// <summary>
    /// Parses Group 01 using OVMS-style offsets on reassembled payload.
    /// </summary>
    public static BmsGroup01Data ParseGroup01FromFrames(List<IsoTpFrame> frames)
    {
        double? currentAmps = null;
        double? voltageVolts = null;
        double? socPercent = null;
        double? capacityAh = null;
        double? hxPercent = null;

        var payload = ReassembleIsoTpPayload(frames);

        if (payload.Length < 4 || payload[0] != 0x61 || payload[1] != 0x01)
        {
            return new BmsGroup01Data(null, null, null, null, null);
        }

        var data = payload.Skip(2).ToArray();
        var dataLen = data.Length;

        // Current1 at bytes 0-3
        if (dataLen >= 4)
        {
            var currentUnsigned = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
            var currentSigned = unchecked((int)currentUnsigned);
            currentAmps = currentSigned / 1024.0;
        }

        // Voltage from CF3
        var cfMap = frames.Where(f => f.FrameType == 2).ToDictionary(f => f.SeqOrLen, f => f.Data);
        if (cfMap.TryGetValue(3, out var cf3) && cf3.Length >= 2)
        {
            var voltageRaw = (cf3[0] << 8) | cf3[1];
            voltageVolts = voltageRaw / 100.0;
        }

        bool isZE1 = dataLen >= 49;

        if (isZE1)
        {
            if (dataLen >= 30)
            {
                var hxRaw = (data[28] << 8) | data[29];
                hxPercent = hxRaw / 102.4;
            }
            if (dataLen >= 34)
            {
                var socRaw = (data[31] << 16) | (data[32] << 8) | data[33];
                socPercent = socRaw / 10000.0;
            }
            if (dataLen >= 38)
            {
                var ahrRaw = (data[35] << 16) | (data[36] << 8) | data[37];
                capacityAh = ahrRaw / 10000.0;
            }
        }
        else
        {
            if (dataLen >= 28)
            {
                var hxRaw = (data[26] << 8) | data[27];
                hxPercent = hxRaw / 100.0;
            }
            if (dataLen >= 36)
            {
                var ahrRaw = (data[33] << 16) | (data[34] << 8) | data[35];
                capacityAh = ahrRaw / 10000.0;
            }
        }

        return new BmsGroup01Data(socPercent, voltageVolts, currentAmps, capacityAh, hxPercent);
    }
}

/// <summary>
/// Represents a parsed ISO-TP frame.
/// </summary>
public record IsoTpFrame(int FrameType, int SeqOrLen, byte[] Data);

/// <summary>
/// Represents parsed BMS Group 01 data.
/// </summary>
public record BmsGroup01Data(
    double? SocPercent,
    double? VoltageVolts,
    double? CurrentAmps,
    double? CapacityAh,
    double? HxPercent);
