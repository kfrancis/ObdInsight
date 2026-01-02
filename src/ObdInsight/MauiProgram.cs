using Microsoft.Extensions.Logging;
using ObdInsight.Core.Transports.Ble;
using ObdInsight.Pages;
using ObdInsight.Services;
using ObdInsight.ViewModels;

namespace ObdInsight
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                    fonts.AddFont("fa-solid-900.ttf", "FontAwesome");
                });

            // Register services
            builder.Services.AddSingleton<INavigationService, ShellNavigationService>();
            builder.Services.AddSingleton<IBleTransportFactory, PluginBleTransportFactory>();
            builder.Services.AddSingleton<IConnectedDeviceService, ConnectedDeviceService>();
            builder.Services.AddSingleton<VehicleImageResolver>();

            // Register ViewModels
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddTransient<DevicesViewModel>();
            builder.Services.AddTransient<VehicleViewModel>();
            builder.Services.AddTransient<DiagnosticReportViewModel>();
            builder.Services.AddTransient<SettingsViewModel>();
            builder.Services.AddTransient<CarProfileViewModel>();

            // Register Pages
            builder.Services.AddSingleton<MainPage>();
            builder.Services.AddTransient<DevicesPage>();
            builder.Services.AddTransient<DiagnosticReportPage>();
            builder.Services.AddTransient<SettingsPage>();
            builder.Services.AddTransient<CarProfilePage>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}