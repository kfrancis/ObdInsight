using ObdInsight.Core.Adapters;

namespace ObdInsight.AdapterComplianceTests;

/// <summary>
/// Base class providing error normalization compliance test implementations.
/// Tests that adapters correctly recognize and normalize common error responses.
/// </summary>
public abstract class ErrorNormalizationComplianceTestsBase
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

    private async Task<ObdResponse> SendCommandWithErrorResponse(string errorResponse)
    {
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        transport.EnqueueResponse($"{errorResponse}\r\n\r\n>");

        var command = ObdCommand.Create("010C");
        return await adapter.SendCommandAsync(command);
    }

    [Test]
    public async Task Error_NoData_NormalizedToFailure()
    {
        // Act
        var result = await SendCommandWithErrorResponse("NO DATA");

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
        await Assert.That(result.RawResponse).Contains("NO DATA");
    }

    [Test]
    public async Task Error_QuestionMark_NormalizedToFailure()
    {
        // Act
        var result = await SendCommandWithErrorResponse("?");

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Error_CanError_NormalizedToFailure()
    {
        // Act
        var result = await SendCommandWithErrorResponse("CAN ERROR");

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Error_UnableToConnect_NormalizedToFailure()
    {
        // Act
        var result = await SendCommandWithErrorResponse("UNABLE TO CONNECT");

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Error_BusInitError_NormalizedToFailure()
    {
        // Act
        var result = await SendCommandWithErrorResponse("BUS INIT: ...ERROR");

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Error_BusInitOk_IsSuccess()
    {
        // Arrange - BUS INIT: OK is not an error
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // BUS INIT: OK followed by actual data
        transport.EnqueueResponse("BUS INIT: OK\r\n410C1AF8\r\n\r\n>");

        // Act
        var result = await adapter.SendCommandAsync(ObdCommand.Create("010C"));

        // Assert - Should succeed (BUS INIT: OK is informational)
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task Error_Stopped_NormalizedToFailure()
    {
        // Act
        var result = await SendCommandWithErrorResponse("STOPPED");

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Error_BufferFull_NormalizedToFailure()
    {
        // Act
        var result = await SendCommandWithErrorResponse("BUFFER FULL");

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Error_LvReset_NormalizedToFailure()
    {
        // LV RESET = Low Voltage Reset (adapter power issue)
        // Act
        var result = await SendCommandWithErrorResponse("LV RESET");

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Error_ErrPrefix_NormalizedToFailure()
    {
        // Some adapters use ERR prefix
        // Act
        var result = await SendCommandWithErrorResponse("ERR94");

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Error_DataError_NormalizedToFailure()
    {
        // Act
        var result = await SendCommandWithErrorResponse("DATA ERROR");

        // Assert
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Error_OkResponse_IsSuccess()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // AT command returns OK
        transport.EnqueueResponse("OK\r\n\r\n>");

        // Act
        var result = await adapter.SendCommandAsync(ObdCommand.Create("ATH0"));

        // Assert
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task Error_ValidHexData_IsSuccess()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        transport.EnqueueResponse("410C1AF8\r\n\r\n>");

        // Act
        var result = await adapter.SendCommandAsync(ObdCommand.Create("010C"));

        // Assert
        await Assert.That(result.Success).IsTrue();
    }

    [Test]
    public async Task Error_FbError_NormalizedToFailure()
    {
        // FB ERROR = Flow control error (CAN)
        // Act
        var result = await SendCommandWithErrorResponse("FB ERROR");

        // Assert
        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Error_RxError_NormalizedToFailure()
    {
        // RX ERROR = Receive error
        // Act
        var result = await SendCommandWithErrorResponse("RX ERROR");

        // Assert
        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Error_ActAlert_NormalizedToFailure()
    {
        // ACT ALERT = Activity alert (no bus activity)
        // Act
        var result = await SendCommandWithErrorResponse("ACT ALERT");

        // Assert
        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Error_LpAlert_NormalizedToFailure()
    {
        // LP ALERT = Low power alert
        // Act
        var result = await SendCommandWithErrorResponse("LP ALERT");

        // Assert
        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Error_NoDataWithPrefix_NormalizedToFailure()
    {
        // Some responses have protocol prefix
        // Act
        var result = await SendCommandWithErrorResponse("7E8 NO DATA");

        // Assert
        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Error_SearchingProtocol_NormalizedToFailure()
    {
        // SEARCHING... means adapter is still looking for protocol
        // Act
        var result = await SendCommandWithErrorResponse("SEARCHING...");

        // Assert
        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Error_EmptyResponse_NormalizedToFailure()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        // Empty response (just prompt)
        transport.EnqueueResponse("\r\n\r\n>");

        // Act
        var result = await adapter.SendCommandAsync(ObdCommand.Create("010C"));

        // Assert
        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Error_ErrorMessagePreservesRawResponse()
    {
        // Arrange
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        transport.EnqueueResponse("CAN ERROR\r\n\r\n>");

        // Act
        var result = await adapter.SendCommandAsync(ObdCommand.Create("010C"));

        // Assert
        await Assert.That(result.RawResponse).IsNotNull();
    }

    [Test]
    public async Task Error_MixedCaseError_StillDetected()
    {
        // Arrange - error in different case
        using var transport = CreateSuccessfulInitTransport();
        await transport.ConnectAsync();
        var adapter = CreateAdapter();
        await adapter.InitializeAsync(transport);

        transport.EnqueueResponse("No Data\r\n\r\n>");

        // Act
        var result = await adapter.SendCommandAsync(ObdCommand.Create("010C"));

        // Assert - Should still detect as error regardless of case
        await Assert.That(result.Success).IsFalse();
    }

    [Test]
    public async Task Error_NegativeResponseCode_NormalizedToFailure()
    {
        // UDS negative response: 7F <service> <NRC>
        // 7F 01 12 = Service 01, NRC 12 (subFunctionNotSupported)
        // Act
        var result = await SendCommandWithErrorResponse("7F 01 12");

        // Assert
        await Assert.That(result.Success).IsFalse();
    }
}
