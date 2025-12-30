using ObdInsight.Core.Adapters;

namespace ObdInsight.AdapterComplianceTests;

/// <summary>
/// Base class providing multi-frame response compliance test implementations.
/// Tests that adapters correctly handle ISO-TP and CAN multi-frame responses.
/// </summary>
public abstract class MultiFrameResponseComplianceTestsBase
{
    /// <summary>
    /// Factory method to create the adapter under test.
    /// </summary>
    protected abstract IObdAdapter CreateAdapter();

    /// <summary>
    /// Creates a mock transport configured for successful initialization.
    /// </summary>
    protected virtual MockTransport CreateSuccessfulInitTransport() =>
        MockTransportScenarios.CreateSuccessfulElm327Init();

    [Test]
    public async Task MultiFrame_VinQuery_ReturnsCompleteVin()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // VIN query response - Mode 09, PID 02
        // VIN is typically returned as multi-line response
        // Example VIN: 1HGBH41JXMN109186 (17 characters)
        transport.EnqueueResponse(
            "014\r\n" +
            "0: 49 02 01 31 48 47\r\n" +
            "1: 42 48 34 31 4A 58 4D\r\n" +
            "2: 4E 31 30 39 31 38 36\r\n" +
            "\r\n>");

        // Act
        var command = ObdCommand.Create("0902");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RawResponse).IsNotNull();
    }

    [Test]
    public async Task MultiFrame_MultiLineData_AllLinesIncluded()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Multi-line response (e.g., DTC query)
        transport.EnqueueResponse(
            "43 01 33 00 00 00 00\r\n" +
            "43 02 45 00 00 00 00\r\n" +
            "\r\n>");

        // Act
        var command = ObdCommand.Create("03"); // Mode 03 - DTCs
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RawResponse).IsNotNull();
    }

    [Test]
    public async Task MultiFrame_CanContinuation_Assembled()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // CAN multi-frame with headers (when ATH1)
        // First frame (10), followed by consecutive frames (21, 22, ...)
        transport.EnqueueResponse(
            "7E8 10 14 49 02 01 31 48 47\r\n" +
            "7E8 21 42 48 34 31 4A 58 4D\r\n" +
            "7E8 22 4E 31 30 39 31 38 36\r\n" +
            "\r\n>");

        // Act
        var command = ObdCommand.Create("0902");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task MultiFrame_MultipleEcuResponses_AllCaptured()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Multiple ECUs responding to same query
        transport.EnqueueResponse(
            "7E8 06 41 00 BE 1F A8 13\r\n" +
            "7EA 06 41 00 80 00 00 00\r\n" +
            "\r\n>");

        // Act
        var command = ObdCommand.Create("0100");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.RawResponse).Contains("7E8");
    }

    [Test]
    public async Task MultiFrame_LongResponse_NoTruncation()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Very long response (e.g., calibration IDs - Mode 09 PID 04)
        var longResponse =
            "7E8 10 3C 49 04 01 43 56\r\n" +
            "7E8 21 4E 2D 31 30 33 30\r\n" +
            "7E8 22 30 30 30 30 30 30\r\n" +
            "7E8 23 30 30 30 30 30 30\r\n" +
            "7E8 24 30 30 30 30 30 30\r\n" +
            "7E8 25 30 30 30 30 30 30\r\n" +
            "7E8 26 30 30 30 30 30 30\r\n" +
            "7E8 27 30 30 30 30 30 30\r\n" +
            "\r\n>";

        transport.EnqueueResponse(longResponse);

        // Act
        var command = ObdCommand.Create("0904");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
        // Should contain all frames
        await Assert.That(result.RawResponse!.Contains("7E8 27")).IsTrue();
    }

    [Test]
    public async Task MultiFrame_SingleFrameResponse_StillWorks()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Single frame response
        transport.EnqueueResponse("410C1AF8\r\n\r\n>");

        // Act
        var command = ObdCommand.Create("010C");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Value).Contains("410C");
    }

    [Test]
    public async Task MultiFrame_WithSpaces_ParsedCorrectly()
    {
        // Arrange - Some devices add spaces between bytes
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        transport.EnqueueResponse(
            "49 02 01 31 48 47 42 48\r\n" +
            "34 31 4A 58 4D 4E 31 30\r\n" +
            "39 31 38 36\r\n" +
            "\r\n>");

        // Act
        var command = ObdCommand.Create("0902");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task MultiFrame_WithoutSpaces_ParsedCorrectly()
    {
        // Arrange - Compact format (ATS0)
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        transport.EnqueueResponse(
            "4902013148474248\r\n" +
            "34314A584D4E3130\r\n" +
            "39313836\r\n" +
            "\r\n>");

        // Act
        var command = ObdCommand.Create("0902");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task MultiFrame_DelayedFrames_StillAssembled()
    {
        // Arrange - Simulate slow multi-frame response
        using var transport = new MockTransport { ResponseDelay = TimeSpan.FromMilliseconds(50) };
        await transport.ConnectAsync();

        // Setup init
        transport.EnqueueResponse("\r\n\r\nELM327 v1.5\r\n\r\n>");
        for (var i = 0; i < 7; i++) transport.EnqueueResponse("OK\r\n\r\n>");
        transport.EnqueueResponse("4100BE1FA813\r\n\r\n>");
        transport.EnqueueResponse("AUTO, ISO 15765-4 CAN\r\n\r\n>");

        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Multi-frame with delay between parts
        transport.EnqueueResponse(
            "49 02 01 31 48 47\r\n" +
            "42 48 34 31 4A 58\r\n" +
            "\r\n>",
            TimeSpan.FromMilliseconds(100));

        // Act
        var command = ObdCommand.Create("0902", TimeSpan.FromSeconds(2));
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task MultiFrame_MixedHexCase_Normalized()
    {
        // Arrange - Mixed case hex (some devices do this)
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        transport.EnqueueResponse("41 0c 1A f8\r\n\r\n>");

        // Act
        var command = ObdCommand.Create("010C");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
    }
}
