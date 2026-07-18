namespace ObdInsight.Core.Communication.Elm327
{
    /// <summary>
    ///     Why a monitoring run ended. Surfaced by <see cref="IElmSession.LastMonitoringEndReason" />
    ///     after <see cref="IElmSession.MonitorFramesAsync" /> completes, and by
    ///     <see cref="CanMonitor.EndReason" /> for the long-lived monitor.
    /// </summary>
    public enum MonitoringEndReason
    {
        /// <summary>Monitoring has not ended (still running, or never started).</summary>
        None,

        /// <summary>Caller-initiated stop or cancellation.</summary>
        Stopped,

        /// <summary>The ELM327 reported BUFFER FULL and exited monitoring itself.</summary>
        BufferFull,

        /// <summary>The adapter unexpectedly dropped to the command prompt.</summary>
        PromptDetected,

        /// <summary>The underlying transport failed.</summary>
        TransportError
    }
}
