using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;

namespace ObdInsight.Telemetry;

/// <summary>Bounded recovery of a complete ELM/vehicle connection, not individual commands.</summary>
public sealed record VehicleConnectionOptions
{
    public int MaxReconnectAttempts { get; init; } = 6;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// A ready, borrowed generation. Never retain its capabilities or telemetry across Ended.
/// The connection owns disposal. Start telemetry explicitly after each ready generation.
/// </summary>
public sealed class VehicleConnectionGeneration
{
    private readonly TaskCompletionSource<Exception?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal VehicleConnectionGeneration(long number, VehicleDetectionResult detection, TelemetrySession telemetry)
    { Number = number; Detection = detection; Telemetry = telemetry; }
    public long Number { get; }
    public VehicleDetectionResult Detection { get; }
    public ITelemetrySession Telemetry { get; }
    /// <summary>Invalidation signal: loss error, or null for owner disposal. This is not a teardown join.</summary>
    public Task<Exception?> Ended => _ended.Task;
    internal void End(Exception? error) => _ended.TrySetResult(error);
}

/// <summary>
/// Owns open, ELM initialization, vehicle detection and generation recovery. Factory calls
/// must return fresh, exclusively owned transports. No interrupted operation is replayed.
/// DisposeAsync cancels and joins all initialization/recovery/teardown work.
/// </summary>
public sealed class VehicleConnection : IAsyncDisposable, IConnectionStateSource
{
    private readonly Func<IElmTransport> _factory;
    private readonly IReadOnlyList<IVehicleProfile> _profiles;
    private readonly VehicleConnectionOptions _options;
    private readonly TelemetrySubscription? _subscription;
    private readonly TelemetrySessionOptions? _telemetryOptions;
    private readonly IEcuWakeupStrategy? _wakeup;
    private readonly ILogger<VehicleConnection> _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _changed = NewSignal();
    private Task? _supervisor;
    private Task? _disposal;
    private Task? _shutdownCancellation;
    private Exception? _failure;
    private VehicleConnectionGeneration? _current;
    private ConnectionState _state = ConnectionState.Connecting;
    private bool _disposed;

    public VehicleConnection(Func<IElmTransport> transportFactory, IReadOnlyList<IVehicleProfile> profiles,
        VehicleConnectionOptions? options = null, TelemetrySubscription? subscription = null,
        TelemetrySessionOptions? telemetryOptions = null, IEcuWakeupStrategy? wakeupStrategy = null,
        ILogger<VehicleConnection>? logger = null, TimeProvider? timeProvider = null)
    {
        _factory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        _clock = timeProvider ?? TimeProvider.System;
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0) throw new ArgumentException("Register at least one vehicle profile.", nameof(profiles));
        _profiles = profiles.ToArray(); // Explicit registration, no scans/activation.
        _options = options ?? new();
        if (_options.MaxReconnectAttempts < 0 || _options.RetryDelay < TimeSpan.Zero ||
            _options.RetryDelay.TotalMilliseconds > uint.MaxValue - 1 ||
            _options.InitializationTimeout <= TimeSpan.Zero || _options.InitializationTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        _subscription = subscription;
        _telemetryOptions = telemetryOptions;
        _wakeup = wakeupStrategy;
        _logger = logger ?? NullLogger<VehicleConnection>.Instance;
    }

    public ConnectionState State { get { lock (_gate) return _state; } }
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    /// <summary>Owner lifetime; faults on exhausted/fatal recovery, completes on disposal.</summary>
    public Task Completion { get { lock (_gate) return _supervisor ?? Task.CompletedTask; } }

