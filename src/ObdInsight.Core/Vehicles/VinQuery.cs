using System.Diagnostics;
using ObdInsight.Core.Adapters;

namespace ObdInsight.Core.Vehicles;

public static class VinQuery
{
    public static async Task<string?> TryGetVinAsync(
        IObdAdapter adapter,
        IObdTransport transport,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(transport);

        if (!transport.IsConnected)
            return null;

        try
        {
            var response = await adapter.SendCommandAsync(
                ObdCommand.Create("2181", TimeSpan.FromSeconds(3)),
                cancellationToken);

            if (!response.Success || string.IsNullOrWhiteSpace(response.Value))
                return null;

            return TryParseVin(response.Value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decode VIN information for Nissan Leaf.
    /// </summary>
    private static void DecodeVin(string vin)
    {
        if (string.IsNullOrEmpty(vin) || vin.Length < 10)
            return;

        // World Manufacturer Identifier (first 3 chars)
        var wmi = vin[..3];
        var manufacturer = wmi switch
        {
            "1N4" => "Nissan (USA - Smyrna, TN)",
            "JN1" => "Nissan (Japan)",
            "SJN" => "Nissan (UK - Sunderland)",
            "VNK" => "Nissan (France)",
            _ => $"Unknown ({wmi})"
        };
        Debug.WriteLine($"Manufacturer: {manufacturer}");

        // Vehicle attributes (chars 4-8)
        if (vin.Length >= 5)
        {
            var modelCode = vin.Substring(3, 2);
            var model = modelCode switch
            {
                "BZ" => "Leaf (BEV)",
                "AZ" => "Leaf (BEV)",
                _ => $"Model code: {modelCode}"
            };
            Debug.WriteLine($"Model: {model}");
        }

        // Model year (10th character)
        if (vin.Length >= 10)
        {
            var yearChar = vin[9];
            var year = yearChar switch
            {
                'A' => 2010,
                'B' => 2011,
                'C' => 2012,
                'D' => 2013,
                'E' => 2014,
                'F' => 2015,
                'G' => 2016,
                'H' => 2017,
                'J' => 2018,
                'K' => 2019,
                'L' => 2020,
                'M' => 2021,
                'N' => 2022,
                'P' => 2023,
                'R' => 2024,
                'S' => 2025,
                _ => 0
            };
            if (year > 0)
            {
                Debug.WriteLine($"Model Year: {year}");

                // Determine battery type based on year
                string battery;
                if (year <= 2015)
                    battery = "24 kWh (ZE0)";
                else if (year == 2016)
                    battery = "24/30 kWh (AZE0)";
                else if (year == 2017)
                    battery = "30 kWh (AZE0)";
                else if (year >= 2018 && year <= 2021)
                    battery = "40/62 kWh (ZE1)";
                else
                    battery = "40/60 kWh (ZE1)";

                Debug.WriteLine($"Battery Type: {battery}");
            }
        }

        // Assembly plant (11th character)
        if (vin.Length >= 11)
        {
            var plantChar = vin[10];
            var plant = plantChar switch
            {
                'C' => "Smyrna, Tennessee, USA",
                'A' => "Oppama, Japan",
                'K' => "Sunderland, UK",
                _ => $"Plant code: {plantChar}"
            };
            Debug.WriteLine($"   Assembly Plant: {plant}");
        }

        // Serial number (chars 12-17)
        if (vin.Length >= 17)
        {
            var serial = vin[11..17];
            Debug.WriteLine($"   Serial: {serial}");
        }
    }

    private static bool IsCanIdPrefix(string s)
    {
        if (s.Length < 3) return false;
        var prefix = s[..3];
        return prefix.All(c => Uri.IsHexDigit(c)) &&
               int.TryParse(prefix, System.Globalization.NumberStyles.HexNumber, null, out var id) &&
               id >= 0x700 && id <= 0x7FF;
    }

    private static byte[] ParseHexString(string hex)
    {
        var result = new List<byte>();
        for (int i = 0; i + 1 < hex.Length; i += 2)
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

    /// <summary>
    /// Parse ISO-TP response, handling multi-frame messages.
    /// Handles both spaced and concatenated hex formats from ELM327.
    /// </summary>
    private static List<byte> ParseIsoTpResponse(string response)
    {
        var bytes = new List<byte>();

        if (string.IsNullOrWhiteSpace(response))
            return bytes;

        var cleaned = response
            .Replace("\r", "\n")
            .Replace(">", "")
            .Trim();

        var lines = cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var frameSequence = new List<(int Type, int Seq, byte[] Data)>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 6) continue;

            if (!IsCanIdPrefix(trimmed))
                continue;

            var frameHex = trimmed[3..];

            if (frameHex.Length < 2) continue;

            if (!byte.TryParse(frameHex[..2], System.Globalization.NumberStyles.HexNumber, null, out var frameTypeByte))
                continue;

            var frameType = (frameTypeByte & 0xF0) >> 4;
            var frameInfo = frameTypeByte & 0x0F;

            byte[] frameData;

            switch (frameType)
            {
                case 0:
                    var sfLen = frameInfo;
                    var sfDataHex = frameHex[2..];
                    frameData = ParseHexString(sfDataHex);
                    if (frameData.Length > sfLen)
                        frameData = frameData[..sfLen];
                    frameSequence.Add((0, 0, frameData));
                    break;

                case 1:
                    if (frameHex.Length < 4) continue;
                    if (!byte.TryParse(frameHex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var lenLowByte))
                        continue;
                    var ffDataHex = frameHex[4..];
                    frameData = ParseHexString(ffDataHex);
                    frameSequence.Add((1, 0, frameData));
                    break;

                case 2:
                    var seqNum = frameInfo;
                    var cfDataHex = frameHex[2..];
                    frameData = ParseHexString(cfDataHex);
                    frameSequence.Add((2, seqNum, frameData));
                    break;

                default:
                    frameData = ParseHexString(frameHex);
                    if (frameData.Length > 0)
                        frameSequence.Add((-1, 0, frameData));
                    break;
            }
        }

        var firstFrame = frameSequence.FirstOrDefault(f => f.Type == 0 || f.Type == 1);
        if (firstFrame.Data != null)
        {
            bytes.AddRange(firstFrame.Data);
        }

        var consecutiveFrames = frameSequence
            .Where(f => f.Type == 2)
            .OrderBy(f => f.Seq)
            .ToList();

        foreach (var cf in consecutiveFrames)
        {
            bytes.AddRange(cf.Data);
        }

        if (bytes.Count == 0)
        {
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.All(c => Uri.IsHexDigit(c)))
                {
                    bytes.AddRange(ParseHexString(trimmed));
                }
            }
        }

        return bytes;
    }

