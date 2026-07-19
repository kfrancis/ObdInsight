namespace ObdInsight.Core.Communication.Elm327
{
    /// <summary>
    ///     One window in a <see cref="CanMonitor" /> filter rotation: a hardware CAN filter
    ///     (AT CM mask / AT CF pattern) held for <paramref name="Dwell" /> before rotating to
    ///     the next window. Rotation works around the ELM327's single mask/pattern pair and
    ///     the limited BLE throughput of cheap adapters — accept-all monitoring overruns them
    ///     within ~100ms on a busy EV bus (observed on hardware 2026-07-18).
    /// </summary>
    /// <param name="Mask">CAN filter mask, 3 hex digits (e.g. "700").</param>
    /// <param name="Pattern">CAN filter pattern, 3 hex digits (e.g. "100" accepts 0x100-0x1FF with mask 700).</param>
    /// <param name="Dwell">How long to monitor this window before rotating.</param>
    public sealed record CanFilterWindow(string Mask, string Pattern, TimeSpan Dwell);
}
