namespace ObdInsight.Core.Protocols;

/// <summary>
///     Represents a parsed CAN frame received from monitoring mode.
/// </summary>
/// <param name="CanId">The CAN identifier (11-bit or 29-bit)</param>
/// <param name="Data">The data bytes of the CAN frame</param>
public readonly record struct RawCanFrame(int CanId, ReadOnlyMemory<byte> Data)
{
    public ObservationMetadata Observation { get; init; }
    /// <summary>
    ///     Gets the CAN ID as a hexadecimal string (3-digit for 11-bit IDs, 8-digit for 29-bit IDs).
    /// </summary>
    public string CanIdHex
    {
        get => CanId > 0x7FF ? $"{CanId:X8}" : $"{CanId:X3}";
    }

    /// <summary>
    ///     Gets a human-readable string representation of the frame.
    /// </summary>
    /// <returns>Format: "CAN_ID: BYTE1 BYTE2 BYTE3 ..."</returns>
    public override string ToString()
    {
        return $"{CanIdHex}: {BitConverter.ToString(Data.ToArray()).Replace("-", " ")}";
    }
}
