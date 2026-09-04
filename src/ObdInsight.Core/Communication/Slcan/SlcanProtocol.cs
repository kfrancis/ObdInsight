using System.Globalization;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Communication.Slcan;

/// <summary>
///     The SLCAN ASCII line protocol family (Lawicel CANUSB grammar and the CANable firmware
///     dialects of it). Pure formatting and parsing - no I/O - so the whole protocol layer is
///     testable without a device present.
///     Why this matters architecturally: SLCAN is a CR-terminated ASCII line protocol, which is
///     structurally the same shape as the ELM327 text protocol this codebase already handles. A raw
///     CAN adapter therefore does NOT require a new I/O stack - only a different command vocabulary
///     and frame grammar.
///     Wire grammar for received frames:
///     t 1DB 8 0011223344556677 CR    standard (11-bit) classic frame
///     T 18DAF110 8 ...         CR    extended (29-bit) classic frame
///     d / D                          same, but CAN FD without bit-rate switch
///     b / B                          CAN FD with bit-rate switch (BRS) - CANable 2.0 / ElmüSoft
///     r / R                          remote frames (no payload; not surfaced as data frames)
///     z / Z                          Lawicel transmit acknowledgements
///     The DLC nibble is a *code*, not a byte count: for CAN FD, codes 9-15 map to 12, 16, 20, 24,
///     32, 48 and 64 bytes. Treating it as a literal length silently truncates every FD frame
///     larger than 8 bytes.
///     Dialect differences that matter (see <see cref="SlcanDialect" />): listen-only is <c>L</c>
///     on Lawicel but <c>M1</c>+<c>O</c> on CANable; CANable stock firmware never acknowledges a
///     command; <c>S7</c> is 800 kbit/s on Lawicel and 750 kbit/s on CANable.
/// </summary>
public static class SlcanProtocol
{
    /// <summary>Close the channel. Safe to send first - the device may have been left open.</summary>
    public const string Close = "C\r";

    /// <summary>
    ///     Lawicel: open the channel in LISTEN-ONLY mode. The transceiver does not even
    ///     acknowledge frames, so the adapter cannot disturb the bus.
    ///     <b>Ignored by CANable stock firmware</b> (the channel stays closed); use
    ///     <see cref="SilentMode" /> followed by <see cref="OpenNormal" /> there. Prefer
    ///     <see cref="OpenCommands" /> over choosing by hand.
    /// </summary>
    public const string OpenListenOnly = "L\r";

    /// <summary>Open the channel normally - CAN acknowledgements ARE transmitted.</summary>
    public const string OpenNormal = "O\r";

    /// <summary>
    ///     CANable / ElmüSoft: select silent (listen-only) mode. Must precede <see cref="OpenNormal" />;
    ///     the firmware applies it at open time. On a Lawicel device <c>M</c> sets the acceptance
    ///     code instead and this string is rejected as malformed (harmless BEL).
    /// </summary>
    public const string SilentMode = "M1\r";

    /// <summary>CANable / ElmüSoft: select normal mode (the firmware default).</summary>
    public const string NormalMode = "M0\r";

    /// <summary>Firmware version query. The reply identifies the dialect - see <see cref="DetectDialect" />.</summary>
    public const string Version = "V\r";

    /// <summary>
    ///     CANable / ElmüSoft: report the error register (bus-off, error-passive, overruns).
    ///     Reply is free text, e.g. <c>CANable Error Register: 0</c>.
    /// </summary>
    public const string ErrorRegister = "E\r";

    /// <summary>
    ///     Standard bitrate selector. The Leaf's three buses are all 500 kbit/s, which is
    ///     <see cref="Bitrate500K" />.
    /// </summary>
    public const string Bitrate500K = "S6\r";

    public const string Bitrate250K = "S5\r";
    public const string Bitrate125K = "S4\r";
    public const string Bitrate1M = "S8\r";

    /// <summary>CANable-only: 83.3 kbit/s. Out of range on Lawicel (which stops at <c>S8</c>).</summary>
    public const string Bitrate83K = "S9\r";

    /// <summary>
    ///     CAN FD data-phase bitrate (CANable 2.0 extension). Only meaningful when the arbitration
    ///     rate is already set; irrelevant for classic-CAN vehicles such as the Leaf.
    /// </summary>
    public const string FdDataBitrate2M = "Y2\r";

    public const string FdDataBitrate5M = "Y5\r";

    /// <summary>
    ///     The <c>S</c> command for a nominal bitrate in kbit/s. Only rates whose <c>S</c> code
    ///     means the same thing on every dialect are accepted; 750/800 kbit/s (<c>S7</c>) is
    ///     deliberately absent because the two firmwares disagree about it.
    /// </summary>
    public static string BitrateCommand(int kilobitsPerSecond) => kilobitsPerSecond switch
    {
        10 => "S0\r",
        20 => "S1\r",
        50 => "S2\r",
        100 => "S3\r",
        125 => Bitrate125K,
        250 => Bitrate250K,
        500 => Bitrate500K,
        1000 => Bitrate1M,
        _ => throw new ArgumentOutOfRangeException(
            nameof(kilobitsPerSecond),
            kilobitsPerSecond,
            "Supported SLCAN nominal bitrates: 10, 20, 50, 100, 125, 250, 500, 1000 kbit/s")
    };

