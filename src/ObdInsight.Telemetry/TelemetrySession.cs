using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObdInsight.Core.Vehicles;

namespace ObdInsight.Telemetry;

/// <summary>
/// Default <see cref="ITelemetrySession"/>: single background loop with per-tier due
/// times; ticks run sequentially (the underlying ELM session is single-writer).
/// Cache-only provider reads are bounded by <see cref="TelemetrySessionOptions.CacheReadTimeout"/>;
/// UDS providers ride the existing monitor-suspension arbitration inside capabilities.
/// </summary>
public sealed class TelemetrySession : ITelemetrySession
{
    private readonly IReadOnlyList<ITelemetryProvider> _providers;
    private readonly IVehicleIdentification? _identification;
    private readonly TelemetrySubscription _subscription;
    private readonly TelemetrySessionOptions _options;
    private readonly ILogger<TelemetrySession> _logger;

    private readonly Dictionary<TelemetrySignal, SignalAvailability> _availability = new();
    private readonly List<Channel<TelemetrySampleBatch>> _subscribers = [];
    private readonly SemaphoreSlim _busGate = new(1, 1);
    private readonly object _stateLock = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    public TelemetrySession(
        IReadOnlyList<ITelemetryProvider> providers,
        TelemetrySubscription? subscription = null,
        TelemetrySessionOptions? options = null,
        IVehicleIdentification? identification = null,
        ILogger<TelemetrySession>? logger = null)
    {
        _providers = providers;
        _subscription = subscription ?? TelemetrySubscription.Default;
        _options = options ?? new TelemetrySessionOptions();
        _identification = identification;
        _logger = logger ?? NullLogger<TelemetrySession>.Instance;

        foreach (var signal in _subscription.Map.Keys)
        {
            _availability[signal] = HasProviderFor(signal)
                ? SignalAvailability.Unknown
                : SignalAvailability.Unavailable;
        }
    }

