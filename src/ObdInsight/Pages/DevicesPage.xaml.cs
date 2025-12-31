using ObdInsight.ViewModels;

namespace ObdInsight.Pages;

public partial class DevicesPage : ContentPage
{
    public DevicesPage(DevicesViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;

#if !DEBUG
        // Hide debug panel in Release builds
        DebugPanel.IsVisible = false;
#endif
    }
}