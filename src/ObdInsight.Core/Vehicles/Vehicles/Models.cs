using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ObdInsight.Core.Vehicles;

public enum GearPosition : byte
{
    Unknown = 0,
    Park = 1,
    Reverse = 2,
    Neutral = 3,
    Drive = 4,
    Eco = 7
}

public readonly record struct VehicleVariantId(string Value);

public sealed record VehicleVariant(
    VehicleVariantId Id,
    string DisplayName,
    int YearFrom,
    int? YearTo,
    string PlatformCode,   // "ZE0", "AZE0-2", "ZE1", "RE4", "RM4", etc. Use whatever makes sense per OEM.
    IReadOnlyDictionary<string, object?> Attributes
)
{
    public T? Get<T>(string key) => Attributes.TryGetValue(key, out var v) && v is T t ? t : default;
    public object? Get(string key) => Attributes.TryGetValue(key, out var v) ? v : null;
}

public static class VariantAttr
{
    public const string Chemistry = "battery.chemistry";

    public const string DisplacementL = "engine.displacementL";

    public const string Drivetrain = "drivetrain";

    // ICE/hybrid-ish attributes (CR-V)
    public const string Engine = "engine";

    public const string Hybrid = "powertrain.hybrid";

    public const string Induction = "engine.induction";

    public const string MaxChargeVolts = "battery.maxChargeVolts";

    public const string Motor = "motor";

    // EV-ish attributes (Leaf)
    public const string PackKwh = "pack.kwh";

    // NA / Turbo
    public const string Transmission = "transmission";
}

public sealed class HvacStatus
{
    public bool AcOn { get; init; }
    public int? AcPowerWatts { get; init; }
    public int? AmbientTempAc { get; init; }
    public bool ClimateControlOn { get; init; }
    public int? ClimateSetpoint { get; init; }
    public double? EvaporatorTempC { get; init; }
    public int? FanSpeed { get; init; }

    // 0-15 raw nibble (you can map later)
    public double? FanVoltageV { get; init; }

    // 50 W / bit
    public int? HeaterPowerWatts { get; init; }

    public double? InteriorIntakeTempC { get; init; }
    public double? OutsideAmbientTempC { get; init; }
    public bool RearDefrostOn { get; init; }
    // 300 W / bit
}

public readonly record struct BrakeStatus(bool BrakePressed, bool AbsActive);
