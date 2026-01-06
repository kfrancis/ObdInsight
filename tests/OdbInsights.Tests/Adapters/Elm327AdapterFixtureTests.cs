using ObdInsight.Drivers.Adapters.Elm327;
using ObdInsight.Core.Transports.Tracing;

namespace OdbInsights.Tests.Adapters;

/// <summary>
/// Integration tests using embedded session fixtures.
/// These tests demonstrate deterministic testing with recorded data.
/// </summary>
public class Elm327AdapterFixtureTests
{
    // Note: The embedded resource path matches the default namespace + folder structure
    private const string SampleSessionResource = "ObdInsights.Tests.TestData.sample_elm327_session.jsonl";

    [Test]
    public async Task DeterministicReplay_SameSessionTwice_ProducesSameResults()
    {
        // Arrange
        var assembly = typeof(Elm327AdapterFixtureTests).Assembly;

        // First run
        using var transport1 = await ReplayTransportFactory.FromResourceAsync(
            assembly,
            SampleSessionResource,
            new ReplayOptions { MatchingMode = ReplayMatchingMode.Any });
        await transport1.ConnectAsync();

        var adapter1 = new Elm327Adapter();
        await adapter1.InitializeAsync(transport1);
        var version1 = adapter1.DeviceVersion;
        var protocol1 = adapter1.ProtocolDescription;

        // Second run
        using var transport2 = await ReplayTransportFactory.FromResourceAsync(
            assembly,
            SampleSessionResource,
            new ReplayOptions { MatchingMode = ReplayMatchingMode.Any });
        await transport2.ConnectAsync();

        var adapter2 = new Elm327Adapter();
        await adapter2.InitializeAsync(transport2);
        var version2 = adapter2.DeviceVersion;
        var protocol2 = adapter2.ProtocolDescription;

        // Assert - both runs produce identical results
        await Assert.That(version1).IsEqualTo(version2);
        await Assert.That(protocol1).IsEqualTo(protocol2);
    }

    [Test]
    public async Task Elm327Adapter_WithSampleFixture_DetectsProtocol()
    {
        // Arrange
        var assembly = typeof(Elm327AdapterFixtureTests).Assembly;
        using var transport = await ReplayTransportFactory.FromResourceAsync(
            assembly,
            SampleSessionResource,
            new ReplayOptions { MatchingMode = ReplayMatchingMode.Any });

        await transport.ConnectAsync();
        var adapter = new Elm327Adapter();

        // Act
        await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(adapter.ProtocolDescription).IsNotNull();
        await Assert.That(adapter.ProtocolDescription).Contains("ISO 15765-4");
    }

    [Test]
    public async Task Elm327Adapter_WithSampleFixture_InitializesSuccessfully()
    {
        // Arrange
        var assembly = typeof(Elm327AdapterFixtureTests).Assembly;
        using var transport = await ReplayTransportFactory.FromResourceAsync(
            assembly,
            SampleSessionResource,
            new ReplayOptions { MatchingMode = ReplayMatchingMode.Any });

        await transport.ConnectAsync();
        var adapter = new Elm327Adapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(result).IsTrue();
        await Assert.That(adapter.DeviceVersion).Contains("ELM327");
        await Assert.That(adapter.ProtocolDescription).Contains("CAN");
    }

    [Test]
    public async Task ReplayTransport_WithSampleFixture_HasCorrectMetadata()
    {
        // Arrange
        var assembly = typeof(Elm327AdapterFixtureTests).Assembly;

        // Act
        using var transport = await ReplayTransportFactory.FromResourceAsync(
            assembly,
            SampleSessionResource);

        // Assert
        await Assert.That(transport.Session.Metadata.Protocol).Contains("CAN");
        await Assert.That(transport.Session.Metadata.AdapterVersion).Contains("ELM327");
        await Assert.That(transport.Session.Metadata.Description).Contains("Sample");
    }

    [Test]
    public async Task ReplayTransport_WithSampleFixture_LoadsAllEntries()
    {
        // Arrange
        var assembly = typeof(Elm327AdapterFixtureTests).Assembly;

        // Act
        using var transport = await ReplayTransportFactory.FromResourceAsync(
            assembly,
            SampleSessionResource);

        // Assert
        await Assert.That(transport.Session).IsNotNull();
        await Assert.That(transport.Session.Entries.Count).IsGreaterThan(20);
        await Assert.That(transport.Session.Metadata.DeviceName).IsEqualTo("Veepeak BLE+");
    }
}