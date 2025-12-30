namespace ObdInsight.Core.Vehicles;

/// <summary>
/// Generic OBD-II vehicle profile for standard protocol support.
/// Used as a fallback when no specific vehicle profile is detected.
/// </summary>
public class StandardObdVehicleProfile : IVehicleProfile
{
    public string Name => "Standard OBD-II Vehicle";
    public string Manufacturer => "Generic";
    public string Model => "OBD-II";
    public Range<int> SupportedYears => new(1996, DateTime.Now.Year + 1);
    public VehicleProtocol Protocol => VehicleProtocol.StandardObd2;
    public bool IsElectric => false;
    public IReadOnlyList<string> VinPrefixes => [];
    public IReadOnlyList<VehiclePid> CustomPids => [];

    public IReadOnlySet<VehicleDataCategory> SupportedCategories { get; } = new HashSet<VehicleDataCategory>
    {
        VehicleDataCategory.Engine,
        VehicleDataCategory.Diagnostics,
        VehicleDataCategory.Movement,
        VehicleDataCategory.Fuel
    };

    private static readonly Dictionary<VehicleDataPoint, (string Command, Func<byte[], object?> Decoder, string Unit)> StandardPidMap = new()
    {
        [VehicleDataPoint.Rpm] = ("010C", bytes => bytes.Length >= 2 ? ((bytes[0] * 256) + bytes[1]) / 4.0 : null, "rpm"),
        [VehicleDataPoint.Speed] = ("010D", bytes => bytes.Length >= 1 ? (int)bytes[0] : null, "km/h"),
        [VehicleDataPoint.CoolantTemp] = ("0105", bytes => bytes.Length >= 1 ? bytes[0] - 40 : null, "°C"),
        [VehicleDataPoint.IntakeTemp] = ("010F", bytes => bytes.Length >= 1 ? bytes[0] - 40 : null, "°C"),
        [VehicleDataPoint.ThrottlePosition] = ("0111", bytes => bytes.Length >= 1 ? bytes[0] * 100.0 / 255.0 : null, "%"),
        [VehicleDataPoint.EngineLoad] = ("0104", bytes => bytes.Length >= 1 ? bytes[0] * 100.0 / 255.0 : null, "%"),
        [VehicleDataPoint.FuelLevel] = ("012F", bytes => bytes.Length >= 1 ? bytes[0] * 100.0 / 255.0 : null, "%"),
        [VehicleDataPoint.AmbientTemp] = ("0146", bytes => bytes.Length >= 1 ? bytes[0] - 40 : null, "°C"),
    };

    public virtual ObdCommand? GetCommand(VehicleDataPoint dataPoint)
    {
        if (dataPoint == VehicleDataPoint.Vin)
        {
            return new ObdCommand("0902", TimeSpan.FromSeconds(10));
        }

        if (dataPoint == VehicleDataPoint.DtcCodes)
        {
            return new ObdCommand("03", TimeSpan.FromSeconds(10));
        }

        return StandardPidMap.TryGetValue(dataPoint, out var pidInfo)
            ? ObdCommand.Create(pidInfo.Command)
            : null;
    }

    public virtual VehicleDataResult DecodeResponse(VehicleDataPoint dataPoint, byte[] responseBytes)
    {
        if (!StandardPidMap.TryGetValue(dataPoint, out var pidInfo))
        {
            return VehicleDataResult.Fail(dataPoint, "Data point not supported");
        }

        try
        {
            var value = pidInfo.Decoder(responseBytes);
            return value != null
                ? VehicleDataResult.Ok(dataPoint, value, pidInfo.Unit)
                : VehicleDataResult.Fail(dataPoint, "Failed to decode response");
        }
        catch (Exception ex)
        {
            return VehicleDataResult.Fail(dataPoint, $"Decode error: {ex.Message}");
        }
    }

    public virtual bool MatchesVin(string vin) => false; // Generic profile doesn't match specific VINs

    public virtual IReadOnlyList<ObdCommand> GetInitializationCommands() => [];
}