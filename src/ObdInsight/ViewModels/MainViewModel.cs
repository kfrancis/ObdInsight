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

    public MainViewModel(INavigationService navigationService)
    {
        ArgumentNullException.ThrowIfNull(navigationService);
        _navigationService = navigationService;
        Title = "OBD Insight";
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
            // TODO: Implement data refresh from IVehicleObdService
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
            // TODO: Implement disconnect logic
            await Task.Delay(100); // Placeholder
            IsConnected = false;
            ConnectionStatus = "Disconnected";
            ClearVehicleData();
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
    public void OnDeviceConnected(string deviceName, string? vehicleProfile)
    {
        IsConnected = true;
        AdapterName = deviceName;
        VehicleName = vehicleProfile ?? "Unknown Vehicle";
        ConnectionStatus = $"Connected to {deviceName}";
    }
}