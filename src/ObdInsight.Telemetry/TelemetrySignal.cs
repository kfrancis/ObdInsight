namespace ObdInsight.Telemetry;

/// <summary>
///     Normalized telemetry signals a consumer can subscribe to. Units are fixed per signal
///     (see <see cref="TelemetrySnapshot" /> field docs): %, V, A, kW, °C, km, km/h.
/// </summary>
public enum TelemetrySignal
{
    /// <summary>State of charge (%).</summary>
    StateOfCharge,

    /// <summary>Battery pack voltage (V).</summary>
    PackVoltage,

    /// <summary>Battery pack current (A; positive = discharge, negative = charge/regen).</summary>
    PackCurrent,

    /// <summary>Battery pack power (kW; positive = discharge, negative = charge/regen).</summary>
    PackPower,

    /// <summary>Battery pack temperature (°C).</summary>
    PackTemperature,

    /// <summary>Battery state of health (%; vehicle-specific metric, may exceed 100).</summary>
    StateOfHealth,

    /// <summary>Minimum cell voltage (V).</summary>
    CellVoltageMin,

    /// <summary>Maximum cell voltage (V).</summary>
    CellVoltageMax,

    /// <summary>Average cell voltage (V).</summary>
    CellVoltageAverage,

    /// <summary>Full per-cell voltage set (V), where the vehicle exposes it.</summary>
    CellVoltages,

    /// <summary>Vehicle speed (km/h).</summary>
    VehicleSpeed,

    /// <summary>Remaining range estimate (km).</summary>
    RemainingRange,

    /// <summary>Cabin/interior temperature (°C).</summary>
    CabinTemperature,

    /// <summary>Whether HVAC/climate control is active.</summary>
    HvacActive,

    /// <summary>Odometer (km). Reserved — no provider yet (roadmap B13).</summary>
    Odometer,

    /// <summary>Charge cycle count. Reserved — no provider yet (roadmap B14).</summary>
    ChargeCycleCount
}

/// <summary>Polling cadence tiers. Periods are configured via <see cref="TelemetrySessionOptions" />.</summary>
public enum CadenceTier
{
    /// <summary>Default every 1–2 s.</summary>
    High,

    /// <summary>Default every 5–10 s.</summary>
    Medium,

    /// <summary>Default every 30–60 s.</summary>
    Low
}

/// <summary>Per-signal availability as observed on the connected vehicle.</summary>
public enum SignalAvailability
{
    /// <summary>
    ///     No data seen yet; may still appear (e.g. broadcast frames that only
    ///     stream while driving). The session keeps polling these.
    /// </summary>
    Unknown,

    /// <summary>The latest assessment contains fresh, usable data for this signal.</summary>
    Available,

    /// <summary>
    ///     No provider exists, or the provider explicitly reports unsupported.
    ///     A timeout, missing reply, or invalid reading is not proof of unsupported hardware.
    /// </summary>
    Unavailable,

    /// <summary>The most recent published reading exceeded the configured maximum observation age.</summary>
    Stale
}
