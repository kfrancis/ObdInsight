using ObdInsight.ViewModels;

namespace ObdInsight.Pages;

/// <summary>
/// Car Profile configuration page
/// </summary>
public partial class CarProfilePage : ContentPage
{
    public CarProfilePage(CarProfileViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
