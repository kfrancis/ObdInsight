using ObdInsight.Core.Adapters;

namespace ObdInsight.AdapterComplianceTests;

/// <summary>
/// Base class providing handshake compliance test implementations.
/// Adapter-specific test classes should inherit and provide the adapter factory.
/// </summary>
public abstract class HandshakeComplianceTestsBase
{
    /// <summary>
    /// Factory method to create the adapter under test.
    /// </summary>
    protected abstract IObdAdapter CreateAdapter();

    /// <summary>
    /// Creates a mock transport configured for successful initialization.
    /// Override to customize for specific adapter protocols.
    /// </summary>
    protected virtual MockTransport CreateSuccessfulInitTransport() =>
        MockTransportScenarios.CreateSuccessfulElm327Init();

    /// <summary>
    /// Creates a mock transport with no vehicle connected scenario.
    /// </summary>
    protected virtual MockTransport CreateNoVehicleTransport() =>
        MockTransportScenarios.CreateNoVehicleElm327Init();

    [Test]
    public async Task Handshake_WithValidResponses_InitializesSuccessfully()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task Handshake_AfterSuccess_IsInitializedReturnsTrue()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();

        // Act
        await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(adapter.IsInitialized).IsTrue();
    }

    [Test]
    public async Task Handshake_BeforeInit_IsInitializedReturnsFalse()
    {
        // Arrange
        var adapter = CreateAdapter();

        // Assert
        await Assert.That(adapter.IsInitialized).IsFalse();
    }

    [Test]
    public async Task Handshake_SlowDevice_StillCompletes()
    {
        // Arrange
        using var transport = new MockTransport { ResponseDelay = TimeSpan.FromMilliseconds(200) };
        await transport.ConnectAsync();

        // Queue slow responses
        transport.EnqueueResponse("\r\n\r\nELM327 v1.5\r\n\r\n>");
        for (var i = 0; i < 7; i++)
        {
            transport.EnqueueResponse("OK\r\n\r\n>");
        }
        transport.EnqueueResponse("4100BE1FA813\r\n\r\n>");
        transport.EnqueueResponse("AUTO, ISO 15765-4 CAN\r\n\r\n>");

        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task Handshake_NoVehicleConnected_StillInitializes()
    {
        // Arrange
        using var transport = CreateNoVehicleTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert - Adapter should still initialize (it works, just no vehicle)
        await Assert.That(result).IsTrue();
        await Assert.That(adapter.IsInitialized).IsTrue();
    }

    [Test]
    public async Task Handshake_SendsResetCommand_AsFirstCommand()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();

        // Act
        await adapter.InitializeAsync(transport);

        // Assert - First command should be a reset (ATZ for ELM327)
        await Assert.That(transport.SentCommands.Count).IsGreaterThan(0);
        var firstCommand = transport.SentCommands[0].Trim().TrimEnd('\r');
        await Assert.That(firstCommand.StartsWith("AT", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }

    [Test]
    public async Task Handshake_DisablesEcho_ForCleanResponses()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();

        // Act
        await adapter.InitializeAsync(transport);

        // Assert - Should send echo off command (ATE0 for ELM327)
        var echoOffSent = transport.SentCommands.Any(c =>
            c.Contains("ATE0", StringComparison.OrdinalIgnoreCase) ||
            c.Contains("E0", StringComparison.OrdinalIgnoreCase));
        await Assert.That(echoOffSent).IsTrue();
    }

    [Test]
    public async Task Handshake_WithCloneDevice_StillInitializes()
    {
        // Arrange - Some clones report different versions
        using var transport = new MockTransport();
        await transport.ConnectAsync();

        transport.EnqueueResponse("\r\n\r\nOBDII by www.xxxxx.com\r\n\r\n>");
        for (var i = 0; i < 7; i++)
        {
            transport.EnqueueResponse("OK\r\n\r\n>");
        }
        transport.EnqueueResponse("4100BE1FA813\r\n\r\n>");
        transport.EnqueueResponse("AUTO, ISO 15765-4 CAN\r\n\r\n>");

        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert - Should handle clone devices gracefully
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task Handshake_AdapterName_IsNotEmpty()
    {
        // Arrange
        var adapter = CreateAdapter();

        // Assert
        await Assert.That(adapter.Name).IsNotEmpty();
    }

    [Test]
    public async Task Handshake_SupportedDeviceNames_IsNotEmpty()
    {
        // Arrange
        var adapter = CreateAdapter();

        // Assert
        await Assert.That(adapter.SupportedDeviceNames.Length).IsGreaterThan(0);
    }
}