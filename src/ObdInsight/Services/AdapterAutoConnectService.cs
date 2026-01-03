using ObdInsight.Core.Transports.Ble;

namespace ObdInsight.Services;

public sealed class AdapterAutoConnectService
{
    private readonly IBleTransportFactory _bleTransportFactory;
    private readonly IConnectedDeviceService _connectedDeviceService;

    public AdapterAutoConnectService(
        IBleTransportFactory bleTransportFactory,
        IConnectedDeviceService connectedDeviceService)
    {
        ArgumentNullException.ThrowIfNull(bleTransportFactory);
        ArgumentNullException.ThrowIfNull(connectedDeviceService);

        _bleTransportFactory = bleTransportFactory;
        _connectedDeviceService = connectedDeviceService;
    }

    public bool IsAutoConnectEnabled
    {
        get => Preferences.Default.Get(AppPreferences.AutoConnectLastAdapter, false);
        set => Preferences.Default.Set(AppPreferences.AutoConnectLastAdapter, value);
    }

    public (string? Address, string? Name, string? ProfileName) GetLastAdapter()
    {
        var address = Preferences.Default.Get<string?>(AppPreferences.LastAdapterAddress, null);
        var name = Preferences.Default.Get<string?>(AppPreferences.LastAdapterName, null);
        var profileName = Preferences.Default.Get<string?>(AppPreferences.LastAdapterProfileName, null);
        return (address, name, profileName);
    }

    public void SaveLastAdapter(string address, string name, string profileName)
    {
        if (string.IsNullOrWhiteSpace(address))
            return;

        Preferences.Default.Set(AppPreferences.LastAdapterAddress, address);
        Preferences.Default.Set(AppPreferences.LastAdapterName, name ?? string.Empty);
        Preferences.Default.Set(AppPreferences.LastAdapterProfileName, profileName ?? string.Empty);
    }

    public void ClearLastAdapter()
    {
        Preferences.Default.Remove(AppPreferences.LastAdapterAddress);
        Preferences.Default.Remove(AppPreferences.LastAdapterName);
        Preferences.Default.Remove(AppPreferences.LastAdapterProfileName);
    }

    public async Task<bool> TryAutoConnectAsync(CancellationToken ct = default)
    {
        if (!IsAutoConnectEnabled)
            return false;

        if (_connectedDeviceService.IsConnected)
            return false;

        var (address, name, profileName) = GetLastAdapter();
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var profile = !string.IsNullOrWhiteSpace(profileName)
            ? BleDeviceProfile.FindByName(profileName)
            : null;

        profile ??= BleDeviceProfile.VeepeakBle;

        var transport = _bleTransportFactory.CreateTransport(profile);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            var connected = await transport.ConnectAsync(address, timeoutCts.Token);
            if (!connected)
            {
                await CleanupTransportAsync(transport);
                return false;
            }

            _connectedDeviceService.SetConnectedDevice(
                transport,
                string.IsNullOrWhiteSpace(name) ? address : name,
                address,
                profile);

            return true;
        }
        catch
        {
            await CleanupTransportAsync(transport);
            throw;
        }
    }

    private static async Task CleanupTransportAsync(IBleTransport transport)
    {
        try
        {
            await transport.DisconnectAsync();
        }
        catch
        {
            // Ignore disconnect errors
        }

        if (transport is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            transport.Dispose();
        }
    }
}
