using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ObdInsight.ViewModels;

/// <summary>
/// ViewModel for the Car Profile page
/// Allows users to configure which vehicle data categories display on the main screen
/// </summary>
public partial class CarProfileViewModel : BaseViewModel
{
    [ObservableProperty]
    private string _vehicleName = "2024 Nissan Leaf";

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
        // In a real implementation, this would persist the toggle states to settings
        await Shell.Current.DisplayAlert("Saved", "Your car profile has been updated.", "OK");
        await Shell.Current.GoToAsync("..");
    }
}
