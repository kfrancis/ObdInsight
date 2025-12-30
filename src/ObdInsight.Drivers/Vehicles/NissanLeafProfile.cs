using ObdInsight.Core.Adapters;
using ObdInsight.Core.Vehicles;

namespace ObdInsight.Drivers.Vehicles;

/// <summary>
/// Vehicle profile for Nissan Leaf (2011-2024).
/// Supports standard OBD-II plus Nissan-specific CAR CAN protocol for EV data.
/// </summary>
/// <remarks>
/// The Nissan Leaf uses a custom CAR CAN protocol for accessing battery and EV data.
/// This profile configures the ELM327 adapter to communicate with the Leaf's
/// Battery Management System (BMS) at ECU address 0x79B.
/// </remarks>
public class NissanLeafProfile : IVehicleProfile
{
    /// <inheritdoc />
    public string Name => "Nissan Leaf";

    /// <inheritdoc />
    public string Manufacturer => "Nissan";

    /// <inheritdoc />
    public string Model => "Leaf";

    /// <inheritdoc />
    public Range<int> SupportedYears => new(2011, 2024);

    /// <inheritdoc />
    public VehicleProtocol Protocol => VehicleProtocol.NissanCarCan;

    /// <inheritdoc />
    public bool IsElectric => true;

    /// <summary>
    /// Nissan VIN prefixes for Leaf models
    /// </summary>
    public IReadOnlyList<string> VinPrefixes { get; } =
    [
        "JN1",    // Japan-built Leaf
        "1N4",    // US-built (Smyrna, TN)
        "3N1",    // Mexico-built
        "5N1",    // US-built (SUV, but included for completeness)
        "SJN",    // UK-built (Sunderland)
    ];

    /// <inheritdoc />
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
    /// Nissan Leaf-specific PIDs for battery and EV data.
    /// These use the CAR CAN protocol and require specific ELM327 initialization.
    /// </summary>
    public IReadOnlyList<VehiclePid> CustomPids { get; } =
    [
        // Battery Management System (BMS) - ECU 0x79B responds on 0x7BB
        new VehiclePid(
            Name: "Battery SOC",
            Command: "022101",
            DataPoint: VehicleDataPoint.BatteryStateOfCharge,
            Unit: "%",
            Description: "High-voltage battery state of charge"
        )
        {
            ExpectedHeader = "7BB",
            Decoder = bytes => DecodeBatterySoc(bytes)
        },

        new VehiclePid(
            Name: "Battery SOH",
            Command: "022105",
            DataPoint: VehicleDataPoint.BatteryStateOfHealth,
            Unit: "%",
            Description: "Battery state of health (capacity remaining)"
        )
        {
            ExpectedHeader = "7BB",
            Decoder = bytes => DecodeBatterySoh(bytes)
        },

        new VehiclePid(
            Name: "Battery Voltage",
            Command: "022101",
            DataPoint: VehicleDataPoint.BatteryVoltage,
            Unit: "V",
            Description: "High-voltage battery pack voltage"
        )
        {
            ExpectedHeader = "7BB",
            Decoder = bytes => DecodeBatteryVoltage(bytes)
        },

        new VehiclePid(
            Name: "Battery Current",
            Command: "022101",
            DataPoint: VehicleDataPoint.BatteryCurrent,
            Unit: "A",
            Description: "Battery current (positive = discharging)"
        )
        {
            ExpectedHeader = "7BB",
            Decoder = bytes => DecodeBatteryCurrent(bytes)
        },

        new VehiclePid(
            Name: "Battery Temperature",
            Command: "022104",
            DataPoint: VehicleDataPoint.BatteryTemp,
            Unit: "°C",
            Description: "Battery pack average temperature"
        )
        {
            ExpectedHeader = "7BB",
            ExpectedFrames = 7,
            Decoder = bytes => DecodeBatteryTemp(bytes)
        },

        new VehiclePid(
            Name: "Battery Capacity",
            Command: "022105",
            DataPoint: VehicleDataPoint.BatteryCapacity,
            Unit: "Ah",
            Description: "Current usable battery capacity"
        )
        {
            ExpectedHeader = "7BB",
            Decoder = bytes => DecodeBatteryCapacity(bytes)
        },

        // Charging information
        new VehiclePid(
            Name: "Charger Status",
            Command: "022111",
            DataPoint: VehicleDataPoint.ChargingStatus,
            Unit: "",
            Description: "Current charging state"
        )
        {
            ExpectedHeader = "7BB",
            Decoder = bytes => DecodeChargingStatus(bytes)
        },

        // Range estimation
        new VehiclePid(
            Name: "Range Remaining",
            Command: "022106",
            DataPoint: VehicleDataPoint.RangeRemaining,
            Unit: "km",
            Description: "Estimated remaining range"
        )
        {
            ExpectedHeader = "7BB",
            Decoder = bytes => DecodeRange(bytes)
        },

        // Odometer (via instrument cluster)
        new VehiclePid(
            Name: "Odometer",
            Command: "022102",
            DataPoint: VehicleDataPoint.Odometer,
            Unit: "km",
            Description: "Total distance traveled"
        )
        {
            Decoder = bytes => DecodeOdometer(bytes)
        }
    ];

