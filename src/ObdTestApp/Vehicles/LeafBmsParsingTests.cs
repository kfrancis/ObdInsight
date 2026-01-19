using System;
using System.Collections.Generic;
using System.Linq;
using Serilog;

namespace ObdTestApp.Vehicles;

/// <summary>
/// Self-contained unit tests for Leaf BMS parsing.
/// Run with: dotnet run -- --test
/// </summary>
public static class LeafBmsParsingTests
{
    /// <summary>
    /// Golden sample from actual Nissan Leaf AZE0 BMS Group 01 response.
    /// Captured: 2026-01-18 from 66:1E:87:02:C2:DB
    /// </summary>
    private static readonly string[] GoldenGroup01Lines =
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
    /// Runs all parsing tests and returns success/failure.
    /// </summary>
    public static bool RunAllTests()
    {
        Console.WriteLine("=== LeafBmsParsingTests ===");
        Console.WriteLine();

        var allPassed = true;

        allPassed &= Test_ParseIsoTpFrames_ExtractsCorrectFrameCount();
        allPassed &= Test_ParseIsoTpFrames_ExtractsCorrectFrameTypes();
        allPassed &= Test_ReassembleIsoTpPayload_ProducesCorrectLength();
        allPassed &= Test_ParseGroup01_ExtractsVoltage();
        allPassed &= Test_ParseGroup01_ExtractsCurrent();
        allPassed &= Test_ParseGroup01_ExtractsHx();
        allPassed &= Test_ParseGroup01_ExtractsSocAndCapacity();

        Console.WriteLine();
        Console.WriteLine(allPassed ? "[PASS] All tests passed!" : "[FAIL] Some tests failed!");
        return allPassed;
    }

