using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Telemetry;

/// <summary>
///     Default <see cref="ITelemetrySession" />: single background loop with per-tier due
///     times; ticks run sequentially (the underlying ELM session is single-writer).
///     Cache-only provider reads are bounded by <see cref="TelemetrySessionOptions.CacheReadTimeout" />;
///     UDS providers ride the existing monitor-suspension arbitration inside capabilities.
/// </summary>
public sealed class TelemetrySession : ITelemetrySession
{
    private readonly Dictionary<TelemetrySignal, SignalAvailability> _availability = new();
    private readonly SemaphoreSlim _busGate = new(1, 1);
    private readonly IConnectionStateSource? _connectionState;
    private readonly IDiagnosticTroubleCodes? _dtc;
    private readonly IVehicleIdentification? _identification;
    private readonly ILogger<TelemetrySession> _logger;
    private readonly TelemetrySessionOptions _options;
    private readonly IReadOnlyList<ITelemetryProvider> _providers;
    private readonly object _stateLock = new();
    private readonly List<Channel<TelemetrySampleBatch>> _subscribers = [];
    private readonly TelemetrySubscription _subscription;
    private readonly TimeProvider _clock;
    private readonly long? _connectionGeneration;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private Task? _cancellationTask;
    private TaskCompletionSource _started = NewCompletion();
    private TaskCompletionSource _completion = NewCompletion();
    private Exception? _terminalError;
    private Exception? _forcedError;
    private bool _disposed;
    private Task? _disposeTask;

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion
    {
        get { lock (_stateLock) { return _loopTask is null ? Task.CompletedTask : _completion.Task; } }
    }

    public TelemetrySession(
        IReadOnlyList<ITelemetryProvider> providers,
        TelemetrySubscription? subscription = null,
        TelemetrySessionOptions? options = null,
        IVehicleIdentification? identification = null,
        IDiagnosticTroubleCodes? dtc = null,
        IConnectionStateSource? connectionState = null,
        ILogger<TelemetrySession>? logger = null,
        TimeProvider? timeProvider = null, long? connectionGeneration = null)
    {
        _providers = providers;
        _subscription = subscription ?? TelemetrySubscription.Default;
        _options = options ?? new TelemetrySessionOptions();
        if (_options.MaxObservationAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        _clock = timeProvider ?? TimeProvider.System;
        _connectionGeneration = connectionGeneration;
        _identification = identification;
        _dtc = dtc;
        _connectionState = connectionState;
        if (_connectionState is not null)
        {
            _connectionState.StateChanged += OnConnectionStateChanged;
        }

        _logger = logger ?? NullLogger<TelemetrySession>.Instance;

        foreach (var signal in _subscription.Map.Keys)
        {
            _availability[signal] = HasProviderFor(signal)
                ? SignalAvailability.Unknown
                : SignalAvailability.Unavailable;
        }
    }

    public IReadOnlyDictionary<TelemetrySignal, SignalAvailability> Availability
    {
        get
        {
            lock (_stateLock)
            {
                return new Dictionary<TelemetrySignal, SignalAvailability>(_availability);
            }
        }
    }

    public event EventHandler<TelemetrySampleBatch>? BatchAvailable;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public ConnectionState? ConnectionState => _connectionState?.State;

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Task started;
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loopTask is null || _completion.Task.IsCompleted)
            {
                if (_cancellationTask is { IsCompleted: false })
                    throw new InvalidOperationException("Cancellation callbacks are still running. Await StopAsync before restarting.");
                _loopCts?.Dispose();
                _cancellationTask = null;
                _loopCts = new CancellationTokenSource();
                _started = NewCompletion();
                _completion = NewCompletion();
                _terminalError = null;
                // Reserve ownership before any provider code executes. The initiating
                // caller token applies to the probe only, never to the running scheduler.
                var runCts = _loopCts;
                _loopTask = Task.Run(() => RunAsync(runCts, ct), CancellationToken.None);
            }
            else if (_loopCts!.IsCancellationRequested)
            {
                throw new InvalidOperationException("The previous telemetry run is still stopping. Await StopAsync before restarting.");
            }