    /// <inheritdoc />
    public ObdCommand? GetCommand(VehicleDataPoint dataPoint)
    {
        // Check custom Leaf PIDs first
        var customPid = CustomPids.FirstOrDefault(p => p.DataPoint == dataPoint);
        if (customPid != null)
        {
            return new ObdCommand(customPid.Command, customPid.Timeout ?? TimeSpan.FromSeconds(5));
        }

        // Fall back to standard OBD-II for basic data
        return dataPoint switch
        {
            VehicleDataPoint.Vin => new ObdCommand("0902", TimeSpan.FromSeconds(10)),
            VehicleDataPoint.DtcCodes => new ObdCommand("03", TimeSpan.FromSeconds(10)),
            VehicleDataPoint.Speed => ObdCommand.Create("010D"),
            VehicleDataPoint.AmbientTemp => ObdCommand.Create("0146"),
            _ => null
        };
    }

    /// <inheritdoc />
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

        // Standard OBD-II fallback
        return dataPoint switch
        {
            VehicleDataPoint.Speed when responseBytes.Length >= 1 =>
                VehicleDataResult.Ok(dataPoint, (int)responseBytes[0], "km/h"),
            VehicleDataPoint.AmbientTemp when responseBytes.Length >= 1 =>
                VehicleDataResult.Ok(dataPoint, responseBytes[0] - 40, "°C"),
            _ => VehicleDataResult.Fail(dataPoint, "Data point not supported")
        };
    }

    /// <inheritdoc />
    public bool MatchesVin(string vin)
    {
        if (string.IsNullOrWhiteSpace(vin) || vin.Length < 3)
            return false;

        var vinUpper = vin.ToUpperInvariant();

        // Check WMI (first 3 chars) against known Nissan prefixes
        if (!VinPrefixes.Any(prefix => vinUpper.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return false;

        // For Nissan VINs, check position 4-6 for Leaf model codes
        // AZ0/AZE = US/Japan Leaf
        // ZE0 = Gen 1 Leaf (2011-2017)
        // ZE1 = Gen 2 Leaf (2018-2024)
        if (vin.Length >= 6)
        {
            var modelCode = vinUpper.Substring(3, 3);
            return modelCode.StartsWith("AZ") ||  // US/Japan Leaf (AZ0, AZE, etc.)
                   modelCode.StartsWith("ZE0") || // Japan Gen 1
                   modelCode.StartsWith("ZE1");   // Japan Gen 2
        }

        return false;
    }

    /// <summary>
    /// Initialization commands to set up ELM327 for Nissan Leaf CAR CAN protocol
    /// </summary>
    public IReadOnlyList<ObdCommand> GetInitializationCommands() =>
    [
        // Set protocol to ISO 15765-4 CAN (11 bit ID, 500 kbaud)
        ObdCommand.Create("ATSP6"),

        // Set CAN receive filter for BMS responses (0x7BB)
        ObdCommand.Create("ATCF7BB"),

        // Set CAN receive mask (accept only exact match)
        ObdCommand.Create("ATCM7FF"),

        // Set header for BMS requests (0x79B)
        ObdCommand.Create("ATSH79B"),

        // Enable CAN flow control
        ObdCommand.Create("ATFC SH79B"),
        ObdCommand.Create("ATFC SD300000"),
        ObdCommand.Create("ATFC SM1"),

        // Clear any previous errors
        ObdCommand.Create("ATCRA"),
    ];

    #region Decoders

    private static double? DecodeBatterySoc(byte[] data)
    {
        // SOC is typically at byte offset 32-33 in the response
        // Value = ((high << 8) | low) / 10000 * 100
        if (data.Length < 35)
            return null;

        var raw = (data[32] << 8) | data[33];
        return raw / 100.0; // Returns percentage
    }

    private static double? DecodeBatterySoh(byte[] data)
    {
        // SOH calculation based on capacity ratio
        // Nominal capacity vs current capacity
        if (data.Length < 30)
            return null;

        var soh = (data[26] << 8) | data[27];
        return soh / 100.0;
    }

    private static double? DecodeBatteryVoltage(byte[] data)
    {
        // Voltage at bytes 22-23: value / 100
        if (data.Length < 25)
            return null;

        var raw = (data[22] << 8) | data[23];
        return raw / 100.0;
    }

    private static double? DecodeBatteryCurrent(byte[] data)
    {
        // Current at bytes 24-25: signed value / 10
        if (data.Length < 27)
            return null;

        var raw = (short)((data[24] << 8) | data[25]);
        return raw / 10.0;
    }

    private static double? DecodeBatteryTemp(byte[] data)
    {
        // Temperature sensors are scattered through multi-frame response
        // Average the pack temperature sensors (typically 4)
        if (data.Length < 40)
            return null;

        // Simplified: return first temp sensor
        // Real implementation would average multiple sensors
        return data[10] - 40; // Offset like standard OBD temps
    }

    private static double? DecodeBatteryCapacity(byte[] data)
    {
        // Capacity in Ah at specific offset
        if (data.Length < 33)
            return null;

        var raw = (data[28] << 8) | data[29];
        return raw / 10.0;
    }

    private static string? DecodeChargingStatus(byte[] data)
    {
        if (data.Length < 5)
            return null;

        return data[4] switch
        {
            0x00 => "Not Charging",
            0x01 => "Level 1 (AC)",
            0x02 => "Level 2 (AC)",
            0x03 => "DC Fast Charging",
            0x04 => "Charging Complete",
            _ => $"Unknown ({data[4]:X2})"
        };
    }

    private static double? DecodeRange(byte[] data)
    {
        // Range in km at specific offset
        if (data.Length < 20)
            return null;

        var raw = (data[16] << 8) | data[17];
        return raw / 10.0;
    }

    private static int? DecodeOdometer(byte[] data)
    {
        // Odometer is typically 3 bytes
        if (data.Length < 7)
            return null;

        return (data[4] << 16) | (data[5] << 8) | data[6];
    }

    #endregion Decoders
}