using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObdInsight.Services;

namespace ObdInsight.ViewModels;

/// <summary>
/// ViewModel for the Car Profile page
/// Allows users to configure which vehicle data categories display on the main screen
/// </summary>
public partial class CarProfileViewModel : BaseViewModel
{
    [ObservableProperty]
    private string _vehicleName = "My Car";

    [ObservableProperty]
    private string _vehicleImageSource = "vehicle_nissan_leaf.svg";

    [ObservableProperty]
    private string _rangeValue = "245 miles";

    [ObservableProperty]
    private string _batteryValue = "78%";

    [ObservableProperty]
    private string _efficiencyValue = "33.6 mi";

    [ObservableProperty]
    private string _chargingValue = "42 kW";

    [ObservableProperty]
    private bool _showRange = true;

    [ObservableProperty]
    private bool _showBattery = true;

    [ObservableProperty]
    private bool _showEfficiency = true;

    [ObservableProperty]
    private bool _showCharging = true;

    [ObservableProperty]
    private bool _showTirePressure = false;

    [ObservableProperty]
    private bool _showMotorPower = false;

    public CarProfileViewModel()
    {
        Title = "Car Profile";
        LoadSettings();
    }

    /// <summary>
    /// Loads settings from preferences
    /// </summary>
    private void LoadSettings()
    {
        VehicleName = Preferences.Default.Get(AppPreferences.CustomVehicleName, "My Car");
        ShowRange = Preferences.Default.Get(AppPreferences.ShowRangeWidget, true);
        ShowBattery = Preferences.Default.Get(AppPreferences.ShowBatteryWidget, true);
        ShowEfficiency = Preferences.Default.Get(AppPreferences.ShowEfficiencyWidget, true);
        ShowCharging = Preferences.Default.Get(AppPreferences.ShowChargingWidget, true);
        ShowTirePressure = Preferences.Default.Get(AppPreferences.ShowTirePressureWidget, false);
        ShowMotorPower = Preferences.Default.Get(AppPreferences.ShowMotorPowerWidget, false);
    }

    /// <summary>
    /// Opens vehicle editing dialog
    /// </summary>
    [RelayCommand]
    private async Task EditVehicleAsync()
    {
        await Shell.Current.DisplayAlert("Edit Vehicle", "Vehicle editing coming soon.", "OK");
    }

    /// <summary>
    /// Saves the profile configuration
    /// </summary>
    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        // Save all settings to preferences
        if (string.IsNullOrWhiteSpace(VehicleName))
        {
            await Shell.Current.DisplayAlert("Error", "Please enter a vehicle name.", "OK");
            return;
        }

        Preferences.Default.Set(AppPreferences.CustomVehicleName, VehicleName);
        Preferences.Default.Set(AppPreferences.ShowRangeWidget, ShowRange);
        Preferences.Default.Set(AppPreferences.ShowBatteryWidget, ShowBattery);
        Preferences.Default.Set(AppPreferences.ShowEfficiencyWidget, ShowEfficiency);
        Preferences.Default.Set(AppPreferences.ShowChargingWidget, ShowCharging);
        Preferences.Default.Set(AppPreferences.ShowTirePressureWidget, ShowTirePressure);
        Preferences.Default.Set(AppPreferences.ShowMotorPowerWidget, ShowMotorPower);

        await Shell.Current.DisplayAlert("Saved", $"Your car profile '{VehicleName}' has been saved.", "OK");
        await Shell.Current.GoToAsync("..");
    }
}
