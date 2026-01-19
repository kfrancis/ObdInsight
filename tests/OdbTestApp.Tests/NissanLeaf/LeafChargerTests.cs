using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using OdbTestApp.Tests.Fixtures;
using ObdTestApp.Vehicles;
using static OdbTestApp.Tests.NissanLeaf.LeafBmsParsingHelpers;

namespace OdbTestApp.Tests.NissanLeaf;

/// <summary>
/// Unit tests for Nissan Leaf Charger VIN parsing using golden sample data.
/// </summary>
public class LeafChargerVinParsingTests
{
    /// <summary>
    /// Golden sample for a 2017 Nissan Leaf AZE0 VIN query (Mode 21 PID 81).
    /// Fake/Generated VIN: 1N4AZ0CP7HC308656
    /// </summary>
    private static readonly string[] s_goldenVinLines =
    [
        // FF (First Frame): ID=79A, PCI=10 (First Frame), Len=0x15 (21 decimal)
        // Payload: [61 81] (Response) + "1N4A" (31 4E 34 41)
        "79A10156181314E3441",

        // CF1 (Consecutive Frame 1): ID=79A, PCI=21
        // Payload: "Z0CP7HC" (5A 30 43 50 37 48 43)
        "79A215A304350374843",

        // CF2 (Consecutive Frame 2): ID=79A, PCI=22
        // Payload: "308656" (33 30 38 36 35 36) + 00 (Padding/Termination)
        "79A2233303836353600",

        // CF3 (Consecutive Frame 3): ID=79A, PCI=23
        // Payload: 00 (Remaining Termination) + CAN Frame Padding
        "79A230000000000000"
    ];

    [Test]
    public async Task ParseVin_ExtractsCorrectVin()
    {
        // Arrange
        var frames = ParseIsoTpFrames(s_goldenVinLines);
        var payload = ReassembleIsoTpPayload(frames);

        // Act
        var vin = ParseVinFromPayload(payload);

        // Assert
        await Assert.That(vin).IsEqualTo("1N4AZ0CP7HC308656");
    }

    [Test]
    public async Task ParseVin_HandlesEmptyLines()
    {
        // Act
        var vin = ParseVinFromLines([]);

        // Assert
        await Assert.That(vin).IsNull();
    }

    [Test]
    public async Task ParseVin_HandlesInvalidHeader()
    {
        // Arrange - payload with wrong header
        var invalidPayload = new byte[] { 0x7F, 0x81, 0x31, 0x4E };

        // Act
        var vin = ParseVinFromPayload(invalidPayload);

        // Assert
        await Assert.That(vin).IsNull();
    }

    [Test]
    public async Task ParseVin_HandlesNullLines()
    {
        // Act
        var vin = ParseVinFromLines(null);

        // Assert
        await Assert.That(vin).IsNull();
    }

    [Test]
    public async Task ParseVinFrames_ExtractsCorrectFrameCount()
    {
        // Arrange & Act
        var frames = ParseIsoTpFrames(s_goldenVinLines);

        // Assert
        await Assert.That(frames).Count().IsEqualTo(3);
    }

    [Test]
    public async Task ParseVinPayload_HasValidHeader()
    {
        // Arrange
        var frames = ParseIsoTpFrames(s_goldenVinLines);

        // Act
        var payload = ReassembleIsoTpPayload(frames);

        // Assert - response to Mode 21 PID 81 should be 61 81
        await Assert.That(payload[0]).IsEqualTo((byte)0x61);
        await Assert.That(payload[1]).IsEqualTo((byte)0x81);
    }

    [Test]
    public async Task ParseVinPayload_ProducesCorrectLength()
    {
        // Arrange
        var frames = ParseIsoTpFrames(s_goldenVinLines);

        // Act
        var payload = ReassembleIsoTpPayload(frames);

        // Assert - should be 21 bytes as indicated by First Frame
        await Assert.That(payload).Count().IsEqualTo(21);
    }

    /// <summary>
    /// Helper to parse VIN from lines (wraps the parsing steps).
    /// </summary>
    private static string? ParseVinFromLines(string[]? lines)
    {
        if (lines == null || lines.Length == 0)
            return null;

        var frames = ParseIsoTpFrames(lines);
        if (frames.Count == 0)
            return null;

        var payload = ReassembleIsoTpPayload(frames);
        return ParseVinFromPayload(payload);
    }

