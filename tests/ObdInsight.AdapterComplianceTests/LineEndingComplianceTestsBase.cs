using ObdInsight.Core.Adapters;

namespace ObdInsight.AdapterComplianceTests;

/// <summary>
/// Base class providing line ending compliance test implementations.
/// Tests that adapters correctly handle various line ending formats.
/// </summary>
public abstract class LineEndingComplianceTestsBase
{
    /// <summary>
    /// Factory method to create the adapter under test.
    /// </summary>
    protected abstract IObdAdapter CreateAdapter();

    /// <summary>
    /// Creates a mock transport with custom init responses for line ending tests.
    /// </summary>
    private MockTransport CreateInitTransportWithLineEndings(string lineEnding)
    {
        var transport = new MockTransport();

        // Build responses with specified line ending
        transport.EnqueueResponse($"{lineEnding}{lineEnding}ELM327 v1.5{lineEnding}{lineEnding}>");
        transport.EnqueueResponse($"OK{lineEnding}{lineEnding}>");
        transport.EnqueueResponse($"OK{lineEnding}{lineEnding}>");
        transport.EnqueueResponse($"OK{lineEnding}{lineEnding}>");
        transport.EnqueueResponse($"OK{lineEnding}{lineEnding}>");
        transport.EnqueueResponse($"OK{lineEnding}{lineEnding}>");
        transport.EnqueueResponse($"OK{lineEnding}{lineEnding}>");
        transport.EnqueueResponse($"OK{lineEnding}{lineEnding}>");
        transport.EnqueueResponse($"4100BE1FA813{lineEnding}{lineEnding}>");
        transport.EnqueueResponse($"AUTO, ISO 15765-4 CAN{lineEnding}{lineEnding}>");

        return transport;
    }

    [Test]
    public async Task LineEnding_CarriageReturnOnly_Handled()
    {
        // Arrange - CR only (\r)
        using var transport = CreateInitTransportWithLineEndings("\r");
        await transport.ConnectAsync();
        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task LineEnding_LineFeedOnly_Handled()
    {
        // Arrange - LF only (\n)
        using var transport = CreateInitTransportWithLineEndings("\n");
        await transport.ConnectAsync();
        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task LineEnding_CrLf_Handled()
    {
        // Arrange - Standard CRLF (\r\n)
        using var transport = CreateInitTransportWithLineEndings("\r\n");
        await transport.ConnectAsync();
        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task LineEnding_Mixed_Handled()
    {
        // Arrange - Mixed line endings in same response
        using var transport = new MockTransport();
        await transport.ConnectAsync();

        transport.EnqueueResponse("\r\n\rELM327 v1.5\n\r\n>");
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
    public async Task LineEnding_ExtraWhitespace_Trimmed()
    {
        // Arrange - Extra spaces and tabs
        using var transport = new MockTransport();
        await transport.ConnectAsync();

        transport.EnqueueResponse("   \r\n\r\n  ELM327 v1.5  \r\n\r\n>");
        for (var i = 0; i < 7; i++)
        {
            transport.EnqueueResponse("  OK  \r\n\r\n>");
        }
        transport.EnqueueResponse("  4100BE1FA813  \r\n\r\n>");
        transport.EnqueueResponse("  AUTO, ISO 15765-4 CAN  \r\n\r\n>");

        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task LineEnding_MultipleConsecutive_Handled()
    {
        // Arrange - Multiple consecutive line breaks
        using var transport = new MockTransport();
        await transport.ConnectAsync();

        transport.EnqueueResponse("\r\n\r\n\r\n\r\nELM327 v1.5\r\n\r\n\r\n\r\n>");
        for (var i = 0; i < 7; i++)
        {
            transport.EnqueueResponse("OK\r\n\r\n\r\n>");
        }
        transport.EnqueueResponse("4100BE1FA813\r\n\r\n\r\n>");
        transport.EnqueueResponse("AUTO, ISO 15765-4 CAN\r\n\r\n\r\n>");

        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task LineEnding_NoLineBreaks_JustPrompt()
    {
        // Arrange - Minimal response with just data and prompt
        using var transport = new MockTransport();
        await transport.ConnectAsync();

        transport.EnqueueResponse("ELM327 v1.5>");
        for (var i = 0; i < 7; i++)
        {
            transport.EnqueueResponse("OK>");
        }
        transport.EnqueueResponse("4100BE1FA813>");
        transport.EnqueueResponse("AUTO, ISO 15765-4 CAN>");

        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task LineEnding_CommandResponse_StripsEchoCorrectly()
    {
        // Arrange - Response with echo (some devices keep echo on)
        using var transport = MockTransportScenarios.CreateSuccessfulElm327Init();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Response with echo of command
        transport.EnqueueResponse("010C\r\n410C1AF8\r\n\r\n>");

        // Act
        var command = ObdCommand.Create("010C");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
        // Value should not contain the echo
        await Assert.That(result.Value!.Contains("010C")).IsFalse();
    }

    [Test]
    public async Task LineEnding_ResponseWithTabs_Handled()
    {
        // Arrange - Response with tabs
        using var transport = new MockTransport();
        await transport.ConnectAsync();

        transport.EnqueueResponse("\r\n\r\nELM327\tv1.5\r\n\r\n>");
        for (var i = 0; i < 7; i++)
        {
            transport.EnqueueResponse("OK\r\n\r\n>");
        }
        transport.EnqueueResponse("4100BE1FA813\r\n\r\n>");
        transport.EnqueueResponse("AUTO,\tISO 15765-4 CAN\r\n\r\n>");

        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task LineEnding_NullBytesIgnored()
    {
        // Arrange - Response with null bytes (some adapters have buffer issues)
        using var transport = new MockTransport();
        await transport.ConnectAsync();

        transport.EnqueueResponse("\0\r\n\r\nELM327 v1.5\r\n\0\r\n>");
        for (var i = 0; i < 7; i++)
        {
            transport.EnqueueResponse("OK\r\n\r\n>");
        }
        transport.EnqueueResponse("4100BE1FA813\r\n\r\n>");
        transport.EnqueueResponse("AUTO, ISO 15765-4 CAN\r\n\r\n>");

        var adapter = CreateAdapter();

        // Act
        var result = await adapter.InitializeAsync(transport);

        // Assert - Should handle null bytes gracefully
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task LineEnding_DataResponse_ParsedCorrectly()
    {
        // Arrange
        using var transport = MockTransportScenarios.CreateSuccessfulElm327Init();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // RPM response with various line endings
        transport.EnqueueResponse("410C1AF8\r\n\r\n>");

        // Act
        var command = ObdCommand.Create("010C");
        var result = await adapter.SendCommandAsync(command);

        // Assert
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Value).Contains("410C");
    }
}
