using ObdInsight.Services;

namespace ObdInsight
{
    public partial class App : Application
    {
        private readonly AdapterAutoConnectService _autoConnectService;

        public App(AdapterAutoConnectService autoConnectService)
        {
            InitializeComponent();

            ArgumentNullException.ThrowIfNull(autoConnectService);
            _autoConnectService = autoConnectService;

            // Force dark theme since the app is designed dark-first
            UserAppTheme = AppTheme.Dark;

            _ = TryAutoConnectOnStartupAsync();
        }

        private async Task TryAutoConnectOnStartupAsync()
        {
            try
            {
                await _autoConnectService.TryAutoConnectAsync();
            }
            catch
            {
                // Best-effort: connection failure shouldn't block app startup.
            }
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}