    /// <summary>Starts single-flight supervision. Cancellation cancels only this caller's wait.</summary>
    public ValueTask<VehicleConnectionGeneration> OpenAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _supervisor ??= Task.Run(SuperviseAsync);
        }
        return WaitForReadyAsync(0, ct);
    }

    /// <summary>Waits for a ready generation newer than afterGeneration. Does not start the owner.</summary>
    public async ValueTask<VehicleConnectionGeneration> WaitForReadyAsync(long afterGeneration, CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task changed;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_supervisor is null) throw new InvalidOperationException("Call OpenAsync first.");
                if (_current is { } current && current.Number > afterGeneration && !current.Ended.IsCompleted) return current;
                if (_failure is not null) throw new IOException("Diagnostic connection recovery ended.", _failure);
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { _changed.TrySetResult(); _changed = NewSignal(); } // under gate

    private void SetState(ConnectionState state)
    {
        ConnectionState old;
        lock (_gate)
        {
            if (_disposed && state == ConnectionState.Connected) return;
            old = _state; _state = state; Pulse();
        }
        if (old == state || StateChanged is not { } handlers) return;
        foreach (EventHandler<ConnectionStateChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, new(old, state)); }
            catch (Exception ex) { _logger.LogWarning(ex, "Connection state subscriber failed"); }
        }
    }

    private async Task SuperviseAsync()
    {
        long number = 0;
        string? expectedVin = null;
        var failures = 0;
        try
        {
            while (true)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                GenerationTransport? transport = null;
                VehicleDetectionResult? detection = null;
                TelemetrySession? telemetry = null;
                VehicleConnectionGeneration? generation = null;
                Exception? loss = null;
                try
                {
                    if (failures > 0) await Task.Delay(_options.RetryDelay, _lifetime.Token).ConfigureAwait(false);
                    transport = new GenerationTransport(_factory());
                    using (var init = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                    {
                        init.CancelAfter(_options.InitializationTimeout);
                        try
                        {
                            await transport.OpenAsync(init.Token).ConfigureAwait(false);
                            _lifetime.Token.ThrowIfCancellationRequested();
                            init.Token.ThrowIfCancellationRequested();
                            var session = new ElmSession(new ElmFramer(transport), _wakeup, timeProvider: _clock);
                            await session.InitializeAndLockAsync(init.Token).ConfigureAwait(false);
                            detection = await VehicleResolver.ResolveAsync(session, _profiles, init.Token).ConfigureAwait(false);
                            init.Token.ThrowIfCancellationRequested();
                            transport.ThrowIfEnded();
                        }
                        catch (OperationCanceledException ex) when (!_lifetime.IsCancellationRequested && init.IsCancellationRequested)
                        { throw new TimeoutException("Diagnostic initialization deadline expired.", ex); }
                    }
                    if (detection.Status != VehicleDetectionStatus.Detected || detection.Commands is null)
                        throw new IOException($"Vehicle detection failed: {detection.Status}.");
                    if (expectedVin is not null && expectedVin != detection.Vin)
                        throw new InvalidOperationException("Replacement connection identified a different vehicle; create a new owner explicitly.");
                    expectedVin ??= detection.Vin;
                    telemetry = TelemetrySession.Create(detection.Commands, _subscription, _telemetryOptions, this,
                        timeProvider: _clock, connectionGeneration: number + 1);
                    generation = new(++number, detection, telemetry);
                    lock (_gate)
                    {
                        if (_disposed) throw new OperationCanceledException(_lifetime.Token);
                        _lifetime.Token.ThrowIfCancellationRequested();
                        transport.ThrowIfEnded();
                        _current = generation;
                    }
                    SetState(ConnectionState.Connected); // initialized and detected, not merely BLE-connected
                    failures = 0;
                    loss = await transport.Failure.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                { loss = ex; }
                finally
                {
                    lock (_gate) { _current = null; generation?.End(loss); Pulse(); }
                    if (loss is not null) SetState(ConnectionState.Reconnecting);
                    // Invalidate I/O first, then join consumers, then release physical resources.
                    transport?.Fail(loss ?? new IOException("Connection owner stopping."));
                    try
                    {
                        if (telemetry is not null)
                        {
                            if (loss is not null) await telemetry.TerminateAsync(loss).ConfigureAwait(false);
                            else await telemetry.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        try { if (detection?.Commands is { } commands) await commands.DisposeAsync().ConfigureAwait(false); }
                        finally { if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false); }
                    }
                }
                _lifetime.Token.ThrowIfCancellationRequested();
                if (++failures > _options.MaxReconnectAttempts) throw new IOException("Diagnostic recovery attempts exhausted.", loss);
                SetState(ConnectionState.Reconnecting);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || _disposed) { }
        catch (Exception ex)
        {
            lock (_gate) { _failure = ex; }
            throw;
        }
        finally { SetState(ConnectionState.Lost); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _shutdownCancellation ??= _lifetime.CancelAsync();
            Pulse();
            return new ValueTask(_disposal ??= Task.Run(DisposeCoreAsync));
        }
    }

    private async Task DisposeCoreAsync()
    {
        var cancellation = _shutdownCancellation!;
        try
        {
            if (_supervisor is not null)
            {
                try { await _supervisor.ConfigureAwait(false); }
                catch { /* Completion retains fatal recovery/teardown failure. */ }
            }
            else SetState(ConnectionState.Lost);
            await cancellation.ConfigureAwait(false);
        }
        finally { _lifetime.Dispose(); }
    }
}
