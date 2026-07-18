namespace ObdInsight.Core.Communication.Elm327
{
    /// <summary>
    ///     Vehicle-specific ECU wakeup/probe strategy, invoked by <see cref="ElmSession" /> during
    ///     initialization when the standard OBD-II broadcast probe (0100) gets no response.
    ///     Implementations live with their vehicle (e.g. an EV's proprietary-CAN BMS probe) so the
    ///     generic session layer stays vehicle-agnostic.
    /// </summary>
    public interface IEcuWakeupStrategy
    {
        /// <summary>Descriptive name used in logs (e.g. the targeted ECU).</summary>
        string Name { get; }

        /// <summary>
        ///     Attempts a vehicle-specific wakeup/probe using the given framer. The adapter is
        ///     already configured for CAN 11-bit 500k (protocol 6) when this is called.
        /// </summary>
        /// <param name="framer">Framer for sending AT commands and queries.</param>
        /// <param name="commandTimeout">The session's per-command timeout.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        ///     The ELM327 protocol character to lock (e.g. '6') if the vehicle responded and the
        ///     protocol is confirmed; null if the probe got no response. Implementations should
        ///     swallow their own probe errors and return null — wakeup is best-effort.
        /// </returns>
        ValueTask<char?> TryWakeupAsync(ElmFramer framer, TimeSpan commandTimeout, CancellationToken ct);
    }
}
