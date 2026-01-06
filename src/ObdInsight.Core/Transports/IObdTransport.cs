namespace ObdInsight.Core.Transports;

/// <summary>
/// Core transport interface for OBD communication.
/// Extends IByteStreamTransport with OBD-specific semantics.
/// </summary>
/// <remarks>
/// IObdTransport is maintained for backward compatibility and semantic clarity.
/// New code should prefer IByteStreamTransport for transport-agnostic implementations.
/// </remarks>
public interface IObdTransport : IByteStreamTransport
{
}