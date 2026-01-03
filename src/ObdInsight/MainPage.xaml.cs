using ObdInsight.ViewModels;

namespace ObdInsight
{
    public partial class MainPage : ContentPage
    {
        private readonly MainViewModel _viewModel;
        private const double MinHeroHeight = 120;
        private const double MaxHeroHeight = 400;
        private const double CollapseDistance = MaxHeroHeight - MinHeroHeight;
        
        private bool _isAnimating;
        private DateTime _lastScrollUpdate = DateTime.MinValue;
        private const int ScrollThrottleMs = 16; // ~60fps

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

        private void OnScrolled(object? sender, ScrolledEventArgs e)
        {
            // Throttle scroll updates to prevent overwhelming the UI thread
            var now = DateTime.UtcNow;
            if ((now - _lastScrollUpdate).TotalMilliseconds < ScrollThrottleMs)
                return;

            _lastScrollUpdate = now;

            // Prevent re-entrant calls
            if (_isAnimating)
                return;

            _isAnimating = true;

            try
            {
                var scrollY = e.ScrollY;

                // Calculate collapse ratio (0 = fully expanded, 1 = fully collapsed)
                var collapseRatio = Math.Clamp(scrollY / CollapseDistance, 0, 1);

                // Animate hero section height
                var newHeight = MaxHeroHeight - (scrollY * 0.8);
                HeroSection.HeightRequest = Math.Max(newHeight, MinHeroHeight);

                // Fade out the vehicle image as we scroll
                VehicleImage.Opacity = 1 - collapseRatio;
                VehicleImage.Scale = 1 - (collapseRatio * 0.5); // Scale down to 50%

                // Scale down and fade the vehicle name (reduce font size effect)
                var nameScale = 1 - (collapseRatio * 0.6); // Scale down to 40% of original
                VehicleNameLabel.Scale = nameScale;
                VehicleNameLabel.Opacity = 1 - (collapseRatio * 0.8);

                // Fade the subtitle
                VehicleSubtitleLabel.Opacity = 1 - collapseRatio;

                // Fade out connection status when collapsed
                ConnectionStatusGrid.Opacity = 1 - (collapseRatio * 1.2);

                // Keep the overlay visible but reduce opacity
                VehicleInfoOverlay.Opacity = Math.Max(1 - (collapseRatio * 0.7), 0.5);
            }
            finally
            {
                _isAnimating = false;
            }
        }
    }
}