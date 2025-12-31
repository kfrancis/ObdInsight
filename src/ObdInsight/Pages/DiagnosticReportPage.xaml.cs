using ObdInsight.ViewModels;

namespace ObdInsight.Pages;

public partial class DiagnosticReportPage : ContentPage
{
    public DiagnosticReportPage(DiagnosticReportViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
