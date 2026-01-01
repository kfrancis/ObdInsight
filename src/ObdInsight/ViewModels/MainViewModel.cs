using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObdInsight.Services;

namespace ObdInsight.ViewModels;

/// <summary>
/// ViewModel for the main dashboard page showing vehicle status.
/// </summary>
public partial class MainViewModel : BaseViewModel
{
    private readonly INavigationService _navigationService;
    private readonly IConnectedDeviceService _connectedDeviceService;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatus = "Not Connected";

    [ObservableProperty]
    private string? _vehicleName;

    [ObservableProperty]
    private string? _adapterName;

    [ObservableProperty]
    private double? _batterySoc;

    [ObservableProperty]
    private double? _batteryVoltage;

    [ObservableProperty]
    private double? _rangeRemaining;

    [ObservableProperty]
    private string? _chargingStatus;

    public MainViewModel(INavigationService navigationService, IConnectedDeviceService connectedDeviceService)
    {
        ArgumentNullException.ThrowIfNull(navigationService);
        ArgumentNullException.ThrowIfNull(connectedDeviceService);

        _navigationService = navigationService;
        _connectedDeviceService = connectedDeviceService;
        Title = "OBD Insight";

        // Subscribe to connection changes
        _connectedDeviceService.ConnectionChanged += OnConnectionChanged;

        // Initialize from current state
        UpdateConnectionState();
    }

    private void OnConnectionChanged(object? sender, DeviceConnectionChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateConnectionState);
    }

    private void UpdateConnectionState()
    {
        IsConnected = _connectedDeviceService.IsConnected;

        if (IsConnected)
        {
            AdapterName = _connectedDeviceService.DeviceName;
            VehicleName = "Unknown Vehicle"; // Will be updated after vehicle detection
            ConnectionStatus = $"Connected to {_connectedDeviceService.DeviceName}";
        }
        else
        {
            ClearVehicleData();
            ConnectionStatus = "Not Connected";
        }
    }

    /// <summary>
    /// Navigates to the device selection page to scan for OBD adapters.
    /// </summary>
    [RelayCommand]
    private async Task ScanForDevicesAsync()
    {
        await _navigationService.NavigateToAsync("//devices");
    }

    /// <summary>
    /// Refreshes the current vehicle data.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshOrDisconnect))]
    private async Task RefreshDataAsync()
    {
        await ExecuteBusyAsync(async () =>
        {
            // TODO: Implement data refresh from IVehicleObdService using _connectedDeviceService.Transport
            await Task.Delay(100); // Placeholder
        });
    }

    /// <summary>
    /// Disconnects from the current device.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshOrDisconnect))]
    private async Task DisconnectAsync()
    {
        await ExecuteBusyAsync(async () =>
        {
            await _connectedDeviceService.DisconnectAsync();
            // State will be updated via ConnectionChanged event
        });
    }

    private bool CanRefreshOrDisconnect() => IsConnected;

    private void ClearVehicleData()
    {
        VehicleName = null;
        AdapterName = null;
        BatterySoc = null;
        BatteryVoltage = null;
        RangeRemaining = null;
        ChargingStatus = null;
    }

    /// <summary>
    /// Called when connection is established from the devices page.
    /// </summary>
    [Obsolete("Use IConnectedDeviceService instead - connection state is automatically tracked")]
    public void OnDeviceConnected(string deviceName, string? vehicleProfile)
    {
        // This method is kept for backwards compatibility but is no longer needed
        // as connection state is tracked via IConnectedDeviceService
    }
}