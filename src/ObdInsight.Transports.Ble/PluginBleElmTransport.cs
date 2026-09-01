using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ObdInsight.Core.Communication.Elm327;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
// Android's global usings drag in Android.Widget.IAdapter — disambiguate explicitly.
using IBleAdapter = Plugin.BLE.Abstractions.Contracts.IAdapter;

namespace ObdInsight.Transports.Ble;

/// <summary>
///     <see cref="IElmTransport" /> over Plugin.BLE for Android/iOS (roadmap B9).
///     Connects to a known device, auto-probes the GATT profile
///     (<see cref="BleProfileResolver" />), feeds reads from notifications (no busy-poll),
///     and chunks writes to the profile's write size.
///     The wrapper is deliberately thin — all selection logic lives in the pure resolver.
///     Hardware check against a real Vgate iCar Pro: pending (working rule 4).
/// </summary>
public sealed class PluginBleElmTransport : IConnectionAwareTransport
{
    private readonly IBleAdapter _adapter;
    private readonly SemaphoreSlim _dataSignal = new(0);
    private readonly Guid _deviceId;
    private readonly ResolvedBleProfile? _forcedProfile;

    private readonly object _gate = new();
    private readonly ILogger<PluginBleElmTransport> _logger;
    private readonly Queue<byte> _rx = new();

    private IDevice? _device;
    private List<GattServiceInfo> _lastTopology = [];
    private ICharacteristic? _notifyCharacteristic;
    private BleProbeStage _probeStage = BleProbeStage.Connecting;
    private ICharacteristic? _writeCharacteristic;

