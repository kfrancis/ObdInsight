namespace ObdInsight
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();

            // Force dark theme since the app is designed dark-first
            UserAppTheme = AppTheme.Dark;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}