    private static bool Test_ParseIsoTpFrames_ExtractsCorrectFrameCount()
    {
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);
        var passed = frames.Count == 7;
        ReportResult(nameof(Test_ParseIsoTpFrames_ExtractsCorrectFrameCount),
            passed, $"Expected 7 frames, got {frames.Count}");
        return passed;
    }

    private static bool Test_ParseIsoTpFrames_ExtractsCorrectFrameTypes()
    {
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);

        // First frame should be type 1 (First Frame)
        var hasFF = frames.Any(f => f.FrameType == 1);
        // Should have 6 consecutive frames (type 2)
        var cfCount = frames.Count(f => f.FrameType == 2);

        var passed = hasFF && cfCount == 6;
        ReportResult(nameof(Test_ParseIsoTpFrames_ExtractsCorrectFrameTypes),
            passed, $"HasFF={hasFF}, CFCount={cfCount} (expected 6)");
        return passed;
    }

    private static bool Test_ReassembleIsoTpPayload_ProducesCorrectLength()
    {
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);
        var payload = ReassembleIsoTpPayload(frames);

        // Length should be 0x2B = 43 bytes
        var passed = payload.Length == 43;
        ReportResult(nameof(Test_ReassembleIsoTpPayload_ProducesCorrectLength),
            passed, $"Expected 43 bytes, got {payload.Length}");

        // Also verify header
        if (payload.Length >= 2)
        {
            var headerOk = payload[0] == 0x61 && payload[1] == 0x01;
            if (!headerOk)
            {
                Console.WriteLine($"    Header mismatch: got 0x{payload[0]:X2} 0x{payload[1]:X2}, expected 0x61 0x01");
            }
        }

        return passed;
    }

    private static bool Test_ParseGroup01_ExtractsVoltage()
    {
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);
        var result = ParseGroup01FromFrames(frames);

        // Voltage should be 0x8D52 / 100 = 361.78V
        var expectedVoltage = 0x8D52 / 100.0; // 361.78V
        var passed = result.VoltageVolts.HasValue &&
                     Math.Abs(result.VoltageVolts.Value - expectedVoltage) < 0.01;
        ReportResult(nameof(Test_ParseGroup01_ExtractsVoltage),
            passed, $"Expected {expectedVoltage:F2}V, got {result.VoltageVolts?.ToString("F2") ?? "null"}V");
        return passed;
    }

    private static bool Test_ParseGroup01_ExtractsCurrent()
    {
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);
        var result = ParseGroup01FromFrames(frames);

        // Current = 0x000000EB / 1024 = 0.229A (approximately)
        var expectedCurrent = 0xEB / 1024.0; // ~0.229A
        var passed = result.CurrentAmps.HasValue &&
                     Math.Abs(result.CurrentAmps.Value - expectedCurrent) < 0.01;
        ReportResult(nameof(Test_ParseGroup01_ExtractsCurrent),
            passed, $"Expected {expectedCurrent:F3}A, got {result.CurrentAmps?.ToString("F3") ?? "null"}A");
        return passed;
    }

    private static bool Test_ParseGroup01_ExtractsHx()
    {
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);
        var result = ParseGroup01FromFrames(frames);

        // Using OVMS-style parsing (24/30kWh format):
        // Reassembled data after 61 01 header:
        // Hx at bytes 26-27 = [0D D8] = 0x0DD8 / 100 = 35.44%
        //
        // This is a realistic Hx value for a degraded Leaf battery.
        // Hx (SOH indicator) typically starts at 100% new and degrades over time.
        // 35% suggests significant battery degradation, which is plausible for an older Leaf.

        var expectedHx = 0x0DD8 / 100.0; // 35.44%
        var passed = result.HxPercent.HasValue &&
                     Math.Abs(result.HxPercent.Value - expectedHx) < 0.1;
        ReportResult(nameof(Test_ParseGroup01_ExtractsHx),
            passed, $"Expected {expectedHx:F2}%, got {result.HxPercent?.ToString("F2") ?? "null"}%");
        return passed;
    }

    private static bool Test_ParseGroup01_ExtractsSocAndCapacity()
    {
        var frames = ParseIsoTpFrames(GoldenGroup01Lines);
        var result = ParseGroup01FromFrames(frames);

        // For 24/30kWh Leaf (41 data bytes):
        // - SOC is NOT available in Group 01 (must use passive CAN monitoring)
        // - AHR at bytes 33-35 in reassembled payload (after 61 01 header):
        //   Payload: ...08 05 C1... at indices 33-35
        //   AHR = 0x0805C1 / 10000 = 52.58 Ah
        //
        // A new 30kWh Leaf has ~66Ah nominal (30000Wh / 360V ~= 83Ah actual usable ~66Ah).
        // 52.58Ah indicates some degradation, which is reasonable for an older Leaf.

        var socOk = !result.SocPercent.HasValue; // SOC should be null for 24/30kWh
        var expectedAhr = 0x0805C1 / 10000.0; // 52.58 Ah
        var ahrOk = result.CapacityAh.HasValue &&
                    Math.Abs(result.CapacityAh.Value - expectedAhr) < 0.1;

        var passed = socOk && ahrOk;
        ReportResult(nameof(Test_ParseGroup01_ExtractsSocAndCapacity),
            passed, $"SOC={result.SocPercent?.ToString("F2") ?? "null (expected for 24/30kWh)"}%, AHR={result.CapacityAh?.ToString("F2") ?? "null"}Ah (expected {expectedAhr:F2}Ah)");
        return passed;
    }

    private static void ReportResult(string testName, bool passed, string details)
    {
        var status = passed ? "[PASS]" : "[FAIL]";
        Console.WriteLine($"  {status} {testName}");
        Console.WriteLine($"         {details}");
    }

    // Re-implement the parsing methods for testing (copy from LeafAze0Bms)
    private static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFrames(string[] lines)
    {
        var frames = new List<(int FrameType, int SeqOrLen, byte[] Data)>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 5) continue;

            var canIdHex = trimmed[..3];
            if (!int.TryParse(canIdHex, System.Globalization.NumberStyles.HexNumber, null, out var canId))
                continue;
            if (canId < 0x700 || canId > 0x7FF)
                continue;

            var frameHex = trimmed[3..];
            if (frameHex.Length < 2) continue;

            var frameBytes = new List<byte>();
            for (var i = 0; i + 1 < frameHex.Length; i += 2)
            {
                if (byte.TryParse(frameHex.AsSpan(i, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
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
                    frames.Add((0, frameInfo, frameBytes.Skip(1).ToArray()));
                    break;
                case 1:
                    if (frameBytes.Count >= 2)
                    {
                        var totalLen = (frameInfo << 8) | frameBytes[1];
                        frames.Add((1, totalLen, frameBytes.Skip(2).ToArray()));
                    }
                    break;
                case 2:
                    frames.Add((2, frameInfo, frameBytes.Skip(1).ToArray()));
                    break;
            }
        }

        return frames;
    }

    private static byte[] ReassembleIsoTpPayload(List<(int FrameType, int SeqOrLen, byte[] Data)> frames)
    {
        var payload = new List<byte>();
        var expectedLength = 0;

        var firstFrame = frames.FirstOrDefault(f => f.FrameType == 0 || f.FrameType == 1);
        if (firstFrame.Data == null)
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

            // Keep arrival order - don't sort by sequence (wraps at 0xF)
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
    private static (double? SocPercent, double? VoltageVolts, double? CurrentAmps,
                    double? CapacityAh, double? HxPercent) ParseGroup01FromFrames(
        List<(int FrameType, int SeqOrLen, byte[] Data)> frames)
    {
        double? currentAmps = null;
        double? voltageVolts = null;
        double? socPercent = null;
        double? capacityAh = null;
        double? hxPercent = null;

        // Debug: Print all frames
        Console.WriteLine("  Parsed frames:");
        foreach (var f in frames)
        {
            var typeStr = f.FrameType switch { 0 => "SF", 1 => "FF", 2 => $"CF{f.SeqOrLen}", _ => "??" };
            Console.WriteLine($"    {typeStr}: [{string.Join(" ", f.Data.Select(b => b.ToString("X2")))}]");
        }

        // Reassemble for OVMS-style offset access
        var payload = ReassembleIsoTpPayload(frames);
        Console.WriteLine($"  Reassembled payload ({payload.Length} bytes): {BitConverter.ToString(payload).Replace("-", " ")}");

        if (payload.Length < 4 || payload[0] != 0x61 || payload[1] != 0x01)
        {
            Console.WriteLine("  ERROR: Invalid header");
            return (null, null, null, null, null);
        }

        // Data after 61 01
        var data = payload.Skip(2).ToArray();
        var dataLen = data.Length;
        Console.WriteLine($"  Data portion ({dataLen} bytes): 39=24kWh, 41=30kWh, 49=ZE1");

        // Current1 at bytes 0-3
        if (dataLen >= 4)
        {
            var currentUnsigned = ((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3];
            var currentSigned = unchecked((int)currentUnsigned);
            currentAmps = currentSigned / 1024.0;
            Console.WriteLine($"    Current: data[0-3]=[{data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2}] = 0x{currentUnsigned:X8} / 1024 = {currentAmps:F3}A");
        }

        // Voltage from CF3
        var cfMap = frames.Where(f => f.FrameType == 2).ToDictionary(f => f.SeqOrLen, f => f.Data);
        if (cfMap.TryGetValue(3, out var cf3) && cf3.Length >= 2)
        {
            var voltageRaw = (cf3[0] << 8) | cf3[1];
            voltageVolts = voltageRaw / 100.0;
            Console.WriteLine($"    Voltage: cf3[0-1]=[{cf3[0]:X2} {cf3[1]:X2}] = 0x{voltageRaw:X4} / 100 = {voltageVolts:F2}V");
        }

        bool isZE1 = dataLen >= 49;

        if (isZE1)
        {
            // ZE1 format
            if (dataLen >= 30)
            {
                var hxRaw = (data[28] << 8) | data[29];
                hxPercent = hxRaw / 102.4;
                Console.WriteLine($"    Hx (ZE1): data[28-29]=[{data[28]:X2} {data[29]:X2}] = 0x{hxRaw:X4} / 102.4 = {hxPercent:F2}%");
            }
            if (dataLen >= 34)
            {
                var socRaw = (data[31] << 16) | (data[32] << 8) | data[33];
                socPercent = socRaw / 10000.0;
                Console.WriteLine($"    SOC (ZE1): data[31-33]=[{data[31]:X2} {data[32]:X2} {data[33]:X2}] = 0x{socRaw:X6} / 10000 = {socPercent:F2}%");
            }
            if (dataLen >= 38)
            {
                var ahrRaw = (data[35] << 16) | (data[36] << 8) | data[37];
                capacityAh = ahrRaw / 10000.0;
                Console.WriteLine($"    AHR (ZE1): data[35-37]=[{data[35]:X2} {data[36]:X2} {data[37]:X2}] = 0x{ahrRaw:X6} / 10000 = {capacityAh:F2}Ah");
            }
        }
        else
        {
            // 24/30kWh format
            if (dataLen >= 28)
            {
                var hxRaw = (data[26] << 8) | data[27];
                hxPercent = hxRaw / 100.0;
                Console.WriteLine($"    Hx (24/30kWh): data[26-27]=[{data[26]:X2} {data[27]:X2}] = 0x{hxRaw:X4} / 100 = {hxPercent:F2}%");
            }
            if (dataLen >= 36)
            {
                var ahrRaw = (data[33] << 16) | (data[34] << 8) | data[35];
                capacityAh = ahrRaw / 10000.0;
                Console.WriteLine($"    AHR (24/30kWh): data[33-35]=[{data[33]:X2} {data[34]:X2} {data[35]:X2}] = 0x{ahrRaw:X6} / 10000 = {capacityAh:F2}Ah");
            }
            Console.WriteLine("    SOC: Not available in Group 01 for 24/30kWh (use passive CAN)");
        }

        return (socPercent, voltageVolts, currentAmps, capacityAh, hxPercent);
    }
}
