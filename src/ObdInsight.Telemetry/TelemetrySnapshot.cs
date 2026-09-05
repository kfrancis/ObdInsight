using ObdInsight.Core.Vehicles;

namespace ObdInsight.Telemetry;

/// <summary>
///     One-shot diagnostic snapshot (pre-/post-check). Every field is nullable —
///     Null measurements mean unavailable. Diagnostic outcomes preserve read failures.
/// </summary>
public sealed record TelemetrySnapshot
{
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Vehicle Identification Number, when readable.</summary>
    public string? Vin { get; init; }

    /// <summary>State of charge (%).</summary>
    public decimal? SocPercent { get; init; }

    /// <summary>Battery pack voltage (V).</summary>
    public decimal? PackVoltageV { get; init; }

    /// <summary>Battery pack current (A; + discharge / − charge).</summary>
    public decimal? PackCurrentA { get; init; }

    /// <summary>Battery pack power (kW; + discharge / − charge).</summary>
    public decimal? PackPowerKw { get; init; }

    /// <summary>Battery pack temperature (°C).</summary>
    public decimal? PackTemperatureC { get; init; }

    /// <summary>State of health (%), not an OEM proxy such as Nissan Hx. Null when unavailable.</summary>
    public decimal? StateOfHealthPercent { get; init; }

    /// <summary>Remaining capacity (Ah).</summary>
    public decimal? CapacityAh { get; init; }

    /// <summary>Full per-cell voltage set (V).</summary>
    public IReadOnlyList<decimal?>? CellVoltagesV { get; init; }

    /// <summary>Minimum cell voltage (V).</summary>
    public decimal? CellVoltageMinV { get; init; }

    /// <summary>Maximum cell voltage (V).</summary>
    public decimal? CellVoltageMaxV { get; init; }

    /// <summary>Average cell voltage (V).</summary>
    public decimal? CellVoltageAverageV { get; init; }

    /// <summary>Vehicle speed (km/h).</summary>
    public decimal? VehicleSpeedKmh { get; init; }

    /// <summary>Remaining range estimate (km).</summary>
    public decimal? RemainingRangeKm { get; init; }

    /// <summary>Cabin/interior temperature (°C).</summary>
    public decimal? CabinTemperatureC { get; init; }

    /// <summary>Whether HVAC/climate control is active.</summary>
    public bool? HvacActive { get; init; }

    /// <summary>Odometer (km). Null until a provider exists (roadmap B13).</summary>
    public decimal? OdometerKm { get; init; }

    /// <summary>Charge cycle count. Null until a provider exists (roadmap B14).</summary>
    public decimal? ChargeCycleCount { get; init; }

    /// <summary>
    ///     Stored/pending outcomes and observed ECU coverage. Null only when no DTC
    ///     capability exists. A successful empty mode means no codes reported by its
    ///     responding ECUs, not proof of a fault-free vehicle.
    /// </summary>
    public DtcReadResult? DiagnosticTroubleCodes { get; init; }
}
