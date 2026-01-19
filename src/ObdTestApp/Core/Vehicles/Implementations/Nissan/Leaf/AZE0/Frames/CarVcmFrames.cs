using ObdInsight.SourceGeneration;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.AZE0;

/// <summary>
/// VCM power consumption and climate data frame for Nissan Leaf AZE0 platform (0x510)
/// Transmitted on CAR-CAN bus
/// </summary>
/// <remarks>
/// This frame contains integrated power consumption, climate control status, and ambient temperature.
/// VCM relays this data from A/C Auto Amp to eyebrow display and A/V unit.
/// </remarks>
[CanFrame(0x510, Description = "VCM power consumption, climate, and eco data (CAR-CAN)")]
public partial class VcmFrame_510_AZE0
{
    [CanSignal(10, 3,
        Description = "Charge mode indicator (0-3)",
        MinValue = 0, MaxValue = 3)]
    public partial int ChargeMode { get; init; }

    [CanSignal(31, 1,
        Description = "Climate control active flag",
        MinValue = 0, MaxValue = 1)]
    public partial bool ClimateControlActive { get; init; }

    [CanSignal(25, 6, Factor = 0.25, Unit = "kW",
        Description = "Climate control power consumption",
        MinValue = 0, MaxValue = 15.75)]
    public partial double ClimateControlPowerConsumption { get; init; }

    [CanSignal(19, 4,
        Description = "Eco indicator (0-15 scale)",
        MinValue = 0, MaxValue = 15)]
    public partial int EcoIndicator { get; init; }

    [CanSignal(47, 5,
        Description = "Eco tree growth level (0-31)",
        MinValue = 0, MaxValue = 31)]
    public partial int EcoTree { get; init; }

    [CanSignal(15, 5,
        Description = "Integrated A/C power consumption",
        MinValue = 0, MaxValue = 31)]
    public partial int IntegratedPowerConsumptionAc { get; init; }

    [CanSignal(23, 4,
        Description = "Integrated auxiliary power consumption",
        MinValue = 0, MaxValue = 15)]
    public partial int IntegratedPowerConsumptionAux { get; init; }

    [CanSignal(7, 8,
        Description = "Integrated motor power consumption (raw value)",
        MinValue = 0, MaxValue = 255)]
    public partial int IntegratedPowerConsumptionMotor { get; init; }

    [CanSignal(56, 8, Factor = 0.5, Offset = -40.0, Unit = "°C",
        Description = "Outside ambient temperature",
        MinValue = -40, MaxValue = 87.5)]
    public partial double OutsideAmbientTemperature { get; init; }

    [CanSignal(39, 5,
        Description = "Instantaneous auxiliary power consumption",
        MinValue = 0, MaxValue = 31)]
    public partial int PowerConsumptionAux { get; init; }
}