            started = _started.Task;
        }

        await started.WaitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Task? loop;
        Task cancellation;
        lock (_stateLock)
        {
            loop = _loopTask;
            cancellation = _cancellationTask ??= _loopCts?.CancelAsync() ?? Task.CompletedTask;
            if (loop is null)
            {
                CompleteSubscribers(null);
            }
        }

        // Cancellation of this wait does not release ownership of the producer.
        // A later stop/dispose still joins both producer and cancellation callbacks.
        await Task.WhenAll(loop ?? Task.CompletedTask, cancellation).WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationTokenSource runCts, CancellationToken startupToken)
    {
        Exception? error = null;
        try
        {
            using (var probeCts = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token, startupToken))
            {
                await _busGate.WaitAsync(probeCts.Token).ConfigureAwait(false);
                try
                {
                    var subscribed = _subscription.Map.Keys.ToHashSet();
                    foreach (var provider in _providers)
                    {
                        var wanted = provider.Signals.Where(subscribed.Contains).ToHashSet();
                        if (wanted.Count == 0) continue;
                        var values = await ReadProviderAsync(provider, wanted, probeCts.Token).ConfigureAwait(false);
                        UpdateAvailability(values.ToDictionary(kv => kv.Key, kv => Assess(kv.Key, kv.Value, _clock.GetUtcNow())));
                    }
                    probeCts.Token.ThrowIfCancellationRequested();
                }
                finally { _busGate.Release(); }
            }

            _started.TrySetResult();
            await RunLoopAsync(runCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runCts.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                if (_forcedError is not null) _started.TrySetException(_forcedError);
                else _started.TrySetCanceled(runCts.Token);
            }
        }
        catch (OperationCanceledException) when (!_started.Task.IsCompleted && startupToken.IsCancellationRequested)
        {
            _started.TrySetCanceled(startupToken);
        }
        catch (Exception ex)
        {
            error = ex;
            _started.TrySetException(ex);
        }
        finally
        {
            lock (_stateLock)
            {
                error ??= _forcedError;
                _terminalError = error;
                CompleteSubscribers(error);
                if (error is null) _completion.TrySetResult();
                else _completion.TrySetException(error);
                // Keep the worker task until it actually completes; never clear it in StopAsync.
            }
        }
    }

    // Caller holds _stateLock.
    private void CompleteSubscribers(Exception? error)
    {
        foreach (var channel in _subscribers) channel.Writer.TryComplete(error);
        _subscribers.Clear();
    }

    public IAsyncEnumerable<TelemetrySampleBatch> Batches(CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<TelemetrySampleBatch>(
            new BoundedChannelOptions(_options.SubscriberBufferSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true
            });

        // Register here rather than inside the iterator: an async iterator would defer this to
        // the first MoveNext, and every batch produced in between would be lost.
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loopTask is not null && _completion.Task.IsCompleted)
                channel.Writer.TryComplete(_terminalError);
            else
                _subscribers.Add(channel);
        }

        return ReadBatchesAsync(channel, ct);
    }

    public IAsyncEnumerable<TelemetrySample<T>> Stream<T>(
        TelemetrySignal<T> signal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        // Batches registers eagerly, so the projection inherits that guarantee.
        return ProjectAsync(Batches(ct), signal, ct);
    }

    public async ValueTask<TelemetrySnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        lock (_stateLock) { ObjectDisposedException.ThrowIf(_disposed, this); }
        await _busGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock) { ObjectDisposedException.ThrowIf(_disposed, this); }
            var all = new Dictionary<TelemetrySignal, TelemetryValue>();
            foreach (var provider in _providers)
            {
                var wanted = provider.Signals.ToHashSet();
                var values = await ReadProviderAsync(provider, wanted, ct);
                foreach (var (signal, value) in values)
                {
                    all[signal] = value;
                }
            }

            string? vin = null;
            if (_identification is not null)
            {
                try
                {
                    vin = await _identification.GetVinAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "VIN read failed during snapshot");
                }
            }

            DtcReadResult? dtcs = null;
            if (_dtc is not null)
            {
                try
                {
                    dtcs = await _dtc.GetDtcsAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    ct.ThrowIfCancellationRequested();
                    _logger.LogDebug(ex, "DTC read failed during snapshot");
                    dtcs = new DtcReadResult
                    {
                        Stored = DtcModeResult.Failed(ex is TimeoutException ? DtcReadStatus.Timeout : DtcReadStatus.QueryFailed),
                        Pending = DtcModeResult.Failed(ex is TimeoutException ? DtcReadStatus.Timeout : DtcReadStatus.QueryFailed)
                    };
                }
            }

            lock (_stateLock) { if (_forcedError is not null) throw _forcedError; }
            var published = _clock.GetUtcNow();
            foreach (var signal in Enum.GetValues<TelemetrySignal>())
                all[signal] = Assess(signal, all.GetValueOrDefault(signal, Absent(signal)), published);
            return new TelemetrySnapshot
            {
                TimestampUtc = published,
                ConnectionGeneration = _connectionGeneration,
                Measurements = new System.Collections.ObjectModel.ReadOnlyDictionary<TelemetrySignal, TelemetryValue>(all),
                Vin = vin,
                SocPercent = Scalar(all, TelemetrySignal.StateOfCharge),
                PackVoltageV = Scalar(all, TelemetrySignal.PackVoltage),
                PackCurrentA = Scalar(all, TelemetrySignal.PackCurrent),
                PackPowerKw = Scalar(all, TelemetrySignal.PackPower),
                PackTemperatureC = Scalar(all, TelemetrySignal.PackTemperature),
                StateOfHealthPercent = Scalar(all, TelemetrySignal.StateOfHealth),
                CellVoltagesV = Vector(all, TelemetrySignal.CellVoltages),
                CellVoltageMinV = Scalar(all, TelemetrySignal.CellVoltageMin),
                CellVoltageMaxV = Scalar(all, TelemetrySignal.CellVoltageMax),
                CellVoltageAverageV = Scalar(all, TelemetrySignal.CellVoltageAverage),
                VehicleSpeedKmh = Scalar(all, TelemetrySignal.VehicleSpeed),
                RemainingRangeKm = Scalar(all, TelemetrySignal.RemainingRange),
                CabinTemperatureC = Scalar(all, TelemetrySignal.CabinTemperature),
                HvacActive = Boolean(all, TelemetrySignal.HvacActive),
                OdometerKm = Scalar(all, TelemetrySignal.Odometer),
                ChargeCycleCount = Scalar(all, TelemetrySignal.ChargeCycleCount),
                DiagnosticTroubleCodes = dtcs
            };
        }
        finally
        {
            _busGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            _disposed = true;
            return new ValueTask(_disposeTask ??= Task.Run(DisposeCoreAsync));
        }
    }

    // The connection owner invalidates the whole generation, including cache-only providers.
    internal ValueTask TerminateAsync(Exception error)
    {
        Invalidate(error);
        return DisposeAsync();
    }

    // Synchronous admission barrier; teardown is separately owned and joined.
    internal void Invalidate(Exception error)
    {
        lock (_stateLock)
        {
            _forcedError ??= error;
            _disposed = true;
            if (_loopTask is null) CompleteSubscribers(error);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            if (_connectionState is not null)
                _connectionState.StateChanged -= OnConnectionStateChanged;
            // Join any snapshot already inside the gate. Keep the managed semaphore
            // undisposed so queued snapshot callers can safely observe disposal.
            await _busGate.WaitAsync().ConfigureAwait(false);
            _busGate.Release();
            lock (_stateLock)
            {
                _loopCts?.Dispose();
                _loopCts = null;
            }
        }
    }

    /// <summary>
    ///     Convenience factory over a connected vehicle's command set.
    /// </summary>
    public static TelemetrySession Create(
        IVehicleCommandSet commands,
        TelemetrySubscription? subscription = null,
        TelemetrySessionOptions? options = null,
        IConnectionStateSource? connectionState = null,
        ILogger<TelemetrySession>? logger = null,
        TimeProvider? timeProvider = null, long? connectionGeneration = null)
    {
        commands.TryGet<IVehicleIdentification>(out var identification);
        commands.TryGet<IDiagnosticTroubleCodes>(out var dtc);
        return new TelemetrySession(
            TelemetryProviderCatalog.FromVehicle(commands),
            subscription,
            options,
            identification,
            dtc,
            connectionState,
            logger, timeProvider, connectionGeneration);
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        lock (_stateLock) { if (_disposed) return; }
        NotifyHandlers(ConnectionStateChanged, e);
    }

    private void NotifyHandlers<T>(EventHandler<T>? handlers, T args)
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception ex) { _logger.LogWarning(ex, "Telemetry event subscriber failed"); }
        }
    }

    private static async IAsyncEnumerable<TelemetrySample<T>> ProjectAsync<T>(
        IAsyncEnumerable<TelemetrySampleBatch> batches,
        TelemetrySignal<T> signal,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var batch in batches.WithCancellation(ct))
        {
            foreach (var sample in batch.Samples)
            {
                if (sample.Signal == signal.Signal && signal.TryRead(sample.Value, out var value))
                {
                    yield return new TelemetrySample<T>(sample.Signal, value, sample.TimestampUtc, sample.Tier)
                    { Observation = sample.Value.Observation, Freshness = sample.Value.Freshness,
                        Age = sample.Value.Age, ConnectionGeneration = batch.ConnectionGeneration };
                }
            }
        }
    }

    private async IAsyncEnumerable<TelemetrySampleBatch> ReadBatchesAsync(
        Channel<TelemetrySampleBatch> channel,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(ct))
            {
                yield return batch;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _subscribers.Remove(channel);
            }
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var due = new Dictionary<CadenceTier, long>
        {
            [CadenceTier.High] = 0, [CadenceTier.Medium] = 0, [CadenceTier.Low] = 0
        };

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var now = Environment.TickCount64;
            var next = due.MinBy(kv => kv.Value);
            var wait = next.Value - now;
            if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct).ConfigureAwait(false);
            await TickAsync(next.Key, ct).ConfigureAwait(false);
            due[next.Key] = Environment.TickCount64 + (long)_options.PeriodFor(next.Key).TotalMilliseconds;
        }
    }

    private async Task TickAsync(CadenceTier tier, CancellationToken ct)
    {
        var signals = _subscription.SignalsFor(tier).ToHashSet();
        if (signals.Count == 0) return;
        var raw = new Dictionary<TelemetrySignal, TelemetryValue>();
        await _busGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var provider in _providers)
            {
                var wanted = provider.Signals.Where(signals.Contains).ToHashSet();
                if (wanted.Count == 0) continue;
                var values = await ReadProviderAsync(provider, wanted, ct).ConfigureAwait(false);
                foreach (var (signal, value) in values)
                    if (wanted.Contains(signal)) raw[signal] = value;
            }
        }
        finally { _busGate.Release(); }

        // Publication is after acquisition. Every value retains its own original observation.
        var published = _clock.GetUtcNow();
        var assessed = signals.ToDictionary(s => s, s => Assess(s, raw.GetValueOrDefault(s, Absent(s)), published));
        UpdateAvailability(assessed);
        var samples = assessed.Select(kv => new TelemetrySample(kv.Key, kv.Value, published, tier)).ToArray();
        Publish(new TelemetrySampleBatch(tier, published, Array.AsReadOnly(samples))
        { ConnectionGeneration = _connectionGeneration });
    }

    private TelemetryValue Absent(TelemetrySignal signal) => TelemetryValue.Empty with
    {
        Observation = new(Quality: HasProviderFor(signal) ? ObservationQuality.Missing : ObservationQuality.Unsupported)
    };

    private TelemetryValue Assess(TelemetrySignal signal, TelemetryValue value, DateTimeOffset published)
    {
        value = value.WithObservation(value.Observation);
        var originalVector = value.Vector;
        if (_options.ValidateRanges) value = TelemetryValidator.Validate(signal, value);
        if (value.Vector is not null && ReferenceEquals(value.Vector, originalVector))
            value = value with { Vector = Array.AsReadOnly(value.Vector.ToArray()) };
        var age = value.Observation.AgeAt(_clock, published);
        return value with
        {
            Age = age is { } elapsed && elapsed >= TimeSpan.Zero ? elapsed : null,
            Freshness = age is null || age < TimeSpan.Zero ? ObservationFreshness.Unknown :
                age > _options.MaxObservationAge ? ObservationFreshness.Stale : ObservationFreshness.Fresh
        };
    }

    private async ValueTask<IReadOnlyDictionary<TelemetrySignal, TelemetryValue>> ReadProviderAsync(
        ITelemetryProvider provider, IReadOnlySet<TelemetrySignal> wanted, CancellationToken ct)
    {
        CancellationTokenSource? timeoutCts = null;
        var effectiveCt = ct;
        if (provider.IsCacheOnly)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.CacheReadTimeout);
            effectiveCt = timeoutCts.Token;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            var values = await provider.ReadAsync(wanted, effectiveCt).ConfigureAwait(false);
            lock (_stateLock) { if (_forcedError is not null) throw _forcedError; }
            ct.ThrowIfCancellationRequested();
            return values;
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true &&
                                                 !ct.IsCancellationRequested)
        {
            // Cold cache hit the read bound — absent data, not an error.
            return wanted.ToDictionary(s => s, _ => TelemetryValue.Empty with
            { Observation = new(Source: ObservationSource.CanBroadcast, Quality: ObservationQuality.Missing) });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogDebug(ex, "Telemetry provider {Provider} read failed", provider.GetType().Name);
            return wanted.ToDictionary(s => s, _ => TelemetryValue.Empty with
            { Observation = new(Source: provider.IsCacheOnly ? ObservationSource.CanBroadcast : ObservationSource.DiagnosticQuery,
                Quality: ObservationQuality.TimedOut) });
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private void UpdateAvailability(IReadOnlyDictionary<TelemetrySignal, TelemetryValue> values)
    {
        lock (_stateLock)
        {
            foreach (var (signal, value) in values)
            {
                _availability[signal] = value.Observation.Quality == ObservationQuality.Unsupported
                    ? SignalAvailability.Unavailable
                    : value.IsEmpty || value.Vector is { } vector && !vector.Any(v => v.HasValue) ? SignalAvailability.Unknown
                    : value.Freshness == ObservationFreshness.Stale ? SignalAvailability.Stale
                    : value.Freshness == ObservationFreshness.Fresh ? SignalAvailability.Available
                    : SignalAvailability.Unknown;
            }
        }
    }

    private void Publish(TelemetrySampleBatch batch)
    {
        lock (_stateLock)
        {
            foreach (var channel in _subscribers)
            {
                channel.Writer.TryWrite(batch);
            }
        }

        NotifyHandlers(BatchAvailable, batch);
    }

    private bool HasProviderFor(TelemetrySignal signal) =>
        _providers.Any(p => p.Signals.Contains(signal));

    private static TelemetryValue Current(TelemetryValue value) =>
        value.Freshness == ObservationFreshness.Fresh ? value : TelemetryValue.Empty;

    private static decimal? Scalar(Dictionary<TelemetrySignal, TelemetryValue> all, TelemetrySignal s) =>
        Current(all.GetValueOrDefault(s)).Scalar;

    private static IReadOnlyList<decimal?>? Vector(Dictionary<TelemetrySignal, TelemetryValue> all, TelemetrySignal s) =>
        Current(all.GetValueOrDefault(s)).Vector;

    private static bool? Boolean(Dictionary<TelemetrySignal, TelemetryValue> all, TelemetrySignal s) =>
        Current(all.GetValueOrDefault(s)).Boolean;
}