    /// <summary>
    ///     The command sequence that opens the channel in the requested mode on the given
    ///     dialect. This is the one place the listen-only difference lives.
    /// </summary>
    /// <remarks>
    ///     <see cref="SlcanDialect.Unknown" /> gets the Lawicel sequence: <c>L</c> is the only
    ///     listen-only request that is harmless everywhere. On a CANable it is ignored and the
    ///     channel stays closed - no frames, but also no acknowledgements on the bus. The
    ///     alternative (<c>M1</c>+<c>O</c>) would open a Lawicel device in NORMAL mode, which is
    ///     the wrong failure direction on a powertrain bus.
    /// </remarks>
    public static IReadOnlyList<string> OpenCommands(SlcanDialect dialect, bool listenOnly) => dialect switch
    {
        SlcanDialect.Canable or SlcanDialect.ElmueSoft =>
            [listenOnly ? SilentMode : NormalMode, OpenNormal],
        _ => [listenOnly ? OpenListenOnly : OpenNormal]
    };

    /// <summary>
    ///     Classifies a device by its reply to <see cref="Version" />. Pure string inspection,
    ///     so the detection rules are unit-testable against captured banners.
    /// </summary>
    /// <remarks>
    ///     Known replies (2026-09-03):
    ///     <list type="bullet">
    ///         <item>canable2-fw: <c>16e7497-dirty github.com/normaldotcom/canable2.git</c> (captured from hardware)</item>
    ///         <item>cantact-fw (CANable 1.0): same shape, <c>GIT_VERSION " " GIT_REMOTE</c></item>
    ///         <item>
    ///             ElmüSoft slcan 2.5: <c>+Board: Multiboard\tMCU: STM32G431\tDevID: 1128\tFirmware: 2492419\tSlcan: 105\t...</c>
    ///             (captured from hardware; leading <c>+</c>, tab-separated <c>Key: Value</c> fields). This
    ///             firmware ACKs with CR/BEL and rejects <see cref="ErrorRegister" /> with BEL.
    ///         </item>
    ///         <item>Lawicel CANUSB: <c>V1013</c> (hardware 10, software 13)</item>
    ///     </list>
    /// </remarks>
    public static SlcanDialect DetectDialect(ReadOnlySpan<char> versionReply)
    {
        var text = versionReply.Trim();
        if (text.IsEmpty)
        {
            return SlcanDialect.Unknown;
        }

        // ElmüSoft first: its banner can mention "CANable" too, and it is the one that honours
        // acknowledgements, so misclassifying it as stock CANable would only cost features,
        // whereas the reverse would make us expect ACKs that never come.
        if (text.Contains('\t') && (ContainsIgnoreCase(text, "Slcan") || ContainsIgnoreCase(text, "Board")))
        {
            return SlcanDialect.ElmueSoft;
        }

        if (ContainsIgnoreCase(text, "canable") || ContainsIgnoreCase(text, "cantact") ||
            ContainsIgnoreCase(text, "normaldotcom"))
        {
            return SlcanDialect.Canable;
        }

        // Lawicel: 'V' followed by four digits (hardware + software version).
        if (text.Length >= 5 && text[0] == 'V' && AllDigits(text.Slice(1, 4)))
        {
            return SlcanDialect.Lawicel;
        }

        return SlcanDialect.Unknown;
    }

    /// <summary>
    ///     Maps an SLCAN DLC code to an actual byte count. Codes 0-8 are literal; 9-15 are the CAN FD
    ///     length codes. Getting this wrong truncates FD payloads rather than failing loudly, so it
    ///     is kept explicit.
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
        _ => throw new ArgumentOutOfRangeException(nameof(dlc), dlc, "SLCAN DLC codes are 0-15")
    };

    /// <summary>
    ///     Attempts to parse one received SLCAN line into a CAN frame.
    ///     Returns false for anything that is not a data frame - version banners, bell/error
    ///     responses, transmit acknowledgements, remote frames, blank lines - rather than throwing,
    ///     because a capture loop must keep running through adapter chatter.
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
            case 't': idLength = 3; break; // standard, classic
            case 'T': idLength = 8; break; // extended, classic
            case 'd':
            case 'b': // FD with bit-rate switch: same payload grammar, faster data phase on the wire
                idLength = 3;
                isCanFd = true;
                break;
            case 'D':
            case 'B':
                idLength = 8;
                isCanFd = true;
                break;
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

    private static bool ContainsIgnoreCase(ReadOnlySpan<char> text, string needle) =>
        text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool AllDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseHexDigit(char c, out int value)
    {
        value = c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => -1
        };

        return value >= 0;
    }
}
