namespace ObdInsight.Simulation;

/// <summary>
///     Vehicle state at one instant of simulated time. Values are physical (V, A, °C, km/h,
///     km, %); the transport encodes them onto simulated CAN frames / UDS responses.
/// </summary>
public sealed record LeafSimulationState
{
    public required double SocPercent { get; init; }
    public required double PackVoltage { get; init; }

    /// <summary>Positive = discharge, negative = charge/regen (matches BMS wire convention).</summary>
    public required double PackCurrentAmps { get; init; }

    public required double HxPercent { get; init; }
    public required double CapacityAh { get; init; }
    public required double PackTempC { get; init; }
    public required double SpeedKmh { get; init; }
    public required double CabinTempC { get; init; }
    public required double AmbientTempC { get; init; }
    public required bool HvacOn { get; init; }
    public required double RangeKm { get; init; }

    /// <summary>96 cell voltages in millivolts.</summary>
    public required int[] CellVoltagesMv { get; init; }
}

/// <summary>
///     Time-driven drive profile: simulated elapsed time → vehicle state. The default is a
///     deterministic 30-minute urban test drive with SOC drain, speed cycles, and pack
///     warming — enough signal movement to exercise a full pre-check → drive → post-check
///     consumer flow without hardware.
/// </summary>
public sealed class LeafDriveProfile
{
    private readonly Func<TimeSpan, LeafSimulationState> _stateAt;

    public LeafDriveProfile(Func<TimeSpan, LeafSimulationState> stateAt) => _stateAt = stateAt;

    /// <summary>30-minute urban drive: SOC 85 → ~70 %, speed 0–60 km/h cycles, pack 20 → 24.5 °C.</summary>
    public static LeafDriveProfile DefaultTestDrive { get; } = new(elapsed =>
    {
        var minutes = elapsed.TotalMinutes;
        var soc = Math.Max(5.0, 85.0 - minutes * 0.5);

        // Urban speed cycle: accelerate/decelerate on a 2-minute period, standstill
        // for the first 15 s (pre-check window).
        var speed = minutes < 0.25
            ? 0.0
            : Math.Max(0.0, 60.0 * Math.Sin(Math.PI * ((minutes - 0.25) % 2.0) / 2.0));

        var packVoltage = 300.0 + soc; // crude linear OCV stand-in
        var current = speed * 1.5; // discharge grows with speed; 0 A at standstill
        var cellMv = (int)Math.Round(packVoltage / 96.0 * 1000.0);
        var cells = new int[96];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = cellMv + i % 5 - 2; // ±2 mV spread
        }

        return new LeafSimulationState
        {
            SocPercent = soc,
            PackVoltage = packVoltage,
            PackCurrentAmps = current,
            HxPercent = 92.4,
            CapacityAh = 56.2,
            PackTempC = 20.0 + minutes * 0.15,
            SpeedKmh = speed,
            CabinTempC = 22.0,
            AmbientTempC = 18.0,
            HvacOn = true,
            RangeKm = soc * 1.6,
            CellVoltagesMv = cells
        };
    });

    public LeafSimulationState StateAt(TimeSpan simulatedElapsed) => _stateAt(simulatedElapsed);
}
