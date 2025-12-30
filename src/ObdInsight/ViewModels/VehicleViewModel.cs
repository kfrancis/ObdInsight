using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObdInsight.Core.Vehicles;

namespace ObdInsight.ViewModels;

/// <summary>
/// ViewModel for displaying detailed vehicle data and diagnostics.
/// </summary>
public partial class VehicleViewModel : BaseViewModel
{
    private IVehicleObdService? _vehicleService;

    [ObservableProperty]
    private string? _vin;

    [ObservableProperty]
    private string? _manufacturer;

    [ObservableProperty]
    private string? _model;

    [ObservableProperty]
    private int? _year;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshBatteryDataCommand))]
    private bool _isElectricVehicle;

    [ObservableProperty]
    private double? _batterySoc;

    [ObservableProperty]
    private double? _batterySoh;

    [ObservableProperty]
    private double? _batteryVoltage;

    [ObservableProperty]
    private double? _batteryCurrent;

    [ObservableProperty]
    private double? _batteryTemperature;

    [ObservableProperty]
    private double? _rangeRemaining;

    [ObservableProperty]
    private string? _chargingStatus;

    [ObservableProperty]
    private double? _powerKw;

    // Standard OBD data
    [ObservableProperty]
    private double? _engineRpm;

    [ObservableProperty]
    private double? _vehicleSpeed;

    [ObservableProperty]
    private double? _coolantTemperature;

    [ObservableProperty]
    private double? _fuelLevel;

    [ObservableProperty]
    private IReadOnlySet<VehicleDataCategory>? _supportedCategories;

    public VehicleViewModel()
    {
        Title = "Vehicle Data";
    }

    /// <summary>
    /// Initializes the ViewModel with a vehicle service connection.
    /// </summary>
    public void Initialize(IVehicleObdService vehicleService)
    {
        ArgumentNullException.ThrowIfNull(vehicleService);
        _vehicleService = vehicleService;

        IsElectricVehicle = vehicleService.SupportsEvData;
        SupportedCategories = vehicleService.SupportedCategories;

        var profile = vehicleService.VehicleProfile;
        Manufacturer = profile.Manufacturer;
        Model = profile.Model;
    }

    /// <summary>
    /// Refreshes all vehicle data from the ECU.
    /// </summary>
    [RelayCommand]
    private async Task RefreshAllDataAsync(CancellationToken cancellationToken)
    {
        if (_vehicleService is null)
        {
            SetError("Not connected to vehicle.");
            return;
        }

        await ExecuteBusyAsync(async () =>
        {
            if (IsElectricVehicle)
            {
                await RefreshEvDataAsync(cancellationToken);
            }
            else
            {
                await RefreshStandardDataAsync(cancellationToken);
            }
        });
    }

    /// <summary>
    /// Refreshes EV-specific battery data.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRefreshBatteryData))]
    private async Task RefreshBatteryDataAsync(CancellationToken cancellationToken)
    {
        if (_vehicleService is null) return;

        await ExecuteBusyAsync(async () =>
        {
            var batteryInfo = await _vehicleService.GetBatteryInfoAsync(cancellationToken);
            if (batteryInfo is not null)
            {
                BatterySoc = batteryInfo.StateOfCharge;
                BatterySoh = batteryInfo.StateOfHealth;
                BatteryVoltage = batteryInfo.Voltage;
                BatteryCurrent = batteryInfo.Current;
                BatteryTemperature = batteryInfo.Temperature;
                RangeRemaining = batteryInfo.RangeRemaining;
                ChargingStatus = batteryInfo.ChargingStatus;
                PowerKw = batteryInfo.PowerKw;
            }
        });
    }

    private bool CanRefreshBatteryData() => IsElectricVehicle;

    private async Task RefreshEvDataAsync(CancellationToken cancellationToken)
    {
        if (_vehicleService is null) return;

        // Fetch battery data
        BatterySoc = await _vehicleService.GetBatterySocAsync(cancellationToken);
        BatterySoh = await _vehicleService.GetBatterySohAsync(cancellationToken);
        BatteryVoltage = await _vehicleService.GetBatteryVoltageAsync(cancellationToken);
        RangeRemaining = await _vehicleService.GetRangeRemainingAsync(cancellationToken);
        ChargingStatus = await _vehicleService.GetChargingStatusAsync(cancellationToken);
    }

    private async Task RefreshStandardDataAsync(CancellationToken cancellationToken)
    {
        if (_vehicleService is null) return;

        // TODO: Implement standard OBD data refresh
        // These would come from Mode 01 PIDs
        await Task.CompletedTask;
    }
}