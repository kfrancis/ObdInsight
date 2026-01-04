namespace ObdInsight.Services;

public static class AppPreferences
{
    // Adapter auto-connect settings
    public const string AutoConnectLastAdapter = "auto_connect_last_adapter";
    public const string LastAdapterAddress = "last_adapter_address";
    public const string LastAdapterName = "last_adapter_name";
    public const string LastAdapterProfileName = "last_adapter_profile_name";

    // Car profile settings
    public const string CustomVehicleName = "custom_vehicle_name";
    public const string ShowRangeWidget = "show_range_widget";
    public const string ShowBatteryWidget = "show_battery_widget";
    public const string ShowEfficiencyWidget = "show_efficiency_widget";
    public const string ShowChargingWidget = "show_charging_widget";
    public const string ShowTirePressureWidget = "show_tire_pressure_widget";
    public const string ShowMotorPowerWidget = "show_motor_power_widget";
}
