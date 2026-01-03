using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObdInsight.Core.Adapters;
using ObdInsight.Core.Transports;
using ObdInsight.Core.Vehicles;
using ObdInsight.Drivers.Adapters;
using ObdInsight.Services;
using Microsoft.Extensions.Logging;

namespace ObdInsight.ViewModels;

/// <summary>
/// ViewModel for the main dashboard page showing vehicle status.
/// </summary>
public partial class MainViewModel : BaseViewModel
{
    private readonly IConnectedDeviceService _connectedDeviceService;
    private readonly ILogger<MainViewModel>? _logger;
    private readonly INavigationService _navigationService;
    private readonly IVehicleDataStore _vehicleDataStore;
    private readonly VehicleImageResolver _vehicleImageResolver;
    private readonly VehicleSessionService _vehicleSession;
    [ObservableProperty]
    private ImageSource _activeVehicleImage = VehicleImageResolver.PlaceholderImage;

    [ObservableProperty]
    private string _activeVehicleName = "No Vehicle";

    // Mockup-oriented header + widget placeholders
    [ObservableProperty]
    private string _activeVehicleSubtitle = "Connect an adapter to begin";

    [ObservableProperty]
    private string? _adapterName;

    [ObservableProperty]
    private string _batteryPercentDisplay = "--";

    [ObservableProperty]
    private double? _batterySoc;

    [ObservableProperty]
    private double? _batteryVoltage;

    [ObservableProperty]
    private string? _chargingStatus;

    [ObservableProperty]
    private string _chargingStatusDisplay = "--";

    [ObservableProperty]
    private string _connectionStatus = "Not Connected";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshDataCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private bool _isConnected;

    private CancellationTokenSource? _pollingCts;
    [ObservableProperty]
    private string _rangeDisplay = "--";

    [ObservableProperty]
    private double? _rangeRemaining;

    [ObservableProperty]
    private string _rangeUnit = "km";

    [ObservableProperty]
    private string _secondaryMetricUnit = string.Empty;

    [ObservableProperty]
    private string _secondaryMetricValueDisplay = "--";

    [ObservableProperty]
    private bool _showBatteryWidget = true;

    [ObservableProperty]
    private bool _showChargingWidget;

    [ObservableProperty]
    private bool _showSecondaryWidget;

    [ObservableProperty]
    private string? _vehicleName;

    private VehicleObdService? _vehicleObdService;
    public MainViewModel(
            INavigationService navigationService,
            IConnectedDeviceService connectedDeviceService,
            VehicleImageResolver vehicleImageResolver,
            IVehicleDataStore vehicleDataStore,
            VehicleSessionService vehicleSession,
            ILogger<MainViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(navigationService);
        ArgumentNullException.ThrowIfNull(connectedDeviceService);
        ArgumentNullException.ThrowIfNull(vehicleImageResolver);
        ArgumentNullException.ThrowIfNull(vehicleDataStore);
        ArgumentNullException.ThrowIfNull(vehicleSession);

        _navigationService = navigationService;
        _connectedDeviceService = connectedDeviceService;
        _vehicleImageResolver = vehicleImageResolver;
        _vehicleDataStore = vehicleDataStore;
        _vehicleSession = vehicleSession;
        _logger = logger;
        Title = "ObdInsight";

        // Subscribe to connection changes
        _connectedDeviceService.ConnectionChanged += OnConnectionChanged;

        // Subscribe to data store updates for UI refresh
        _vehicleDataStore.PropertyChanged += OnVehicleDataStorePropertyChanged;

        // Initialize from current state
        UpdateConnectionState();
    }

