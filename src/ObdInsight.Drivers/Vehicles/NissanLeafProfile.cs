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
/// Battery Management System (BMS/LBC) at ECU address 0x79B (responses on 0x7BB).
/// 
/// Based on OVMS (Open Vehicle Monitor System) Nissan Leaf implementation.
/// 
/// Key CAN addresses:
/// - BMS (LBC): TX=0x79B, RX=0x7BB
/// - Charger:   TX=0x797, RX=0x79A
/// - Broadcast: TX=0x7DF
/// 
/// The Leaf does NOT respond to standard OBD-II Mode 01 PIDs. Instead, it uses
/// Mode 21 (OBD-II Group) requests for battery data.
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
    /// These use the CAR CAN protocol with Mode 21 (OBD-II Group) requests.
    /// Requires setting header to 0x79B before sending commands.
    /// 
    /// Response data (from OVMS analysis):
    /// - Group 01: 39-51 bytes - SOC, Ah capacity, Hx, voltage/current
    /// - Group 02: 196 bytes - 96 cell voltages (2 bytes each) + pack/bus voltage
    /// - Group 04: 14-29 bytes - Battery temperatures
    /// - Group 06: 24 bytes - Cell balancing shunts
    /// - Group 61: 329 bytes - SOH (ZE1 only)
    /// </summary>
    public IReadOnlyList<VehiclePid> CustomPids { get; } =
    [
        // Battery Management System (BMS/LBC) - ECU 0x79B responds on 0x7BB
        // Mode 21 Group 01: Main battery data - MULTI-VALUE response
        new VehiclePid(
            Name: "Battery Group 01",
            Command: "2101",
            DataPoint: VehicleDataPoint.BatteryStateOfCharge,
            Unit: "%",
            Description: "Main battery data group: SOC, Ah, Hx, voltage, current"
        )
        {
            ExpectedHeader = "7BB",
            ExpectedFrames = 4, // 39-51 bytes needs multi-frame
            // This single command provides multiple data points
            ProvidesDataPoints =
            [
                VehicleDataPoint.BatteryStateOfCharge,
                VehicleDataPoint.BatteryStateOfHealth,
                VehicleDataPoint.BatteryCapacity,
                VehicleDataPoint.BatteryVoltage,
                VehicleDataPoint.BatteryCurrent
            ],
            MultiDecoder = DecodeGroup01MultiValue
        },

        // Mode 21 Group 02: Cell voltages
        new VehiclePid(
            Name: "Cell Voltages",
            Command: "2102",
            DataPoint: VehicleDataPoint.BatteryCellVoltages,
            Unit: "mV",
            Description: "Individual cell voltages (96 cells)"
        )
        {
            ExpectedHeader = "7BB",
            ExpectedFrames = 14, // 196 bytes needs multi-frame
            Decoder = bytes => DecodeCellVoltagesFromGroup02(bytes),
            ProvidesDataPoints =
            [
                VehicleDataPoint.BatteryCellVoltages,
                VehicleDataPoint.BatteryVoltage
            ],
            MultiDecoder = DecodeGroup02MultiValue
        },

        // Mode 21 Group 04: Temperatures
        new VehiclePid(
            Name: "Battery Temperature",
            Command: "2104",
            DataPoint: VehicleDataPoint.BatteryTemp,
            Unit: "°C",
            Description: "Battery pack temperature (averaged from sensors)"
        )
        {
            ExpectedHeader = "7BB",
            Decoder = bytes => DecodeBatteryTempFromGroup04(bytes)
        },

        // Mode 21 Group 61: SOH (ZE1 40kWh+ only)
        new VehiclePid(
            Name: "Battery SOH (ZE1)",
            Command: "2161",
            DataPoint: VehicleDataPoint.BatteryStateOfHealth,
            Unit: "%",
            Description: "Battery state of health (ZE1 models only)"
        )
        {
            ExpectedHeader = "7BB",
            ExpectedFrames = 24, // 329 bytes
            Decoder = bytes => DecodeSohFromGroup61(bytes)
        },

        // Charger ECU (0x797) - VIN and charge counts
        // Set header to 0x797 before these commands
        new VehiclePid(
            Name: "VIN",
            Command: "2181",  // Mode 21 PID 81
            DataPoint: VehicleDataPoint.Vin,
            Unit: "",
            Description: "Vehicle Identification Number from charger ECU"
        )
        {
            ExpectedHeader = "79A",
            Decoder = bytes => DecodeVinFromCharger(bytes)
        },

        // Charging status
        new VehiclePid(
            Name: "Charging Status",
            Command: "2101",
            DataPoint: VehicleDataPoint.ChargingStatus,
            Unit: "",
            Description: "Current charging state"
        )
        {
            ExpectedHeader = "7BB",
            Decoder = bytes => DecodeChargingStatus(bytes)
        },
    ];

    /// <inheritdoc />
    public ObdCommand? GetCommand(VehicleDataPoint dataPoint)
    {
        // Check custom Leaf PIDs first
        var customPid = CustomPids.FirstOrDefault(p => 
            p.DataPoint == dataPoint || 
            (p.ProvidesDataPoints?.Contains(dataPoint) ?? false));
            
        if (customPid != null)
        {
            return new ObdCommand(customPid.Command, customPid.Timeout ?? TimeSpan.FromSeconds(10));
        }

        // Fall back to standard OBD-II for basic data (may not work on Leaf)
        return dataPoint switch
        {
            VehicleDataPoint.Speed => ObdCommand.Create("010D"),
            VehicleDataPoint.AmbientTemp => ObdCommand.Create("0146"),
            _ => null
        };
    }

    /// <inheritdoc />
    public VehicleDataResult DecodeResponse(VehicleDataPoint dataPoint, byte[] responseBytes)
    {
        var customPid = CustomPids.FirstOrDefault(p => 
            p.DataPoint == dataPoint || 
            (p.ProvidesDataPoints?.Contains(dataPoint) ?? false));
            
        if (customPid != null)
        {
            // For multi-value PIDs, use the multi-decoder and extract the specific value
            if (customPid.MultiDecoder != null)
            {
                try
                {
                    var multiResult = customPid.MultiDecoder(responseBytes);
                    if (multiResult.TryGetValue(dataPoint, out var value) && value != null)
                    {
                        var unit = GetUnitForDataPoint(dataPoint);
                        return VehicleDataResult.Ok(dataPoint, value, unit);
                    }
                    return VehicleDataResult.Fail(dataPoint, "Data point not found in multi-value response");
                }
                catch (Exception ex)
                {
                    return VehicleDataResult.Fail(dataPoint, $"Multi-decode error: {ex.Message}");
                }
            }
            
            // Single-value decoder
            if (customPid.Decoder != null)
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
        }

        return VehicleDataResult.Fail(dataPoint, "Data point not supported");
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<VehicleDataPoint, VehicleDataResult> DecodeMultiResponse(
        string command,
        byte[] responseBytes)
    {
        var results = new Dictionary<VehicleDataPoint, VehicleDataResult>();
        
        var pid = CustomPids.FirstOrDefault(p => p.Command == command);
        if (pid?.MultiDecoder != null)
        {
            try
            {
                var multiValues = pid.MultiDecoder(responseBytes);
                foreach (var (dataPoint, value) in multiValues)
                {
                    if (value != null)
                    {
                        var unit = GetUnitForDataPoint(dataPoint);
                        results[dataPoint] = VehicleDataResult.Ok(dataPoint, value, unit);
                    }
                    else
                    {
                        results[dataPoint] = VehicleDataResult.Fail(dataPoint, "Null value decoded");
                    }
                }
            }
            catch (Exception ex)
            {
                // Return failure for all data points this command provides
                if (pid.ProvidesDataPoints != null)
                {
                    foreach (var dp in pid.ProvidesDataPoints)
                    {
                        results[dp] = VehicleDataResult.Fail(dp, $"Multi-decode error: {ex.Message}");
                    }
                }
            }
        }
        else if (pid?.Decoder != null)
        {
            var result = DecodeResponse(pid.DataPoint, responseBytes);
            results[pid.DataPoint] = result;
        }

        return results;
    }

    /// <inheritdoc />
    public IReadOnlyList<(ObdCommand Command, IReadOnlyList<VehicleDataPoint> DataPoints)> GetOptimizedCommands(
        IEnumerable<VehicleDataPoint> dataPoints)
    {
        var requested = dataPoints.ToHashSet();
        var result = new List<(ObdCommand, IReadOnlyList<VehicleDataPoint>)>();
        var covered = new HashSet<VehicleDataPoint>();

        // First, find multi-value PIDs that can satisfy multiple requests
        foreach (var pid in CustomPids.Where(p => p.IsMultiValue))
        {
            var providedPoints = pid.AllDataPoints.Where(dp => requested.Contains(dp) && !covered.Contains(dp)).ToList();
            if (providedPoints.Count > 0)
            {
                result.Add((new ObdCommand(pid.Command, pid.Timeout ?? TimeSpan.FromSeconds(10)), providedPoints));
                foreach (var dp in providedPoints)
                    covered.Add(dp);
            }
        }

        // Then, add single-value commands for any remaining data points
        foreach (var dp in requested.Where(dp => !covered.Contains(dp)))
        {
            var cmd = GetCommand(dp);
            if (cmd != null)
            {
                result.Add((cmd, new[] { dp }));
            }
        }

        return result;
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

    /// <inheritdoc />
    public IReadOnlyList<ObdCommand> GetInitializationCommands() =>
    [
        // Set protocol to ISO 15765-4 CAN (11 bit ID, 500 kbaud)
        new ObdCommand("ATSP6", TimeSpan.FromSeconds(3)),
        
        // Enable headers so we can see response addresses
        new ObdCommand("ATH1", TimeSpan.FromSeconds(2)),
        
        // Enable CAN auto formatting
        new ObdCommand("ATCAF1", TimeSpan.FromSeconds(2)),
        
        // Set header for BMS requests (0x79B)
        new ObdCommand("ATSH79B", TimeSpan.FromSeconds(2)),

        // Set up flow control for ISO-TP multi-frame responses
        new ObdCommand("ATFCSH79B", TimeSpan.FromSeconds(2)),  // Flow control header
        new ObdCommand("ATFCSD300000", TimeSpan.FromSeconds(2)),  // Flow control data (CTS, block size 0, separation time 0)
        new ObdCommand("ATFCSM1", TimeSpan.FromSeconds(2)),  // Flow control mode 1 (user defined)
    ];

    /// <summary>
    /// Gets the unit string for a specific data point
    /// </summary>
    private static string GetUnitForDataPoint(VehicleDataPoint dataPoint) => dataPoint switch
    {
        VehicleDataPoint.BatteryStateOfCharge => "%",
        VehicleDataPoint.BatteryStateOfHealth => "%",
        VehicleDataPoint.BatteryVoltage => "V",
        VehicleDataPoint.BatteryCurrent => "A",
        VehicleDataPoint.BatteryCapacity => "Ah",
        VehicleDataPoint.BatteryTemp => "°C",
        VehicleDataPoint.BatteryCellVoltages => "mV",
        VehicleDataPoint.RangeRemaining => "km",
        VehicleDataPoint.Speed => "km/h",
        VehicleDataPoint.ChargingStatus => "",
        VehicleDataPoint.Vin => "",
        _ => ""
    };

    #region Multi-Value Decoders

    /// <summary>
    /// Decode multiple values from Group 01 response
    /// Returns SOC, SOH (Hx), Ah capacity
    /// </summary>
    private static IReadOnlyDictionary<VehicleDataPoint, object?> DecodeGroup01MultiValue(byte[] data)
    {
        var results = new Dictionary<VehicleDataPoint, object?>();

        // SOC
        var soc = DecodeBatterySocFromGroup01(data);
        if (soc.HasValue)
            results[VehicleDataPoint.BatteryStateOfCharge] = soc.Value;

        // Hx (health indicator, correlates to SOH)
        var hx = DecodeHxFromGroup01(data);
        if (hx.HasValue)
            results[VehicleDataPoint.BatteryStateOfHealth] = hx.Value;

        // Ah capacity
        var ah = DecodeBatteryAhFromGroup01(data);
        if (ah.HasValue)
            results[VehicleDataPoint.BatteryCapacity] = ah.Value;

        // Note: Voltage and Current are actually in CAN message 0x1db, not Group 01
        // Leave them null here - would need direct CAN access

        return results;
    }

    /// <summary>
    /// Decode multiple values from Group 02 response
    /// Returns cell voltages and pack voltage
    /// </summary>
    private static IReadOnlyDictionary<VehicleDataPoint, object?> DecodeGroup02MultiValue(byte[] data)
    {
        var results = new Dictionary<VehicleDataPoint, object?>();

        var cellData = DecodeCellVoltagesFromGroup02(data);
        if (cellData != null)
        {
            results[VehicleDataPoint.BatteryCellVoltages] = cellData;

            // Extract pack voltage from cell data if it's an anonymous type
            if (cellData is { } cd)
            {
                var packVoltageProperty = cd.GetType().GetProperty("PackVoltage");
                if (packVoltageProperty?.GetValue(cd) is double packVoltage)
                {
                    results[VehicleDataPoint.BatteryVoltage] = packVoltage;
                }
            }
        }

        return results;
    }

    #endregion

    #region Single-Value Decoders - Based on OVMS Nissan Leaf Implementation

    /// <summary>
    /// Decode SOC from Group 01 response
    /// For ZE1 (40kWh+): SOC at bytes 31-33 as 24-bit value / 10000
    /// For ZE0/AZE0: SOC at byte 4 bits 6-0
    /// </summary>
    private static double? DecodeBatterySocFromGroup01(byte[] data)
    {
        if (data.Length >= 34)  // ZE1 format (51 bytes)
        {
            var soc = (data[31] << 16) | (data[32] << 8) | data[33];
            return soc / 10000.0;
        }
        
        // ZE0/AZE0 format - SOC from 0x1db CAN message, but in group response
        // it's typically not directly available, need to calculate from Hx or use instrument SOC
        return null;
    }

    /// <summary>
    /// Decode battery Ah capacity from Group 01
    /// Bytes 33-35 for ZE0/AZE0: ah10000 = (d[33]<<16 | d[34]<<8 | d[35]) / 10000
    /// Bytes 35-37 for ZE1: ah10000 = (d[35]<<16 | d[36]<<8 | d[37]) / 10000
    /// </summary>
    private static double? DecodeBatteryAhFromGroup01(byte[] data)
    {
        if (data.Length >= 51)  // ZE1 format
        {
            var ah10000 = (data[35] << 16) | (data[36] << 8) | data[37];
            return ah10000 / 10000.0;
        }
        else if (data.Length >= 39)  // ZE0/AZE0 format
        {
            var ah10000 = (data[33] << 16) | (data[34] << 8) | data[35];
            return ah10000 / 10000.0;
        }
        return null;
    }

    /// <summary>
    /// Decode Hx (health indicator) from Group 01
    /// For ZE0/AZE0: Hx at bytes 26-27, value / 100
    /// For ZE1: Hx at bytes 28-29, value / 102.4
    /// </summary>
    private static double? DecodeHxFromGroup01(byte[] data)
    {
        if (data.Length >= 51)  // ZE1 format
        {
            var hx = (data[28] << 8) | data[29];
            return hx / 102.4;  // From Dala's Leaf2018-CAN pdf
        }
        else if (data.Length >= 39)  // ZE0/AZE0 format
        {
            var hx = (data[26] << 8) | data[27];
            return hx / 100.0;
        }
        return null;
    }

    /// <summary>
    /// Decode cell voltages from Group 02 (196 bytes)
    /// 96 cells, 2 bytes each (mV), followed by pack voltage and bus voltage
    /// </summary>
    private static object? DecodeCellVoltagesFromGroup02(byte[] data)
    {
        if (data.Length < 196)
            return null;

        var voltages = new double[96];
        for (var i = 0; i < 96; i++)
        {
            var millivolt = (data[i * 2] << 8) | data[i * 2 + 1];
            if (millivolt < 5000)  // Ignore invalid readings
            {
                voltages[i] = millivolt / 1000.0;  // Convert to volts
            }
        }

        // Pack voltage at bytes 192-193
        var packVoltage = ((data[192] << 8) | data[193]) / 100.0;
        
        return new
        {
            CellVoltages = voltages,
            PackVoltage = packVoltage,
            MinCell = voltages.Where(v => v > 0).DefaultIfEmpty(0).Min(),
            MaxCell = voltages.Max(),
            AvgCell = voltages.Where(v => v > 0).DefaultIfEmpty(0).Average()
        };
    }

    /// <summary>
    /// Decode battery temperatures from Group 04 (14-29 bytes)
    /// Contains 4 thermistor readings at specific offsets
    /// Temperature = -0.102 * (thermistor - 710)
    /// </summary>
    private static double? DecodeBatteryTempFromGroup04(byte[] data)
    {
        if (data.Length < 14)
            return null;

        var temps = new List<double>();
        
        // Read 4 temperature sensors at offsets 0-2, 3-5, 6-8, 9-11
        // Format: 2-byte thermistor value, 1-byte temp in degC
        for (var i = 0; i < 4; i++)
        {
            var offset = i * 3;
            if (offset + 2 >= data.Length)
                break;

            var thermistor = (data[offset] << 8) | data[offset + 1];
            if (thermistor != 0xFFFF)  // Valid reading
            {
                var temp = -0.102 * (thermistor - 710);
                temps.Add(temp);
            }
            else
            {
                // Use the direct temp byte if thermistor is invalid
                var tempDirect = data[offset + 2];
                if (tempDirect != 0xFF)
                    temps.Add(tempDirect);
            }
        }

        return temps.Count > 0 ? temps.Average() : null;
    }

    /// <summary>
    /// Decode SOH from Group 61 (ZE1 40kWh+ only, 329 bytes)
    /// SOH at bytes 2-3 as 16-bit value / 100
    /// </summary>
    private static double? DecodeSohFromGroup61(byte[] data)
    {
        if (data.Length < 329)
            return null;

        var soh = (data[2] << 8) | data[3];
        return soh / 100.0;
    }

    /// <summary>
    /// Decode VIN from Charger ECU (Mode 21 PID 81, 19 bytes)
    /// </summary>
    private static string? DecodeVinFromCharger(byte[] data)
    {
        if (data.Length < 17)
            return null;

        // VIN is ASCII, may contain ESC (0x1B) characters in AZE0 models
        var vinChars = new char[17];
        for (var i = 0; i < 17 && i < data.Length; i++)
        {
            var c = data[i];
            // Replace ESC character with space, filter non-printable
            vinChars[i] = c == 0x1B ? ' ' : (c >= 0x20 && c <= 0x7E ? (char)c : ' ');
        }

        return new string(vinChars).Trim();
    }

    /// <summary>
    /// Decode charging status from Group 01
    /// </summary>
    private static string? DecodeChargingStatus(byte[] data)
    {
        if (data.Length < 2)
            return "Unknown";

        // This is a simplified decoder - actual charging status comes from
        // CAN messages 0x5bf (ZE0) or 0x390 (AZE0)
        // For now, return a placeholder based on available data
        return "Not Charging";
    }

    #endregion
}