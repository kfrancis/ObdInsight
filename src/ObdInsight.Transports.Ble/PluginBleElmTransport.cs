using System.Text;
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
/// <see cref="IElmTransport"/> over Plugin.BLE for Android/iOS (roadmap B9).
/// Connects to a known device, auto-probes the GATT profile
/// (<see cref="BleProfileResolver"/>), feeds reads from notifications (no busy-poll),
/// and chunks writes to the profile's write size.
///
/// The wrapper is deliberately thin — all selection logic lives in the pure resolver.
/// Hardware check against a real Vgate iCar Pro: pending (working rule 4).
/// </summary>
public sealed class PluginBleElmTransport : IConnectionAwareTransport
{
    private readonly IBleAdapter _adapter;
    private readonly Guid _deviceId;
    private readonly ResolvedBleProfile? _forcedProfile;
    private readonly ILogger<PluginBleElmTransport> _logger;

    private readonly object _gate = new();
    private readonly Queue<byte> _rx = new();
    private readonly SemaphoreSlim _dataSignal = new(0);

    private IDevice? _device;
    private ICharacteristic? _writeCharacteristic;
    private ICharacteristic? _notifyCharacteristic;
    private ResolvedBleProfile? _activeProfile;

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

    public event EventHandler? ConnectionLost;

    /// <summary>The profile in use after a successful open.</summary>
    public ResolvedBleProfile? ActiveProfile => _activeProfile;

    public bool IsOpen { get; private set; }

    public async ValueTask OpenAsync(CancellationToken ct)
    {
        _device = await _adapter.ConnectToKnownDeviceAsync(_deviceId, cancellationToken: ct);

        var services = await _device.GetServicesAsync(ct);
        var topology = new List<GattServiceInfo>();
        foreach (var service in services)
        {
            var characteristics = await service.GetCharacteristicsAsync();
            topology.Add(new GattServiceInfo(
                service.Id,
                characteristics
                    .Select(c => new GattCharacteristicInfo(c.Id, c.CanWrite, c.CanUpdate))
                    .ToList()));
        }

        var resolved = _forcedProfile ?? BleProfileResolver.Resolve(topology)
            ?? throw new IOException(
                $"No compatible OBD GATT profile on device {_deviceId} " +
                $"({topology.Count} services discovered).");
        _logger.LogInformation("BLE profile resolved: {Profile}", resolved.Name);

        var gattService = services.First(s => s.Id == resolved.ServiceUuid);
        var characteristicsInService = await gattService.GetCharacteristicsAsync();
        _writeCharacteristic = characteristicsInService.First(c => c.Id == resolved.WriteCharacteristicUuid);
        _notifyCharacteristic = characteristicsInService.First(c => c.Id == resolved.NotifyCharacteristicUuid);

        _writeCharacteristic.WriteType = resolved.WriteWithResponse
            ? CharacteristicWriteType.WithResponse
            : CharacteristicWriteType.WithoutResponse;

        _notifyCharacteristic.ValueUpdated += OnValueUpdated;
        await _notifyCharacteristic.StartUpdatesAsync(ct);

        _adapter.DeviceDisconnected += OnDeviceDisconnected;
        _adapter.DeviceConnectionLost += OnDeviceDisconnected;

        _activeProfile = resolved;
        IsOpen = true;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var characteristic = _writeCharacteristic
            ?? throw new InvalidOperationException("Transport not open.");
        var chunkSize = _activeProfile?.MaxWriteSize ?? 20;

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
