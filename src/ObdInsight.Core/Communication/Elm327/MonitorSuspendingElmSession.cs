using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Communication.Elm327
{
    /// <summary>
    ///     <see cref="IElmSession" /> decorator that transparently suspends a shared
    ///     <see cref="CanMonitor" /> around request/response work, so capabilities written
    ///     against <see cref="IElmSession" /> (UDS queries, legacy enter/exit monitoring) can
    ///     coexist with a continuously running monitor without knowing it exists.
    ///     For legacy monitoring consumers, the suspension opens at
    ///     <see cref="EnterMonitoringModeAsync" /> and closes at <see cref="ExitMonitoringModeAsync" />.
    ///     Not thread-safe — same single-consumer contract as <see cref="ElmSession" />.
    /// </summary>
    internal sealed class MonitorSuspendingElmSession : IElmSession
    {
        private readonly IElmSession _inner;
        private readonly CanMonitor _monitor;
        private IAsyncDisposable? _monitoringSuspension;

        public MonitorSuspendingElmSession(IElmSession inner, CanMonitor monitor)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        }

        public TimeSpan CommandTimeout
        {
            get => _inner.CommandTimeout;
            set => _inner.CommandTimeout = value;
        }

        public EcuCommunicationMode CurrentMode => _inner.CurrentMode;

        public bool EnableDebugLogging
        {
            get => _inner.EnableDebugLogging;
            set => _inner.EnableDebugLogging = value;
        }

        public int MaxConsecutiveFailures
        {
            get => _inner.MaxConsecutiveFailures;
            set => _inner.MaxConsecutiveFailures = value;
        }

        public TimeSpan ProtocolDetectionTimeout
        {
            get => _inner.ProtocolDetectionTimeout;
            set => _inner.ProtocolDetectionTimeout = value;
        }

        public MonitoringEndReason LastMonitoringEndReason => _inner.LastMonitoringEndReason;

        public async ValueTask InitializeAndLockAsync(CancellationToken ct)
        {
            await using var _ = await _monitor.SuspendAsync(ct);
            await _inner.InitializeAndLockAsync(ct);
        }

        public async ValueTask<string[]> QueryAsync(string obdCommand, CancellationToken ct)
        {
            await using var _ = await _monitor.SuspendAsync(ct);
            return await _inner.QueryAsync(obdCommand, ct);
        }

        public async ValueTask<string[]> QueryAsync(string obdCommand, EcuContext context, CancellationToken ct)
        {
            await using var _ = await _monitor.SuspendAsync(ct);
            return await _inner.QueryAsync(obdCommand, context, ct);
        }

        public async ValueTask SetEcuContextAsync(EcuContext context, CancellationToken ct)
        {
            await using var _ = await _monitor.SuspendAsync(ct);
            await _inner.SetEcuContextAsync(context, ct);
        }

        public async ValueTask<bool> ActivateSessionAsync(EcuContext context, CancellationToken ct)
        {
            await using var _ = await _monitor.SuspendAsync(ct);
            return await _inner.ActivateSessionAsync(context, ct);
        }

        public async ValueTask EnterMonitoringModeAsync(EcuContext context, CancellationToken ct)
        {
            // Legacy monitoring consumer wants exclusive monitoring: hold the shared monitor
            // suspended for the whole Enter..Exit window.
            _monitoringSuspension ??= await _monitor.SuspendAsync(ct);
            try
            {
                await _inner.EnterMonitoringModeAsync(context, ct);
            }
            catch
            {
                await ReleaseMonitoringSuspensionAsync();
                throw;
            }
        }

        public IAsyncEnumerable<RawCanFrame> MonitorFramesAsync(CancellationToken ct)
        {
            return _inner.MonitorFramesAsync(ct);
        }

        public async ValueTask ExitMonitoringModeAsync(CancellationToken ct)
        {
            try
            {
                await _inner.ExitMonitoringModeAsync(ct);
            }
            finally
            {
                await ReleaseMonitoringSuspensionAsync();
            }
        }

        private async ValueTask ReleaseMonitoringSuspensionAsync()
        {
            var suspension = _monitoringSuspension;
            _monitoringSuspension = null;
            if (suspension is not null)
            {
                await suspension.DisposeAsync();
            }
        }
    }
}
