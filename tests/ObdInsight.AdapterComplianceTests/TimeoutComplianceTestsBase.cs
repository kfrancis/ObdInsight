using ObdInsight.Core.Adapters;

namespace ObdInsight.AdapterComplianceTests;

/// <summary>
/// Base class providing timeout compliance test implementations.
/// Tests that adapters correctly handle timeout scenarios.
/// </summary>
public abstract class TimeoutComplianceTestsBase
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
    public async Task Timeout_CommandTimeout_ReturnsFailure()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Don't queue any response - should timeout
        transport.ClearResponses();
        transport.ThrowOnNoResponse = true;

        // Act
        var command = ObdCommand.Create("010C", TimeSpan.FromMilliseconds(100));
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Timeout_ImmediateResponse_Succeeds()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Queue immediate response
        transport.EnqueueResponse("410C1AF8\r\n\r\n>", TimeSpan.Zero);

        // Act
        var command = ObdCommand.Create("010C");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task Timeout_DefaultTimeout_IsReasonable()
    {
        // Assert - Default timeout should be between 1-10 seconds
        await Assert.That(ObdCommand.DefaultTimeout.TotalSeconds).IsGreaterThanOrEqualTo(1);
        await Assert.That(ObdCommand.DefaultTimeout.TotalSeconds).IsLessThanOrEqualTo(10);
    }

    [Test]
    public async Task Timeout_ResponseJustBeforeTimeout_Succeeds()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Queue response that arrives just before timeout
        transport.EnqueueResponse("410C1AF8\r\n\r\n>", TimeSpan.FromMilliseconds(100));

        // Act - use timeout slightly longer than delay
        var command = ObdCommand.Create("010C", TimeSpan.FromMilliseconds(500));
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task Timeout_PartialResponse_EventuallyTimesOut()
    {
        // Arrange
        using var transport = new MockTransport { ThrowOnNoResponse = true };
        await transport.ConnectAsync();

        // Setup for init
        transport.EnqueueResponse("\r\n\r\nELM327 v1.5\r\n\r\n>");
        for (var i = 0; i < 7; i++) transport.EnqueueResponse("OK\r\n\r\n>");
        transport.EnqueueResponse("4100BE1FA813\r\n\r\n>");
        transport.EnqueueResponse("AUTO, ISO 15765-4 CAN\r\n\r\n>");

        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Queue partial response (no terminator)
        transport.EnqueueResponse("410C", TimeSpan.Zero);

        // Act
        var command = ObdCommand.Create("010C", TimeSpan.FromMilliseconds(100));
        var result = await adapter.SendCommandAsync(command);

        // Assert - should fail due to incomplete response
        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Timeout_MultipleCommandsSequentially_EachHasOwnTimeout()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Queue responses for multiple commands
        transport.EnqueueResponse("410C1AF8\r\n\r\n>", TimeSpan.Zero);
        transport.EnqueueResponse("410D00\r\n\r\n>", TimeSpan.Zero);

        // Act
        var cmd1 = ObdCommand.Create("010C", TimeSpan.FromSeconds(1));
        var cmd2 = ObdCommand.Create("010D", TimeSpan.FromSeconds(2));

        var result1 = await adapter.SendCommandAsync(cmd1);
        var result2 = await adapter.SendCommandAsync(cmd2);

        // Assert
        await Assert.That(result1.Success).IsTrue();
        await Assert.That(result2.Success).IsTrue();
    }
}