    /// <summary>
    /// Gets the VehicleDataStore for widget bindings.
    /// Widgets bind to this store to display vehicle data.
    /// </summary>
    public IVehicleDataStore VehicleDataStore => _vehicleDataStore;
    /// <summary>
    /// Called when the page appears. Refreshes data if connected.
    /// </summary>
    public async Task OnAppearingAsync()
    {
        _logger?.LogDebug("MainPage appearing, IsConnected={IsConnected}, HasVehicleService={HasService}",
            IsConnected, _vehicleObdService != null);

        if (!IsConnected)
        {
            _logger?.LogDebug("Not connected, skipping data refresh");
            return;
        }

        // If we're connected but don't have a vehicle service yet, initialize it
        if (_vehicleObdService == null)
        {
            _logger?.LogDebug("Vehicle service not initialized, initializing now...");
            await InitializeVehicleServiceAsync();
        }

        // Only refresh if we successfully initialized
        if (_vehicleObdService != null)
        {
            _logger?.LogDebug("Refreshing data...");
            await RefreshDataAsync();
        }
        else
        {
            _logger?.LogWarning("Vehicle service still null after initialization attempt");
        }
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
    /// Disconnects from the current device.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshOrDisconnect))]
    private async Task DisconnectAsync()
    {
        await ExecuteBusyAsync(async () =>
        {
            // Stop polling and clear data store
            _pollingCts?.Cancel();
            await _vehicleDataStore.StopPollingAsync();
            _vehicleDataStore.Clear();

            // Disconnect the vehicle service
            if (_vehicleObdService != null)
            {
                await _vehicleObdService.DisconnectAsync();
                _vehicleObdService = null;
            }

            await _connectedDeviceService.DisconnectAsync();
            // State will be updated via ConnectionChanged event
        });
    }

    private async Task InitializeVehicleServiceAsync()
    {
        _logger?.LogInformation("InitializeVehicleServiceAsync called. IsConnected={IsConnected}, HasTransport={HasTransport}",
            _connectedDeviceService.IsConnected, _connectedDeviceService.Transport != null);

        if (!_connectedDeviceService.IsConnected || _connectedDeviceService.Transport == null)
        {
            _logger?.LogWarning("Cannot initialize vehicle service: not connected or no transport");
            return;
        }

        try
        {
            _logger?.LogInformation("Initializing vehicle service...");

            // Create adapter for the transport
            var adapter = AdapterRegistry.CreateDefaultAdapter();
            _logger?.LogDebug("Created adapter: {AdapterType}", adapter.GetType().Name);

            // Use detected profile from session, or fall back to standard
            var profile = _vehicleSession.Profile ?? new StandardObdVehicleProfile();
            _logger?.LogInformation("Using vehicle profile: {Profile}, IsElectric={IsElectric}", profile.Name, profile.IsElectric);

            // Create the vehicle OBD service
            _vehicleObdService = new VehicleObdService(adapter, initialProfile: profile);

            // The BLE transport implements IObdTransport
            var transport = _connectedDeviceService.Transport;
            _logger?.LogDebug("Transport type: {TransportType}, IsObdTransport={IsObdTransport}",
                transport.GetType().Name, transport is IObdTransport);

            if (transport is IObdTransport obdTransport)
            {
                var options = new VehicleServiceOptions
                {
                    AutoDetectVehicle = false, // Already detected in DevicesViewModel
                    ManualProfile = profile,
                    RunVehicleInit = true
                };

                _logger?.LogDebug("Connecting vehicle service with options...");
                var connected = await _vehicleObdService.ConnectAsync(obdTransport, options);

                if (!connected)
                {
                    _logger?.LogWarning("Failed to connect vehicle service - ConnectAsync returned false");
                    _vehicleObdService = null;
                    return;
                }

                _logger?.LogInformation("Vehicle service connected successfully");
            }
            else
            {
                _logger?.LogWarning("Transport does not implement IObdTransport: {TransportType}", transport.GetType().Name);
                _vehicleObdService = null;
                return;
            }

            // Wire up the data store to the vehicle service
            _logger?.LogDebug("Setting vehicle service on data store...");
            _vehicleDataStore.SetVehicleService(_vehicleObdService);

            // Start polling for data
            _pollingCts?.Cancel();
            _pollingCts = new CancellationTokenSource();
            _logger?.LogDebug("Starting polling...");
            await _vehicleDataStore.StartPollingAsync(_pollingCts.Token);

            _logger?.LogInformation("Vehicle service initialized and polling started successfully");

            // Update UI to show EV widgets
            UpdateWidgetVisibility();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize vehicle service");
            _vehicleObdService = null;
        }
    }

    private void OnConnectionChanged(object? sender, DeviceConnectionChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            UpdateConnectionState();

            if (e.IsConnected)
            {
                // Initialize the vehicle service when we get connected
                await InitializeVehicleServiceAsync();
            }
        });
    }
    private void OnVehicleDataStorePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Update legacy properties from the data store for backward compatibility
        // These can be removed once all UI binds directly to VehicleDataStore
        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(IVehicleDataStore.BatterySoc):
                    BatterySoc = _vehicleDataStore.BatterySoc;
                    BatteryPercentDisplay = BatterySoc.HasValue ? BatterySoc.Value.ToString("F0") : "--";
                    ShowBatteryWidget = BatterySoc.HasValue || _vehicleDataStore.IsElectricVehicle;
                    break;
                case nameof(IVehicleDataStore.BatteryVoltage):
                    BatteryVoltage = _vehicleDataStore.BatteryVoltage;
                    SecondaryMetricValueDisplay = BatteryVoltage.HasValue ? BatteryVoltage.Value.ToString("F1") : "--";
                    SecondaryMetricUnit = BatteryVoltage.HasValue ? "V" : string.Empty;
                    ShowSecondaryWidget = BatteryVoltage.HasValue;
                    break;
                case nameof(IVehicleDataStore.RangeRemaining):
                    RangeRemaining = _vehicleDataStore.RangeRemaining;
                    RangeDisplay = RangeRemaining.HasValue ? Math.Round(RangeRemaining.Value).ToString() : "--";
                    break;
                case nameof(IVehicleDataStore.ChargingStatus):
                    ChargingStatus = _vehicleDataStore.ChargingStatus;
                    ChargingStatusDisplay = ChargingStatus ?? "--";
                    ShowChargingWidget = !string.IsNullOrWhiteSpace(ChargingStatus) || _vehicleDataStore.IsCharging;
                    break;
                case nameof(IVehicleDataStore.VehicleName):
                    VehicleName = _vehicleDataStore.VehicleName;
                    ActiveVehicleName = VehicleName ?? "No Vehicle";
                    break;
            }
        });
    }

    /// <summary>
    /// Refreshes the current vehicle data.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshOrDisconnect))]
    private async Task RefreshDataAsync()
    {
        if (!IsConnected)
            return;

        await ExecuteBusyAsync(async () =>
        {
            _logger?.LogDebug("Refreshing vehicle data...");

            // Refresh data through the VehicleDataStore
            await _vehicleDataStore.RefreshAsync();

            _logger?.LogDebug("Vehicle data refresh complete");
        });
    }

    /// <summary>
    /// Navigates to the device selection page to scan for OBD adapters.
    /// </summary>
    [RelayCommand]
    private async Task ScanForDevicesAsync()
    {
        await _navigationService.NavigateToAsync("//devices");
    }

    private void UpdateConnectionState()
    {
        IsConnected = _connectedDeviceService.IsConnected;

        if (IsConnected)
        {
            AdapterName = _connectedDeviceService.DeviceName;

            // Use detected vehicle name from session, or fall back to profile name
            VehicleName = _vehicleSession.Profile?.Name ?? "Unknown Vehicle";
            ConnectionStatus = $"Connected to {_connectedDeviceService.DeviceName}";

            ActiveVehicleName = VehicleName;
            ActiveVehicleSubtitle = AdapterName is null ? "Connected" : $"Adapter: {AdapterName}";
            ActiveVehicleImage = _vehicleImageResolver.Resolve(_vehicleSession.Profile);

            // Show widgets for EV by default when connected
            var isEv = _vehicleSession.Profile?.IsElectric ?? true; // Assume EV if unknown
            ShowBatteryWidget = isEv;
            ShowChargingWidget = isEv;
            ShowSecondaryWidget = isEv;

            // Widgets show placeholder values until data arrives
            RangeDisplay = RangeRemaining.HasValue ? Math.Round(RangeRemaining.Value).ToString() : "--";
            RangeUnit = "km";
            BatteryPercentDisplay = BatterySoc.HasValue ? BatterySoc.Value.ToString("F0") : "--";
            ChargingStatusDisplay = ChargingStatus ?? "--";
            SecondaryMetricValueDisplay = BatteryVoltage.HasValue ? BatteryVoltage.Value.ToString("F1") : "--";
            SecondaryMetricUnit = BatteryVoltage.HasValue ? "V" : string.Empty;
        }
        else
        {
            // Clean up when disconnected
            _pollingCts?.Cancel();
            _vehicleObdService = null;

            ClearVehicleData();
            ConnectionStatus = "Not Connected";

            ActiveVehicleName = "No Vehicle";
            ActiveVehicleSubtitle = "Connect an adapter to begin";
            ActiveVehicleImage = _vehicleImageResolver.Resolve(null);

            RangeDisplay = "--";
            RangeUnit = "km";
            BatteryPercentDisplay = "--";
            ChargingStatusDisplay = "--";
            SecondaryMetricValueDisplay = "--";
            SecondaryMetricUnit = string.Empty;

            ShowBatteryWidget = false;
            ShowChargingWidget = false;
            ShowSecondaryWidget = false;
        }
    }

    private void UpdateWidgetVisibility()
    {
        var isEv = _vehicleObdService?.VehicleProfile.IsElectric ?? _vehicleSession.Profile?.IsElectric ?? false;

        // Show EV widgets for electric vehicles
        ShowBatteryWidget = isEv;
        ShowChargingWidget = isEv;
        ShowSecondaryWidget = isEv;
    }
}