using ObdInsight.Core.Adapters;
using ObdInsight.Core.Vehicles;

namespace ObdInsight.Drivers.Vehicles;

/// <summary>
/// Vehicle profile for Chevrolet Bolt EV/EUV (2017-2023).
/// Supports GM-specific extended PIDs for EV data.
/// </summary>
public class ChevroletBoltProfile : IVehicleProfile
{
    public string Name => "Chevrolet Bolt EV";
    public string Manufacturer => "Chevrolet";
    public string Model => "Bolt EV/EUV";
    public Range<int> SupportedYears => new(2017, 2023);
    public VehicleProtocol Protocol => VehicleProtocol.GmEv;
    public bool IsElectric => true;

    public IReadOnlyList<string> VinPrefixes { get; } =
    [
        "1G1",    // Chevrolet USA
        "2G1",    // Chevrolet Canada
        "3G1",    // Chevrolet Mexico
    ];

    public IReadOnlySet<VehicleDataCategory> SupportedCategories { get; } = new HashSet<VehicleDataCategory>
    {
        VehicleDataCategory.Battery,
        VehicleDataCategory.Charging,
        VehicleDataCategory.Range,
        VehicleDataCategory.Climate,
        VehicleDataCategory.Diagnostics,
        VehicleDataCategory.Movement
    };

    /// <summary>
    /// GM-specific PIDs for Bolt battery and EV data
    /// </summary>
    public IReadOnlyList<VehiclePid> CustomPids { get; } =
    [
        new VehiclePid(
            Name: "Battery SOC Display",
            Command: "228334",
            DataPoint: VehicleDataPoint.BatteryStateOfCharge,
            Unit: "%",
            Description: "High-voltage battery displayed SOC"
        )
        {
            Decoder = bytes => bytes.Length >= 2 ? ((bytes[0] << 8) | bytes[1]) / 100.0 : null
        },

        new VehiclePid(
            Name: "Battery Voltage",
            Command: "22430D",
            DataPoint: VehicleDataPoint.BatteryVoltage,
            Unit: "V",
            Description: "HV battery pack voltage"
        )
        {
            Decoder = bytes => bytes.Length >= 2 ? ((bytes[0] << 8) | bytes[1]) / 64.0 : null
        },

        new VehiclePid(
            Name: "Battery Current",
            Command: "22434F",
            DataPoint: VehicleDataPoint.BatteryCurrent,
            Unit: "A",
            Description: "HV battery current"
        )
        {
            Decoder = bytes =>
            {
                if (bytes.Length < 2) return null;
                var raw = (short)((bytes[0] << 8) | bytes[1]);
                return raw / 20.0;
            }
        },

        new VehiclePid(
            Name: "Estimated Range",
            Command: "222487",
            DataPoint: VehicleDataPoint.RangeRemaining,
            Unit: "km",
            Description: "Estimated remaining range"
        )
        {
            Decoder = bytes => bytes.Length >= 2 ? ((bytes[0] << 8) | bytes[1]) / 64.0 : null
        }
    ];

    public ObdCommand? GetCommand(VehicleDataPoint dataPoint)
    {
        var customPid = CustomPids.FirstOrDefault(p => p.DataPoint == dataPoint);
        if (customPid != null)
        {
            return new ObdCommand(customPid.Command, customPid.Timeout ?? TimeSpan.FromSeconds(5));
        }

        return dataPoint switch
        {
            VehicleDataPoint.Vin => new ObdCommand("0902", TimeSpan.FromSeconds(10)),
            VehicleDataPoint.DtcCodes => new ObdCommand("03", TimeSpan.FromSeconds(10)),
            VehicleDataPoint.Speed => ObdCommand.Create("010D"),
            VehicleDataPoint.AmbientTemp => ObdCommand.Create("0146"),
            _ => null
        };
    }

    public VehicleDataResult DecodeResponse(VehicleDataPoint dataPoint, byte[] responseBytes)
    {
        var customPid = CustomPids.FirstOrDefault(p => p.DataPoint == dataPoint);
        if (customPid?.Decoder != null)
        {
            try
            {
                var value = customPid.Decoder(responseBytes);
                return value != null
                    ? VehicleDataResult.Ok(dataPoint, value, customPid.Unit)
                    : VehicleDataResult.Fail(dataPoint, "Failed to decode response");
            }
            catch (Exception ex)
            {
                return VehicleDataResult.Fail(dataPoint, $"Decode error: {ex.Message}");
            }
        }

        return dataPoint switch
        {
            VehicleDataPoint.Speed when responseBytes.Length >= 1 =>
                VehicleDataResult.Ok(dataPoint, (int)responseBytes[0], "km/h"),
            VehicleDataPoint.AmbientTemp when responseBytes.Length >= 1 =>
                VehicleDataResult.Ok(dataPoint, responseBytes[0] - 40, "°C"),
            _ => VehicleDataResult.Fail(dataPoint, "Data point not supported")
        };
    }

    public bool MatchesVin(string vin)
    {
        if (string.IsNullOrWhiteSpace(vin) || vin.Length < 10)
            return false;

        var vinUpper = vin.ToUpperInvariant();

        // Check for GM prefix
        if (!VinPrefixes.Any(p => vinUpper.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Bolt EV VDS codes: FW, FV, FP (different trims/years)
        // Position 4-5 typically identifies the model
        var vds = vinUpper.Substring(3, 5);
        return vds.Contains("FW") || vds.Contains("FV") || vds.Contains("FP");
    }

    public IReadOnlyList<ObdCommand> GetInitializationCommands() =>
    [
        // GM uses standard ISO 15765-4 CAN for extended diagnostics
        ObdCommand.Create("ATSP6"),
        // Set header for BECM (Battery Energy Control Module)
        ObdCommand.Create("ATSH7E4"),
    ];
}