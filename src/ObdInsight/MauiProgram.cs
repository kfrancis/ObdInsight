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
                });

            // Register services
            builder.Services.AddSingleton<INavigationService, ShellNavigationService>();
            builder.Services.AddSingleton<IBleTransportFactory, PluginBleTransportFactory>();

            // Register ViewModels
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddTransient<DevicesViewModel>();
            builder.Services.AddTransient<VehicleViewModel>();

            // Register Pages
            builder.Services.AddSingleton<MainPage>();
            builder.Services.AddTransient<DevicesPage>();

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}