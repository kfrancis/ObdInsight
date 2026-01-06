namespace ObdInsight.Core.Sessions;

/// <summary>
/// Raw CAN/ISO-TP session for direct frame-level communication.
/// Provides low-level access for vehicles requiring non-standard protocols.
/// </summary>
public interface ICanFrameSession : IDisposable
{
    bool IsActive { get; }
    event EventHandler<CanFrame>? FrameReceived;
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    Task SendFrameAsync(CanFrame frame, CancellationToken cancellationToken = default);
    Task<CanFrame> ReceiveFrameAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    void SubscribeToId(uint canId, EventHandler<CanFrame> handler);
    void UnsubscribeFromId(uint canId, EventHandler<CanFrame> handler);
}

/// <summary>
/// Represents a CAN frame for low-level communication.
/// </summary>
/// <param name="Id">CAN identifier (11-bit standard or 29-bit extended)</param>
/// <param name="Data">Frame payload data (up to 8 bytes for CAN 2.0)</param>
/// <param name="IsExtendedId">Whether this uses 29-bit extended identifier</param>
public record CanFrame(uint Id, byte[] Data, bool IsExtendedId = false)
{
    public static CanFrame Create(uint id, byte[] data) => new(id, data, false);
    public static CanFrame CreateExtended(uint id, byte[] data) => new(id, data, true);
}
