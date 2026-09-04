namespace ObdInsight.Core.Communication.Slcan;

/// <summary>
///     Which flavour of SLCAN the device on the other end speaks. "SLCAN" is a family, not a
///     standard: the command letters overlap, but the ones that matter for safety (how to open
///     the channel without transmitting) differ between firmwares.
/// </summary>
/// <remarks>
///     <para>
///         The Lawicel CANUSB manual defines <c>L</c> as "open listen-only". The normaldotcom
///         CANable firmware (<c>cantact-fw</c> for CANable 1.0, <c>canable2-fw</c> for CANable
///         2.0) never implemented <c>L</c>: silent mode is <c>M1</c> sent before <c>O</c>, and
///         <c>M</c> on a Lawicel device means "acceptance code" instead. Sending the wrong
///         sequence does not fail loudly - CANable firmware acknowledges nothing at all - it
///         either leaves the channel closed (no frames, ever) or opens it in normal mode with
///         acknowledgements on the bus. Hence the dialect is modelled explicitly rather than
///         hoped for. Verified against the firmware sources 2026-09-03; see
///         <c>docs/CANABLE_SUPPORT.md</c>.
///     </para>
/// </remarks>
public enum SlcanDialect
{
    /// <summary>
    ///     Not determined. The frame source treats this as <see cref="Lawicel" /> for the open
    ///     sequence, because <c>L</c> is the only listen-only command that is harmless on every
    ///     known firmware (CANable ignores it, so the channel simply stays closed).
    /// </summary>
    Unknown = 0,

    /// <summary>
    ///     Classic Lawicel CANUSB / CAN232 grammar: <c>L</c> opens listen-only, every command is
    ///     acknowledged with CR (0x0D) or rejected with BEL (0x07), <c>V</c> answers
    ///     <c>Vhhss</c>, <c>Z1</c> enables timestamps.
    /// </summary>
    Lawicel,

    /// <summary>
    ///     normaldotcom CANable 1.0 / 2.0 stock firmware: <c>M1</c> + <c>O</c> for silent mode,
    ///     no <c>L</c>, no acknowledgements whatsoever, <c>V</c> answers the git describe string
    ///     and remote URL, <c>E</c> reports the error register. CANable 2.0 adds CAN FD
    ///     (<c>d/D</c>, <c>b/B</c> frames, <c>Y2/Y5</c> data bitrate). <c>S7</c> is 750 kbit/s
    ///     on this firmware, not the Lawicel 800 kbit/s.
    /// </summary>
    Canable,

    /// <summary>
    ///     ElmüSoft "CANable 2.5" replacement firmware (netcult.ch/elmue): backward-compatible
    ///     with <see cref="Canable" /> (<c>M1</c> + <c>O</c> still works, <c>OS</c> is the
    ///     shorthand) but acknowledges commands like Lawicel, adds host-side filters
    ///     (<c>F</c>), bus-load reports (<c>L</c> - a different meaning again), arbitrary
    ///     bitrates and a multi-field <c>V</c> reply.
    /// </summary>
    ElmueSoft
}
