using ObdInsight.Core.Adapters;
using ObdInsight.Drivers.Adapters.Elm327;

namespace ObdInsight.AdapterComplianceTests;

/// <summary>
/// Compliance tests for the ELM327 adapter.
/// All ELM327-compatible adapters must pass these tests.
/// </summary>
/// <remarks>
/// This test suite verifies:
/// - Correct handshake/initialization sequence
/// - Proper timeout handling
/// - Line ending normalization
/// - Multi-frame response assembly
/// - Error response detection and normalization
/// </remarks>
[InheritsTests]
public class Elm327HandshakeComplianceTests : HandshakeComplianceTestsBase
{
    protected override IObdAdapter CreateAdapter() => new Elm327Adapter();

    [Test]
    public async Task Handshake_Elm327Specific_ExtractsVersionCorrectly()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = (Elm327Adapter)CreateAdapter();

        // Act
        await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(adapter.DeviceVersion).Contains("ELM327");
    }

    [Test]
    public async Task Handshake_Elm327Specific_DetectsProtocol()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = (Elm327Adapter)CreateAdapter();

        // Act
        await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(adapter.ProtocolDescription).IsNotNull();
        await Assert.That(adapter.ProtocolDescription).Contains("CAN");
    }

    [Test]
    public async Task Handshake_Elm327Specific_LogEventsFired()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = (Elm327Adapter)CreateAdapter();

        var logMessages = new List<string>();
        adapter.Log += (_, e) => logMessages.Add(e.Message);

        // Act
        await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(logMessages.Count).IsGreaterThan(0);
    }
}

/// <summary>
/// Timeout compliance tests for ELM327 adapter.
/// </summary>
[InheritsTests]
public class Elm327TimeoutComplianceTests : TimeoutComplianceTestsBase
{
    protected override IObdAdapter CreateAdapter() => new Elm327Adapter();
}

/// <summary>
/// Line ending compliance tests for ELM327 adapter.
/// </summary>
[InheritsTests]
public class Elm327LineEndingComplianceTests : LineEndingComplianceTestsBase
{
    protected override IObdAdapter CreateAdapter() => new Elm327Adapter();
}

/// <summary>
/// Multi-frame response compliance tests for ELM327 adapter.
/// </summary>
[InheritsTests]
public class Elm327MultiFrameComplianceTests : MultiFrameResponseComplianceTestsBase
{
    protected override IObdAdapter CreateAdapter() => new Elm327Adapter();
}

/// <summary>
/// Error normalization compliance tests for ELM327 adapter.
/// </summary>
[InheritsTests]
public class Elm327ErrorNormalizationComplianceTests : ErrorNormalizationComplianceTestsBase
{
    protected override IObdAdapter CreateAdapter() => new Elm327Adapter();

    [Test]
    public async Task Error_Elm327Specific_QuestionMarkForUnknownAt()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Unknown AT command returns ?
        transport.EnqueueResponse("?\r\n\r\n>");

        // Act
        var result = await adapter.SendCommandAsync(ObdCommand.Create("ATXYZ"));

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).Contains("Unknown");
    }
}

/// <summary>
/// Combined compliance test runner for ELM327 adapter.
/// Use this for quick validation that an adapter passes all compliance categories.
/// </summary>
public class Elm327FullComplianceTests
{
    [Test]
    public async Task Elm327Adapter_FullCompliance_PassesAllCategories()
    {
        var adapter = new Elm327Adapter();

        // Verify adapter properties
        await Assert.That(adapter.Name).IsEqualTo("ELM327");
        await Assert.That(adapter.SupportedDeviceNames.Length).IsGreaterThan(0);
        await Assert.That(adapter.IsInitialized).IsFalse();

        // Verify initialization
        using var transport = MockTransportScenarios.CreateSuccessfulElm327Init();
        await transport.ConnectAsync();

        var initResult = await adapter.InitializeAsync(transport);

        await Assert.That(initResult).IsTrue();
        await Assert.That(adapter.IsInitialized).IsTrue();
        await Assert.That(adapter.DeviceVersion).IsNotNull();
        await Assert.That(adapter.ProtocolDescription).IsNotNull();

        // Verify command handling
        transport.EnqueueResponse("410C1AF8\r\n\r\n>");
        var cmdResult = await adapter.SendCommandAsync(ObdCommand.Create("010C"));

        await Assert.That(cmdResult.Success).IsTrue();
        await Assert.That(cmdResult.Value).IsNotNull();

        // Verify error handling
        transport.EnqueueResponse("NO DATA\r\n\r\n>");
        var errorResult = await adapter.SendCommandAsync(ObdCommand.Create("010C"));

        await Assert.That(errorResult.Success).IsFalse();
        await Assert.That(errorResult.Error).IsNotNull();
    }
}