    public PluginBleElmTransport(
        IBleAdapter adapter,
        Guid deviceId,
        ResolvedBleProfile? forcedProfile = null,
        ILogger<PluginBleElmTransport>? logger = null)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _deviceId = deviceId;
        _forcedProfile = forcedProfile;
        _logger = logger ?? NullLogger<PluginBleElmTransport>.Instance;
    }

    /// <summary>The profile in use after a successful open.</summary>
    public ResolvedBleProfile? ActiveProfile { get; private set; }

    public BleProbeReport? LastProbeReport { get; private set; }

    public event EventHandler? ConnectionLost;

    public bool IsOpen { get; private set; }

    public async ValueTask OpenAsync(CancellationToken ct)
    {
        _probeStage = BleProbeStage.Connecting;
        _lastTopology = [];
        LastProbeReport = null;
        ResolvedBleProfile? resolved = null;
        try
        {
            _device = await _adapter.ConnectToKnownDeviceAsync(_deviceId, cancellationToken: ct);

            _probeStage = BleProbeStage.DiscoveringServices;
            var services = await _device.GetServicesAsync(ct);
            foreach (var service in services)
            {
                var characteristics = await service.GetCharacteristicsAsync();
                _lastTopology.Add(new GattServiceInfo(
                    service.Id,
                    characteristics
                        .Select(c => new GattCharacteristicInfo(c.Id, c.CanWrite, c.CanUpdate))
                        .ToList()));
            }

            _probeStage = BleProbeStage.ResolvingProfile;
            resolved = _forcedProfile ?? BleProfileResolver.Resolve(_lastTopology, _device.Name);
            if (resolved is null)
                throw new IOException(
                    $"No compatible OBD GATT profile on device {_deviceId} " +
                    $"({_lastTopology.Count} services discovered).");
            _logger.LogInformation("BLE profile resolved: {Profile}", resolved.Name);

            _probeStage = BleProbeStage.BindingCharacteristics;
            var gattService = services.First(s => s.Id == resolved.ServiceUuid);
            var characteristicsInService = await gattService.GetCharacteristicsAsync();
            _writeCharacteristic = characteristicsInService.First(c => c.Id == resolved.WriteCharacteristicUuid);
            _notifyCharacteristic = characteristicsInService.First(c => c.Id == resolved.NotifyCharacteristicUuid);

            _writeCharacteristic.WriteType = resolved.WriteWithResponse
                ? CharacteristicWriteType.WithResponse
                : CharacteristicWriteType.WithoutResponse;

            _probeStage = BleProbeStage.SubscribingNotifications;
            _notifyCharacteristic.ValueUpdated += OnValueUpdated;
            await _notifyCharacteristic.StartUpdatesAsync(ct);

            _adapter.DeviceDisconnected += OnDeviceDisconnected;
            _adapter.DeviceConnectionLost += OnDeviceDisconnected;

            ActiveProfile = resolved;
            IsOpen = true;
            CompleteProbe(new BleProbeReport(BleProbeStage.Completed, _lastTopology, resolved, null, null));
        }
        catch (Exception ex)
        {
            var kind = ClassifyFailure(_probeStage, ex);
            CompleteProbe(new BleProbeReport(
                _probeStage,
                _lastTopology,
                resolved,
                kind,
                FailureMessage(kind)));
            throw;
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var characteristic = _writeCharacteristic
                             ?? throw new InvalidOperationException("Transport not open.");
        var chunkSize = ActiveProfile?.MaxWriteSize ?? 20;

        foreach (var chunk in BleProfileResolver.Chunk(data, chunkSize))
        {
            await characteristic.WriteAsync(chunk.ToArray(), ct);
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_rx.Count > 0)
                {
                    var n = 0;
                    while (n < buffer.Length && _rx.Count > 0)
                    {
                        buffer.Span[n++] = _rx.Dequeue();
                    }

                    return n;
                }
            }

            await _dataSignal.WaitAsync(ct);
        }
    }

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public void ClearBuffer()
    {
        lock (_gate)
        {
            _rx.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        IsOpen = false;
        _adapter.DeviceDisconnected -= OnDeviceDisconnected;
        _adapter.DeviceConnectionLost -= OnDeviceDisconnected;

        if (_notifyCharacteristic is not null)
        {
            _notifyCharacteristic.ValueUpdated -= OnValueUpdated;
            try
            {
                await _notifyCharacteristic.StopUpdatesAsync();
            }
            catch
            {
                // Best-effort teardown — the link may already be gone.
            }
        }

        if (_device is not null)
        {
            try
            {
                await _adapter.DisconnectDeviceAsync(_device);
            }
            catch
            {
                // Best-effort teardown.
            }
        }

        _dataSignal.Dispose();
    }

    public event Action<BleProbeReport>? ProbeCompleted;

    private void CompleteProbe(BleProbeReport report)
    {
        LastProbeReport = report;
        var handlers = ProbeCompleted;
        if (handlers is null)
            return;

        foreach (Action<BleProbeReport> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(report);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "BLE probe-completed callback failed.");
            }
        }
    }

    private static BleProbeFailureKind ClassifyFailure(BleProbeStage stage, Exception exception) =>
        exception is OperationCanceledException
            ? BleProbeFailureKind.Cancelled
            : stage switch
            {
                BleProbeStage.Connecting => BleProbeFailureKind.ConnectionFailed,
                BleProbeStage.DiscoveringServices => BleProbeFailureKind.ServiceDiscoveryFailed,
                BleProbeStage.ResolvingProfile when exception is IOException => BleProbeFailureKind.NoCompatibleProfile,
                BleProbeStage.BindingCharacteristics => BleProbeFailureKind.CharacteristicBindingFailed,
                BleProbeStage.SubscribingNotifications => BleProbeFailureKind.NotificationSubscriptionFailed,
                _ => BleProbeFailureKind.Unknown
            };

    private static string FailureMessage(BleProbeFailureKind kind) =>
        kind switch
        {
            BleProbeFailureKind.Cancelled => "The BLE probe was cancelled.",
            BleProbeFailureKind.ConnectionFailed => "The BLE connection failed.",
            BleProbeFailureKind.ServiceDiscoveryFailed => "BLE service discovery failed.",
            BleProbeFailureKind.NoCompatibleProfile => "No compatible BLE GATT profile was found.",
            BleProbeFailureKind.CharacteristicBindingFailed => "BLE characteristic binding failed.",
            BleProbeFailureKind.NotificationSubscriptionFailed => "BLE notification subscription failed.",
            _ => "The BLE probe failed."
        };

    private void OnValueUpdated(object? sender, CharacteristicUpdatedEventArgs e)
    {
        var value = e.Characteristic.Value;
        if (value is not { Length: > 0 })
        {
            return;
        }

        lock (_gate)
        {
            foreach (var b in value)
            {
                _rx.Enqueue(b);
            }
        }

        _dataSignal.Release();
    }

    private void OnDeviceDisconnected(object? sender, DeviceEventArgs e)
    {
        if (_device is null || e.Device.Id != _device.Id || !IsOpen)
        {
            return;
        }

        IsOpen = false;
        _logger.LogWarning("BLE connection lost to {DeviceId}", _deviceId);
        ConnectionLost?.Invoke(this, EventArgs.Empty);
        _dataSignal.Release(); // wake a blocked reader so it can observe the loss
    }
}
