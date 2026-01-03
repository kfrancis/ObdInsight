using ObdInsight.ViewModels;

namespace ObdInsight
{
    public partial class MainPage : ContentPage
    {
        private readonly MainViewModel _viewModel;

        public MainPage(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            BindingContext = viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            // Notify the ViewModel that the page is appearing so it can refresh data
            await _viewModel.OnAppearingAsync();
        }
    }
}