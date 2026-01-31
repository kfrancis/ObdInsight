using OdbTestApp.Tests.Fixtures;
using static OdbTestApp.Tests.NissanLeaf.AZE0.Unit.LeafBmsParsingHelpers;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;

namespace OdbTestApp.Tests.NissanLeaf.AZE0.Integration;

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
        var context = LeafAze0Contexts.Ident;

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
        var context = LeafAze0Contexts.Ident;

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
        var context = LeafAze0Contexts.Ident;

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
        var context = LeafAze0Contexts.Ident;

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
