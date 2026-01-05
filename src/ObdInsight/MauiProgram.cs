using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using ObdInsight.Core.Transports.Ble;
using ObdInsight.Core.Vehicles;
using ObdInsight.Drivers;
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
                    fonts.AddFont("fa-brands-400.ttf", "FontAwesomeBrands");
                });

            // Register services
            builder.Services.AddSingleton<INavigationService, ShellNavigationService>();
            builder.Services.AddSingleton<IBleTransportFactory, PluginBleTransportFactory>();
            builder.Services.AddSingleton<IConnectedDeviceService, ConnectedDeviceService>();
            builder.Services.AddSingleton<ObdDataService>(); // Will auto-start on creation
            builder.Services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
            builder.Services.AddSingleton<AdapterAutoConnectService>();
            builder.Services.AddSingleton<VehicleImageResolver>();
            builder.Services.AddSingleton<VehicleSessionService>();

            builder.Services.AddSingleton<IVehicleDetector>(sp =>
            {
                var detector = new VehicleDetectorService();
                VehicleProfileRegistry.RegisterAllProfiles(detector);
                return detector;
            });

            // Register VehicleDataStore as singleton for widget bindings
            builder.Services.AddSingleton<IVehicleDataStore>(sp =>
            {
                var logger = sp.GetService<ILogger<VehicleDataStore>>();
                return new VehicleDataStore(logger);
            });

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