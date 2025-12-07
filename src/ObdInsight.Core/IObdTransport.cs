namespace ObdInsight.Core
{
    public interface IObdTransport : IDisposable
    {
        string Name { get; }
        bool IsConnected { get; }

        Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
        Task DisconnectAsync();

        Task WriteAsync(string data, CancellationToken cancellationToken = default);
        Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
        Task<string> ReadUntilAsync(string terminator, TimeSpan timeout, CancellationToken cancellationToken = default);

        // For diagnostics/logging
        event EventHandler<string>? DataReceived;
        event EventHandler<string>? DataSent;
    }

    public interface IObdAdapter
    {
        string Name { get; }
        string[] SupportedDeviceNames { get; } // For auto-detection

        Task<bool> InitializeAsync(IObdTransport transport, CancellationToken cancellationToken = default);
        Task<ObdResponse> SendCommandAsync(ObdCommand command, CancellationToken cancellationToken = default);
        Task ResetAsync();
    }

    public record ObdCommand(string Command, TimeSpan? CustomTimeout = null);

    public record ObdResponse(bool Success, string? Value, string? RawResponse, string? Error);
}