    /// <summary>
    /// Parse VIN from charger response.
    /// From 2017 Leaf: 79A10156181314E3442\r79A215A304350334843\r79A2233313034303800
    /// Decoded: 61 81 31 4E 34 42 5A 30 43 50 33 48 43 33 31 30 34 30 38 00
    ///        = "1N4BZ0CP3HC310408" (example)
    /// </summary>
    private static string? TryParseVin(string response)
    {
        try
        {
            var bytes = ParseIsoTpResponse(response);

            Debug.WriteLine($"Parsed {bytes.Count} bytes");

            if (bytes.Count < 5)
            {
                Debug.WriteLine("Not enough data for VIN");
                return null;
            }

            // Show raw for debugging
            if (bytes.Count <= 25)
            {
                Debug.WriteLine($"Raw: {BitConverter.ToString(bytes.ToArray())}");
            }

            // Find response header 61 81 (positive response to 21 81)
            int vinStart = -1;
            for (int i = 0; i < bytes.Count - 1; i++)
            {
                if (bytes[i] == 0x61 && bytes[i + 1] == 0x81)
                {
                    vinStart = i + 2; // VIN starts after header
                    break;
                }
            }

            if (vinStart >= 0)
            {
                // Extract up to 17 characters for VIN
                var vinBytes = bytes.Skip(vinStart).Take(17).ToArray();

                // Convert to ASCII, filtering out non-printable
                var vinChars = vinBytes
                    .Where(b => b >= 0x20 && b < 0x7F)
                    .Select(b => (char)b)
                    .ToArray();

                var vin = new string(vinChars).Trim('\0', ' ');

                if (vin.Length >= 10)
                {
                    Debug.WriteLine($"VIN: {vin}");
                    DecodeVin(vin);
                    return vin;
                }
            }

            // Alternative: try to find ASCII printable characters
            var allPrintable = bytes
                .Where(b => b >= 0x30 && b <= 0x5A) // 0-9, A-Z
                .Select(b => (char)b)
                .ToArray();

            if (allPrintable.Length >= 10)
            {
                var rawVin = new string(allPrintable);
                // Take first 17 VIN characters
                if (rawVin.Length > 17)
                    rawVin = rawVin[..17];

                Debug.WriteLine($"VIN: {rawVin}");
                DecodeVin(rawVin);

                return rawVin;
            }
            else
            {
                Debug.WriteLine("Could not extract VIN");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Parse error: {ex.Message}");
        }

        return null;
    }
}