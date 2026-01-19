using ObdTestApp.Core.Communication.Elm327;

namespace ObdTestApp.Core.Vehicles;

public interface IVehicleProfile
{
    string Make { get; }
    string Model { get; }
    IReadOnlyList<VehicleVariant> Variants { get; }

    VehicleVariantId? DetectVariantFromVin(string vin);

    IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session);
}

public abstract class VehicleProfile : IVehicleProfile
{
    public abstract string Make { get; }
    public abstract string Model { get; }
    public abstract IReadOnlyList<VehicleVariant> Variants { get; }

    /// <summary>
    /// Attempts to detect the vehicle variant from a VIN.
    /// Override in derived classes to implement vehicle-specific VIN parsing logic.
    /// </summary>
    /// <param name="vin">The 17-character Vehicle Identification Number</param>
    /// <returns>The detected variant ID, or null if detection failed</returns>
    public virtual VehicleVariantId? DetectVariantFromVin(string vin)
    {
        return null; // Default: no detection logic
    }

    public abstract IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session);

    /// <summary>
    /// Decodes the model year from VIN position 10 (standard across all manufacturers).
    /// </summary>
    protected static int? DecodeModelYear(char modelYearChar)
    {
        // VIN model year encoding (Position 10):
        // A=2010, B=2011, etc. (I, O, Q, U, Z are not used)
        // This repeats every 30 years
        return modelYearChar switch
        {
            'A' => 2010,
            'B' => 2011,
            'C' => 2012,
            'D' => 2013,
            'E' => 2014,
            'F' => 2015,
            'G' => 2016,
            'H' => 2017,
            'J' => 2018,
            'K' => 2019,
            'L' => 2020,
            'M' => 2021,
            'N' => 2022,
            'P' => 2023,
            'R' => 2024,
            'S' => 2025,
            'T' => 2026,
            'V' => 2027,
            'W' => 2028,
            'X' => 2029,
            'Y' => 2030,
            // Could extend to earlier years if needed (2001-2009)
            '1' => 2001,
            '2' => 2002,
            '3' => 2003,
            '4' => 2004,
            '5' => 2005,
            '6' => 2006,
            '7' => 2007,
            '8' => 2008,
            '9' => 2009,
            _ => null
        };
    }

    /// <summary>
    /// Gets the Vehicle Descriptor Section (VDS) - characters 4-9 of VIN.
    /// </summary>
    protected static string GetVds(string vin) => vin.Substring(3, 6);

    /// <summary>
    /// Gets the Vehicle Identifier Section (VIS) - characters 10-17 of VIN.
    /// </summary>
    protected static string GetVis(string vin) => vin[9..];

    /// <summary>
    /// Gets the World Manufacturer Identifier (WMI) - first 3 characters of VIN.
    /// </summary>
    protected static string GetWmi(string vin) => vin[..3];

    /// <summary>
    /// Validates that a VIN is properly formatted (17 characters, no I/O/Q).
    /// </summary>
    protected static bool IsValidVin(string? vin)
    {
        if (string.IsNullOrWhiteSpace(vin) || vin.Length != 17)
            return false;

        // VINs should not contain I, O, or Q to avoid confusion with 1, 0
        return !vin.Any(c => c is 'I' or 'O' or 'Q');
    }

    /// <summary>
    /// Filters variants by model year range.
    /// </summary>
    protected IReadOnlyList<VehicleVariant> GetVariantsByYear(int modelYear)
    {
        return [.. Variants.Where(v => modelYear >= v.YearFrom && modelYear <= (v.YearTo ?? int.MaxValue))];
    }
}
