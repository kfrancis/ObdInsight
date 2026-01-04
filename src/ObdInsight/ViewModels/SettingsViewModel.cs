using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObdInsight.Pages;
using System.Collections.ObjectModel;

namespace ObdInsight.ViewModels;

/// <summary>
/// ViewModel for the settings page.
/// </summary>
public partial class SettingsViewModel : BaseViewModel
{
    [ObservableProperty]
    private string _activeVehicleName = "No Vehicle";

    [ObservableProperty]
    private string _selectedUnit = "Metric";

    [ObservableProperty]
    private string _appVersion = "1.0.0";

    public ObservableCollection<string> AvailableUnits { get; } = new()
    {
        "Imperial",
        "Metric"
    };

    public SettingsViewModel()
    {
        Title = "Settings";
    }

    [RelayCommand]
    private async Task ManageVehiclesAsync()
    {
        await Shell.Current.DisplayAlert("Coming Soon", "Vehicle management is not yet implemented.", "OK");
    }

    [RelayCommand]
    private async Task ManageCarProfileAsync()
    {
        await Shell.Current.GoToAsync(nameof(CarProfilePage));
    }

    [RelayCommand]
    private async Task ViewLicensesAsync()
    {
        await Shell.Current.DisplayAlert("Licenses", "Open source licenses will be displayed here.", "OK");
    }
}
