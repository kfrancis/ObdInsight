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
    private readonly VehicleImageResolver _vehicleImageResolver;

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

    // Mockup-oriented header + widget placeholders

    [ObservableProperty]
    private string _activeVehicleName = "No Vehicle";

    [ObservableProperty]
    private string _activeVehicleSubtitle = "Connect an adapter to begin";

    [ObservableProperty]
    private ImageSource _activeVehicleImage = VehicleImageResolver.PlaceholderImage;

    [ObservableProperty]
    private bool _showBatteryWidget;

    [ObservableProperty]
    private bool _showChargingWidget;

    [ObservableProperty]
    private bool _showSecondaryWidget;

    [ObservableProperty]
    private string _rangeDisplay = "--";

    [ObservableProperty]
    private string _rangeUnit = "mi";

    [ObservableProperty]
    private string _batteryPercentDisplay = "--";

    [ObservableProperty]
    private string _chargingStatusDisplay = "--";

    [ObservableProperty]
    private string _secondaryMetricValueDisplay = "--";

    [ObservableProperty]
    private string _secondaryMetricUnit = string.Empty;

    public MainViewModel(
        INavigationService navigationService,
        IConnectedDeviceService connectedDeviceService,
        VehicleImageResolver vehicleImageResolver)
    {
        ArgumentNullException.ThrowIfNull(navigationService);
        ArgumentNullException.ThrowIfNull(connectedDeviceService);
        ArgumentNullException.ThrowIfNull(vehicleImageResolver);

        _navigationService = navigationService;
        _connectedDeviceService = connectedDeviceService;
        _vehicleImageResolver = vehicleImageResolver;
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

            // Vehicle selection/detection is not wired yet.
            // Default the UI to the first targeted vehicle: Nissan Leaf.
            VehicleName = "Nissan Leaf";
            ConnectionStatus = $"Connected to {_connectedDeviceService.DeviceName}";

            this.ActiveVehicleName = VehicleName;
            this.ActiveVehicleSubtitle = AdapterName is null ? "Connected" : $"Adapter: {AdapterName}";
            this.ActiveVehicleImage = "vehicle_nissan_leaf.svg";

            // Widgets: show range always (still placeholder), show BEV widgets when we have their values.
            this.RangeDisplay = RangeRemaining.HasValue ? Math.Round(RangeRemaining.Value).ToString() : "--";
            this.RangeUnit = "mi";

            this.ShowBatteryWidget = BatterySoc.HasValue;
            this.BatteryPercentDisplay = BatterySoc.HasValue ? BatterySoc.Value.ToString("F0") : "--";

            this.ShowChargingWidget = !string.IsNullOrWhiteSpace(ChargingStatus);
            this.ChargingStatusDisplay = ChargingStatus ?? "--";

            this.ShowSecondaryWidget = BatteryVoltage.HasValue;
            this.SecondaryMetricValueDisplay = BatteryVoltage.HasValue ? BatteryVoltage.Value.ToString("F1") : "--";
            this.SecondaryMetricUnit = BatteryVoltage.HasValue ? "V" : string.Empty;
        }
        else
        {
            ClearVehicleData();
            ConnectionStatus = "Not Connected";

            this.ActiveVehicleName = "No Vehicle";
            this.ActiveVehicleSubtitle = "Connect an adapter to begin";
            this.ActiveVehicleImage = _vehicleImageResolver.Resolve(null);

            this.RangeDisplay = "--";
            this.RangeUnit = "mi";
            this.BatteryPercentDisplay = "--";
            this.ChargingStatusDisplay = "--";
            this.SecondaryMetricValueDisplay = "--";
            this.SecondaryMetricUnit = string.Empty;

            this.ShowBatteryWidget = false;
            this.ShowChargingWidget = false;
            this.ShowSecondaryWidget = false;
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