using ObdInsight.Pages;

namespace ObdInsight
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();

            // Register routes for navigation
            Routing.RegisterRoute("devices", typeof(DevicesPage));
        }
    }
}
