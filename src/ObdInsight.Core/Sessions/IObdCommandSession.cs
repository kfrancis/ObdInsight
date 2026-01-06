using ObdInsight.Core.Adapters;
using ObdInsight.Core.Transports;

namespace ObdInsight.Core.Sessions;

/// <summary>
/// Command-oriented OBD session for ELM/STN-style adapters.
/// Manages the lifecycle of sending OBD commands and receiving responses.
/// </summary>
public interface IObdCommandSession : IDisposable
{
    bool IsInitialized { get; }
    IObdAdapter? Adapter { get; }
    IByteStreamTransport? Transport { get; }
    Task<bool> InitializeAsync(IObdAdapter adapter, IByteStreamTransport transport, CancellationToken cancellationToken = default);
    Task<ObdResponse> SendCommandAsync(ObdCommand command, CancellationToken cancellationToken = default);
    Task CloseAsync();
}
