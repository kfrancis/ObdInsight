namespace ObdInsight.Telemetry;

/// <summary>
///     A raw CAN frame captured while <see cref="IRawCanMonitor" /> is streaming ATMA
///     monitor-mode output, stamped with the wall-clock time it was received.
/// </summary>
/// <param name="Timestamp">When the frame was received from the adapter.</param>
/// <param name="CanId">The CAN identifier (11-bit or 29-bit).</param>
/// <param name="Data">The frame's data bytes (0-8).</param>
public readonly record struct RawCanFrame(DateTimeOffset Timestamp, int CanId, byte[] Data);
