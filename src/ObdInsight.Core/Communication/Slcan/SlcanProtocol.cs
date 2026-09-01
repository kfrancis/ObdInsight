using System;
using System.Globalization;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Communication.Slcan;

/// <summary>
/// The Lawicel SLCAN ASCII line protocol, as spoken by CANable / CANable 2.0 hardware running
/// the `slcan` firmware (the alternative to candleLight, which is USB-native gs_usb and needs a
/// WinUSB driver on Windows; slcan enumerates as an ordinary virtual COM port instead).
///
/// Pure formatting and parsing - no I/O - so the whole protocol layer is testable without the
/// device present.
///
/// Why this matters architecturally: SLCAN is a CR-terminated ASCII line protocol, which is
/// structurally the same shape as the ELM327 text protocol this codebase already handles. A raw
/// CAN adapter therefore does NOT require a new I/O stack - only a different command vocabulary
/// and frame grammar.
///
/// Wire grammar for received frames:
///
///   t 1DB 8 0011223344556677 CR    standard (11-bit) classic frame
///   T 18DAF110 8 ...         CR    extended (29-bit) classic frame
///   d / D                          same, but CAN FD (CANable 2.0 firmware extension)
///   z / Z                          transmit acknowledgements
///
/// The DLC nibble is a *code*, not a byte count: for CAN FD, codes 9-15 map to 12, 16, 20, 24,
/// 32, 48 and 64 bytes. Treating it as a literal length silently truncates every FD frame
/// larger than 8 bytes.
/// </summary>
public static class SlcanProtocol
{
    /// <summary>Close the channel. Safe to send first - the device may have been left open.</summary>
    public const string Close = "C\r";

    /// <summary>
    /// Open the channel in LISTEN-ONLY mode. The transceiver does not even acknowledge frames,
    /// so the adapter cannot disturb the bus.
    ///
    /// This is a first-class protocol command, unlike the ELM327's <c>AT CSM</c>, whose polarity
    /// varies by firmware version and has to be verified empirically. On a powertrain bus that
    /// difference matters: here, listen-only is stated rather than hoped for.
    /// </summary>
    public const string OpenListenOnly = "L\r";

    /// <summary>Open the channel normally - CAN acknowledgements ARE transmitted.</summary>
    public const string OpenNormal = "O\r";

    /// <summary>Firmware version query.</summary>
    public const string Version = "V\r";

    /// <summary>
    /// Standard bitrate selector. The Leaf's three buses are all 500 kbit/s, which is
    /// <see cref="Bitrate500K"/>.
    /// </summary>
    public const string Bitrate500K = "S6\r";

    public const string Bitrate250K = "S5\r";
    public const string Bitrate125K = "S4\r";
    public const string Bitrate1M = "S8\r";

    /// <summary>
    /// CAN FD data-phase bitrate (CANable 2.0 extension). Only meaningful when the arbitration
    /// rate is already set; irrelevant for classic-CAN vehicles such as the Leaf.
    /// </summary>
    public const string FdDataBitrate2M = "Y2\r";

    /// <summary>
    /// Maps an SLCAN DLC code to an actual byte count. Codes 0-8 are literal; 9-15 are the CAN FD
    /// length codes. Getting this wrong truncates FD payloads rather than failing loudly, so it
    /// is kept explicit.
    /// </summary>
    public static int DlcToLength(int dlc) => dlc switch
    {
        <= 8 and >= 0 => dlc,
        9 => 12,
        10 => 16,
        11 => 20,
        12 => 24,
        13 => 32,
        14 => 48,
        15 => 64,
        _ => throw new ArgumentOutOfRangeException(nameof(dlc), dlc, "SLCAN DLC codes are 0-15"),
    };

    /// <summary>
    /// Attempts to parse one received SLCAN line into a CAN frame.
    ///
    /// Returns false for anything that is not a frame - version banners, bell/error responses,
    /// transmit acknowledgements, blank lines - rather than throwing, because a capture loop must
    /// keep running through adapter chatter.
    /// </summary>
    public static bool TryParseFrame(ReadOnlySpan<char> line, out RawCanFrame frame, out bool isCanFd)
    {
        frame = default;
        isCanFd = false;

        line = line.Trim();
        if (line.Length < 5)
        {
            return false;
        }

        int idLength;
        switch (line[0])
        {
            case 't': idLength = 3; break;                       // standard, classic
            case 'T': idLength = 8; break;                       // extended, classic
            case 'd': idLength = 3; isCanFd = true; break;       // standard, FD
            case 'D': idLength = 8; isCanFd = true; break;       // extended, FD
            default: return false;
        }

        if (line.Length < 1 + idLength + 1)
        {
            return false;
        }

        var idText = line.Slice(1, idLength);
        if (!int.TryParse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var canId))
        {
            return false;
        }

        var dlcChar = line[1 + idLength];
        if (!TryParseHexDigit(dlcChar, out var dlc))
        {
            return false;
        }

        var length = DlcToLength(dlc);
        var payloadText = line[(2 + idLength)..];

        // The device may append a timestamp when that mode is enabled; take only the payload and
        // ignore any trailing characters rather than rejecting the frame outright.
        if (payloadText.Length < length * 2)
        {
            return false;
        }

        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            if (!TryParseHexDigit(payloadText[i * 2], out var hi) ||
                !TryParseHexDigit(payloadText[i * 2 + 1], out var lo))
            {
                return false;
            }

            data[i] = (byte)((hi << 4) | lo);
        }

        frame = new RawCanFrame(canId, data);
        return true;
    }

    private static bool TryParseHexDigit(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1,
        };

        return value >= 0;
    }
}
