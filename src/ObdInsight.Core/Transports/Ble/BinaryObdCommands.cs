namespace ObdInsight.Core.Transports.Ble;

/// <summary>
/// Common binary OBD protocol commands and utilities.
/// These are typical for adapters using direct CAN frame access.
/// </summary>
public static class BinaryObdCommands
{
    /// <summary>
    /// ECU response address range end
    /// </summary>
    public const ushort EcuResponseEnd = 0x7EF;

    /// <summary>
    /// ECU response address range start (7E8-7EF)
    /// </summary>
    public const ushort EcuResponseStart = 0x7E8;

    /// <summary>
    /// Standard OBD-II functional broadcast address (7DF)
    /// </summary>
    public const ushort FunctionalBroadcastId = 0x7DF;

    /// <summary>
    /// Common test commands for probing binary protocol format.
    /// </summary>
    public static IReadOnlyList<(string Name, byte[] Data)> ProbeCommands =>
    [
        // Raw OBD-II service 01 PID 00 (supported PIDs)
        ("Raw 01 00", [0x01, 0x00]),

        // With ISO-TP length prefix
        ("ISO-TP 01 00", [0x02, 0x01, 0x00]),

        // Try with CAN ID prefix (7DF = functional broadcast)
        ("CAN+PID 7DF", [0x07, 0xDF, 0x02, 0x01, 0x00]),

        // Some adapters use length-prefixed proprietary framing
        ("Len+Cmd 3", [0x03, 0x01, 0x00, 0x00]),

        // AT passthrough (some binary protocols support this)
        ("AT passthrough ATI", [0x41, 0x54, 0x49, 0x0D]), // "ATI\r"

        // Simple ping/status commands
        ("Status 0x00", [0x00]),
        ("Ping 0xFF", [0xFF]),

        // Some adapters use STN-style binary
        ("STN style", [0x21, 0x01, 0x00]), // Request type + service + PID
    ];

    /// <summary>
    /// Build a raw CAN frame with ID prefix (some binary protocols use this format).
    /// </summary>
    /// <param name="canId">11-bit CAN ID</param>
    /// <param name="data">Data bytes (up to 8)</param>
    /// <returns>Frame bytes with ID prefix</returns>
    public static byte[] BuildCanFrame(ushort canId, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > 8)
            throw new ArgumentException("CAN data cannot exceed 8 bytes", nameof(data));

        var frame = new byte[data.Length + 2];
        frame[0] = (byte)(canId >> 8);
        frame[1] = (byte)(canId & 0xFF);
        Array.Copy(data, 0, frame, 2, data.Length);
        return frame;
    }

    /// <summary>
    /// Build a multi-PID request (up to 6 PIDs in one request).
    /// </summary>
    /// <param name="service">OBD service</param>
    /// <param name="pids">PIDs to request (1-6)</param>
    /// <returns>CAN data bytes for the request</returns>
    /// <exception cref="ArgumentException">If pids count is not 1-6</exception>
    public static byte[] BuildMultiPidRequest(byte service, params byte[] pids)
    {
        ArgumentNullException.ThrowIfNull(pids);
        if (pids.Length is 0 or > 6)
            throw new ArgumentException("Must request 1-6 PIDs", nameof(pids));

        var data = new byte[pids.Length + 2];
        data[0] = (byte)(pids.Length + 1); // ISO-TP length
        data[1] = service;
        Array.Copy(pids, 0, data, 2, pids.Length);
        return data;
    }

    /// <summary>
    /// Build a standard OBD-II PID request frame.
    /// Format: [Length][Service][PID] (ISO-TP single frame)
    /// </summary>
    /// <param name="service">OBD service (01=current data, 02=freeze frame, etc.)</param>
    /// <param name="pid">Parameter ID</param>
    /// <returns>CAN data bytes for the request</returns>
    public static byte[] BuildPidRequest(byte service, byte pid)
    {
        // Standard OBD-II uses ISO-TP single frame format:
        // [Length][Service][PID]
        return [0x02, service, pid];
    }

    /// <summary>
    /// Format bytes as hex string for display.
    /// </summary>
    public static string ToHexString(ReadOnlySpan<byte> data) =>
        BitConverter.ToString(data.ToArray());

    /// <summary>
    /// Try to interpret bytes as ASCII if they appear to be printable text.
    /// </summary>
    /// <param name="data">Data to interpret</param>
    /// <param name="ascii">ASCII string if interpretable</param>
    /// <returns>True if data appears to be ASCII text</returns>
    public static bool TryInterpretAsAscii(ReadOnlySpan<byte> data, out string ascii)
    {
        if (data.IsEmpty)
        {
            ascii = string.Empty;
            return false;
        }

        // Check if all bytes are printable ASCII or common control chars
        foreach (var b in data)
        {
            if (b is not ((>= 0x20 and < 0x7F) or 0x0D or 0x0A or 0x09))
            {
                ascii = string.Empty;
                return false;
            }
        }

        ascii = System.Text.Encoding.ASCII.GetString(data);
        return true;
    }
}