    /// <summary>
    /// Parses VIN from reassembled ISO-TP payload.
    /// Expected format: [61 81] [VIN ASCII bytes] [00 padding]
    /// </summary>
    private static string? ParseVinFromPayload(byte[] payload)
    {
        // Validate header (61 81 = response to Mode 21 PID 81)
        if (payload.Length < 3 || payload[0] != 0x61 || payload[1] != 0x81)
            return null;

        // VIN data starts at byte 2
        var vinBytes = payload.AsSpan(2);

        // Convert to ASCII, stopping at first null byte
        var chars = new List<char>();
        foreach (var b in vinBytes)
        {
            if (b == 0x00)
                break;

            // Only include valid ASCII characters
            if (b >= 0x20 && b <= 0x7E)
                chars.Add((char)b);
        }

        return chars.Count >= 17 ? new string([.. chars]) : null;
    }
}

/// <summary>
/// Integration tests for Nissan Leaf Charger using a real BLE connection.
/// </summary>
[ClassDataSource<BleSessionFixture>(Shared = SharedType.Keyed)]
public class LeafChargerIntegrationTests(BleSessionFixture bleFixture)
{
    [Test]
    public async Task QueryVin_ContainsOnlyValidCharacters()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.Charger;

        // Act
        var lines = await session.QueryAsync("2181", context, CancellationToken.None);
        var frames = ParseIsoTpFrames(lines);
        var payload = ReassembleIsoTpPayload(frames);
        var vin = ParseVinFromPayload(payload);

        // Assert - VIN should only contain 0-9, A-Z (excluding I, O, Q)
        await Assert.That(vin).IsNotNull();
        foreach (var c in vin!)
        {
            var isValid = (c >= '0' && c <= '9') ||
                         (c >= 'A' && c <= 'Z' && c != 'I' && c != 'O' && c != 'Q');
            await Assert.That(isValid).IsTrue();
        }
    }

    [Test]
    public async Task QueryVin_Returns17CharacterVin()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.Charger;

        // Act
        var lines = await session.QueryAsync("2181", context, CancellationToken.None);
        var frames = ParseIsoTpFrames(lines);
        var payload = ReassembleIsoTpPayload(frames);
        var vin = ParseVinFromPayload(payload);

        // Assert
        await Assert.That(vin).IsNotNull();
        await Assert.That(vin!.Length).IsEqualTo(17);
    }

    [Test]
    public async Task QueryVin_ReturnsValidFormat()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.Charger;

        // Act
        var lines = await session.QueryAsync("2181", context, CancellationToken.None);

        // Assert
        await Assert.That(lines).IsNotEmpty();

        var frames = ParseIsoTpFrames(lines);
        await Assert.That(frames).IsNotEmpty();

        var payload = ReassembleIsoTpPayload(frames);
        await Assert.That(payload).Count().IsGreaterThanOrEqualTo(3);
        await Assert.That(payload[0]).IsEqualTo((byte)0x61);
        await Assert.That(payload[1]).IsEqualTo((byte)0x81);
    }

    [Test]
    public async Task QueryVin_StartsWithManufacturerCode()
    {
        // Arrange
        var session = bleFixture.Session;
        var context = LeafAze0Contexts.Charger;

        // Act
        var lines = await session.QueryAsync("2181", context, CancellationToken.None);
        var frames = ParseIsoTpFrames(lines);
        var payload = ReassembleIsoTpPayload(frames);
        var vin = ParseVinFromPayload(payload);

        // Assert - Nissan VINs start with '1N4' (USA) or 'JN1' (Japan)
        await Assert.That(vin).IsNotNull();
        await Assert.That(vin!.StartsWith("1N4") || vin.StartsWith("JN1")).IsTrue();
    }

    /// <summary>
    /// Parses VIN from reassembled ISO-TP payload.
    /// </summary>
    private static string? ParseVinFromPayload(byte[] payload)
    {
        if (payload.Length < 3 || payload[0] != 0x61 || payload[1] != 0x81)
            return null;

        var vinBytes = payload.AsSpan(2);
        var chars = new List<char>();

        foreach (var b in vinBytes)
        {
            if (b == 0x00)
                break;

            // Only include valid ASCII characters
            if (b >= 0x20 && b <= 0x7E)
                chars.Add((char)b);
        }

        return chars.Count >= 17 ? new string([.. chars]) : null;
    }
}
