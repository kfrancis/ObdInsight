using ObdInsight.Core.Adapters;
using ObdInsight.Core.Transports;

namespace ObdInsight.AdapterComplianceTests;

/// <summary>
/// Defines the compliance tests that every OBD adapter plugin must pass.
/// Implementing this interface ensures adapters handle all common scenarios correctly.
/// </summary>
/// <remarks>
/// Compliance categories:
/// - Handshake: Proper initialization sequence
/// - Timeout: Correct timeout handling
/// - Line endings: Edge cases with \r, \n, \r\n
/// - Multi-frame: ISO-TP and CAN multi-frame responses
/// - Error normalization: Common error patterns ("NO DATA", "?", "CAN ERROR", etc.)
/// </remarks>
public interface IAdapterComplianceTests
{
    /// <summary>
    /// Factory method to create the adapter under test.
    /// </summary>
    IObdAdapter CreateAdapter();

    #region Handshake Sequence Tests

    /// <summary>
    /// Adapter should complete initialization with valid handshake responses.
    /// </summary>
    Task Handshake_WithValidResponses_InitializesSuccessfully();

    /// <summary>
    /// Adapter should set IsInitialized to true after successful init.
    /// </summary>
    Task Handshake_AfterSuccess_IsInitializedReturnsTrue();

    /// <summary>
    /// Adapter should extract device version from reset response.
    /// </summary>
    Task Handshake_ResetResponse_ExtractsDeviceVersion();

    /// <summary>
    /// Adapter should handle slow device responses during handshake.
    /// </summary>
    Task Handshake_SlowDevice_StillCompletes();

    /// <summary>
    /// Adapter should handle UNABLE TO CONNECT during handshake (no vehicle).
    /// </summary>
    Task Handshake_NoVehicleConnected_StillInitializes();

    #endregion

    #region Timeout Behavior Tests

    /// <summary>
    /// Adapter should respect command timeout settings.
    /// </summary>
    Task Timeout_CommandTimeout_IsRespected();

    /// <summary>
    /// Adapter should return failure on command timeout.
    /// </summary>
    Task Timeout_OnExpiry_ReturnsFailure();

    /// <summary>
    /// Adapter should support custom timeout per command.
    /// </summary>
    Task Timeout_CustomTimeout_IsUsed();

    /// <summary>
    /// Adapter should handle immediate timeout (zero delay response).
    /// </summary>
    Task Timeout_ImmediateResponse_Succeeds();

    #endregion

    #region Line Ending Edge Cases

    /// <summary>
    /// Adapter should handle CR only line endings.
    /// </summary>
    Task LineEnding_CarriageReturnOnly_Handled();

    /// <summary>
    /// Adapter should handle LF only line endings.
    /// </summary>
    Task LineEnding_LineFeedOnly_Handled();

    /// <summary>
    /// Adapter should handle CRLF line endings.
    /// </summary>
    Task LineEnding_CrLf_Handled();

    /// <summary>
    /// Adapter should handle mixed line endings in response.
    /// </summary>
    Task LineEnding_Mixed_Handled();

    /// <summary>
    /// Adapter should handle extra whitespace around responses.
    /// </summary>
    Task LineEnding_ExtraWhitespace_Trimmed();

    /// <summary>
    /// Adapter should handle multiple consecutive line breaks.
    /// </summary>
    Task LineEnding_MultipleConsecutive_Handled();

    #endregion

    #region Multi-Frame Response Tests

    /// <summary>
    /// Adapter should handle ISO-TP multi-frame responses (VIN query).
    /// </summary>
    Task MultiFrame_VinQuery_ReturnsCompleteVin();

    /// <summary>
    /// Adapter should handle multi-line hex data responses.
    /// </summary>
    Task MultiFrame_MultiLineData_Concatenated();

    /// <summary>
    /// Adapter should handle CAN multi-frame with continuation frames.
    /// </summary>
    Task MultiFrame_CanContinuation_Assembled();

    /// <summary>
    /// Adapter should handle multiple ECU responses.
    /// </summary>
    Task MultiFrame_MultipleEcuResponses_AllCaptured();

    #endregion

    #region Error Normalization Tests

    /// <summary>
    /// Adapter should normalize "NO DATA" error.
    /// </summary>
    Task Error_NoData_NormalizedToFailure();

    /// <summary>
    /// Adapter should normalize "?" (unknown command) error.
    /// </summary>
    Task Error_QuestionMark_NormalizedToFailure();

    /// <summary>
    /// Adapter should normalize "CAN ERROR" response.
    /// </summary>
    Task Error_CanError_NormalizedToFailure();

    /// <summary>
    /// Adapter should normalize "UNABLE TO CONNECT" error.
    /// </summary>
    Task Error_UnableToConnect_NormalizedToFailure();

    /// <summary>
    /// Adapter should normalize "BUS INIT: ...ERROR" response.
    /// </summary>
    Task Error_BusInitError_NormalizedToFailure();

    /// <summary>
    /// Adapter should normalize "STOPPED" response.
    /// </summary>
    Task Error_Stopped_NormalizedToFailure();

    /// <summary>
    /// Adapter should normalize "BUFFER FULL" response.
    /// </summary>
    Task Error_BufferFull_NormalizedToFailure();

    /// <summary>
    /// Adapter should normalize "LV RESET" response.
    /// </summary>
    Task Error_LvReset_NormalizedToFailure();

    /// <summary>
    /// Adapter should normalize "ERR" prefixed responses.
    /// </summary>
    Task Error_ErrPrefix_NormalizedToFailure();

    /// <summary>
    /// Adapter should normalize "DATA ERROR" response.
    /// </summary>
    Task Error_DataError_NormalizedToFailure();

    /// <summary>
    /// Adapter should not treat "OK" as an error.
    /// </summary>
    Task Error_OkResponse_IsSuccess();

    #endregion
}

/// <summary>
/// Compliance test result summary.
/// </summary>
public record ComplianceTestResult
{
    /// <summary>Category of the test (Handshake, Timeout, etc.)</summary>
    public required string Category { get; init; }

    /// <summary>Name of the test</summary>
    public required string TestName { get; init; }

    /// <summary>Whether the test passed</summary>
    public required bool Passed { get; init; }

    /// <summary>Error message if failed</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Duration of the test</summary>
    public TimeSpan Duration { get; init; }
}