    /// <summary>
    /// Convenience factory over a connected vehicle's command set.
    /// </summary>
    public static TelemetrySession Create(
        IVehicleCommandSet commands,
        TelemetrySubscription? subscription = null,
        TelemetrySessionOptions? options = null,
        ILogger<TelemetrySession>? logger = null)
    {
        commands.TryGet<IVehicleIdentification>(out var identification);
        return new TelemetrySession(
            TelemetryProviderCatalog.FromVehicle(commands),
            subscription,
            options,
            identification,
            logger);
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

    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        lock (_stateLock)
        {
            if (_loopTask is not null)
            {
                return;
            }
        }

        // Availability probe: read every subscribed provider once. Cache-only providers
        // are time-bounded; a UDS provider costs one real query.
        var subscribed = _subscription.Map.Keys.ToHashSet();
        foreach (var provider in _providers)
        {
            var wanted = provider.Signals.Where(subscribed.Contains).ToHashSet();
            if (wanted.Count == 0)
            {
                continue;
            }

            var values = await ReadProviderAsync(provider, wanted, ct);
            UpdateAvailability(values, probe: true, provider.IsCacheOnly);
        }

        var cts = new CancellationTokenSource();
        lock (_stateLock)
        {
            _loopCts = cts;
            _loopTask = Task.Run(() => RunLoopAsync(cts.Token), CancellationToken.None);
        }
    }

    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        Task? loop;
        lock (_stateLock)
        {
            _loopCts?.Cancel();
            loop = _loopTask;
            _loopCts = null;
            _loopTask = null;
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Loop cancellation surfacing through the await — expected on stop.
            }
        }

        lock (_stateLock)
        {
            foreach (var channel in _subscribers)
            {
                channel.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    public async IAsyncEnumerable<TelemetrySampleBatch> Batches(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<TelemetrySampleBatch>(new BoundedChannelOptions(_options.SubscriberBufferSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        lock (_stateLock)
        {
            _subscribers.Add(channel);
        }

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

    public async ValueTask<TelemetrySnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        await _busGate.WaitAsync(ct);
        try
        {
            var all = new Dictionary<TelemetrySignal, TelemetryValue>();
            foreach (var provider in _providers)
            {
                var wanted = provider.Signals.ToHashSet();
                var values = await ReadProviderAsync(provider, wanted, ct);
                foreach (var (signal, value) in values)
                {
                    all[signal] = _options.ValidateRanges
                        ? TelemetryValidator.Validate(signal, value)
                        : value;
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

            return new TelemetrySnapshot
            {
                TimestampUtc = DateTimeOffset.UtcNow,
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
            };
        }
        finally
        {
            _busGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _busGate.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var due = new Dictionary<CadenceTier, long>
        {
            [CadenceTier.High] = 0,
            [CadenceTier.Medium] = 0,
            [CadenceTier.Low] = 0,
        };

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = Environment.TickCount64;
                var next = due.MinBy(kv => kv.Value);
                var wait = next.Value - now;
                if (wait > 0)
                {
                    await Task.Delay((int)wait, ct);
                }

                var tier = next.Key;
                await TickAsync(tier, ct);

                // Schedule from completion, not from the previous due time — an overrun
                // must not produce a catch-up burst.
                due[tier] = Environment.TickCount64 +
                            (long)_options.PeriodFor(tier).TotalMilliseconds;
            }
        }
        catch (OperationCanceledException)
        {
            // Stop requested.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Telemetry scheduler loop died");
        }
    }

    private async Task TickAsync(CadenceTier tier, CancellationToken ct)
    {
        var signals = _subscription.SignalsFor(tier).ToHashSet();
        if (signals.Count == 0)
        {
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var samples = new List<TelemetrySample>(signals.Count);
        var served = new HashSet<TelemetrySignal>();

        await _busGate.WaitAsync(ct);
        try
        {
            foreach (var provider in _providers)
            {
                var wanted = provider.Signals.Where(signals.Contains).ToHashSet();
                if (wanted.Count == 0)
                {
                    continue;
                }

                var values = await ReadProviderAsync(provider, wanted, ct);
                UpdateAvailability(values, probe: false, provider.IsCacheOnly);
                foreach (var (signal, rawValue) in values)
                {
                    var value = _options.ValidateRanges
                        ? TelemetryValidator.Validate(signal, rawValue)
                        : rawValue;
                    samples.Add(new TelemetrySample(signal, value, timestamp, tier));
                    served.Add(signal);
                }
            }
        }
        finally
        {
            _busGate.Release();
        }

        // Signals with no provider still get an (empty) sample — the batch shape is stable.
        foreach (var signal in signals.Where(s => !served.Contains(s)))
        {
            samples.Add(new TelemetrySample(signal, TelemetryValue.Empty, timestamp, tier));
        }

        Publish(new TelemetrySampleBatch(tier, timestamp, samples));
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
            return await provider.ReadAsync(wanted, effectiveCt);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true &&
                                                 !ct.IsCancellationRequested)
        {
            // Cold cache hit the read bound — absent data, not an error.
            return wanted.ToDictionary(s => s, _ => TelemetryValue.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Telemetry provider {Provider} read failed", provider.GetType().Name);
            return wanted.ToDictionary(s => s, _ => TelemetryValue.Empty);
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private void UpdateAvailability(
        IReadOnlyDictionary<TelemetrySignal, TelemetryValue> values, bool probe, bool cacheOnly)
    {
        lock (_stateLock)
        {
            foreach (var (signal, value) in values)
            {
                if (!value.IsEmpty)
                {
                    _availability[signal] = SignalAvailability.Available;
                }
                else if (probe && !cacheOnly &&
                         _availability.GetValueOrDefault(signal) != SignalAvailability.Available)
                {
                    // A UDS probe that answered with nothing is a definitive miss;
                    // an empty cache probe stays Unknown (frames may appear while driving).
                    _availability[signal] = SignalAvailability.Unavailable;
                }
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

        BatchAvailable?.Invoke(this, batch);
    }

    private bool HasProviderFor(TelemetrySignal signal) =>
        _providers.Any(p => p.Signals.Contains(signal));

    private static decimal? Scalar(Dictionary<TelemetrySignal, TelemetryValue> all, TelemetrySignal s) =>
        all.GetValueOrDefault(s).Scalar;

    private static IReadOnlyList<decimal>? Vector(Dictionary<TelemetrySignal, TelemetryValue> all, TelemetrySignal s) =>
        all.GetValueOrDefault(s).Vector;

    private static bool? Boolean(Dictionary<TelemetrySignal, TelemetryValue> all, TelemetrySignal s) =>
        all.GetValueOrDefault(s).Boolean;
}
