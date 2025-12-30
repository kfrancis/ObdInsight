namespace ObdInsight.Core.Vehicles;

/// <summary>
/// Service for automatically detecting vehicle type from VIN or supported PIDs.
/// </summary>
public interface IVehicleDetector
{
    /// <summary>
    /// Attempts to detect the vehicle profile from a VIN.
    /// </summary>
    /// <param name="vin">The 17-character Vehicle Identification Number</param>
    /// <returns>The matching vehicle profile, or null if unknown</returns>
    IVehicleProfile? DetectFromVin(string vin);

    /// <summary>
    /// Attempts to detect the vehicle profile by probing the ECU.
    /// This queries supported PIDs and manufacturer-specific commands.
    /// </summary>
    /// <param name="adapter">The OBD adapter to use for probing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The detected vehicle profile, or a generic OBD-II profile</returns>
    Task<VehicleDetectionResult> DetectFromEcuAsync(
        IObdAdapter adapter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all registered vehicle profiles.
    /// </summary>
    IReadOnlyList<IVehicleProfile> RegisteredProfiles { get; }

    /// <summary>
    /// Registers a custom vehicle profile.
    /// </summary>
    void RegisterProfile(IVehicleProfile profile);
}

/// <summary>
/// Result of vehicle detection attempt
/// </summary>
public record VehicleDetectionResult(
    IVehicleProfile Profile,
    VehicleDetectionMethod Method,
    float Confidence,
    string? DetectedVin = null,
    string? Notes = null
)
{
    /// <summary>
    /// Whether the detection found a specific vehicle profile (not just generic OBD-II)
    /// </summary>
    public bool IsSpecificVehicle => Method != VehicleDetectionMethod.FallbackGeneric;
}

/// <summary>
/// How the vehicle was detected
/// </summary>
public enum VehicleDetectionMethod
{
    /// <summary>Matched by VIN prefix</summary>
    VinMatch,

    /// <summary>Matched by supported PID fingerprint</summary>
    PidFingerprint,

    /// <summary>Matched by manufacturer-specific command response</summary>
    ManufacturerProbe,

    /// <summary>User manually selected the profile</summary>
    UserSelected,

    /// <summary>No specific match found, using generic OBD-II</summary>
    FallbackGeneric
}

/// <summary>
/// Information extracted from a VIN
/// </summary>
public record VinInfo(
    string Vin,
    string Wmi,           // World Manufacturer Identifier (chars 1-3)
    string Vds,           // Vehicle Descriptor Section (chars 4-9)
    string Vis,           // Vehicle Identifier Section (chars 10-17)
    string? Manufacturer,
    string? Country,
    int? ModelYear
)
{
    /// <summary>
    /// Parses a VIN string into its components
    /// </summary>
    public static VinInfo? Parse(string? vin)
    {
        if (string.IsNullOrWhiteSpace(vin) || vin.Length != 17)
            return null;

        vin = vin.ToUpperInvariant();

        var wmi = vin[..3];
        var vds = vin[3..9];
        var vis = vin[9..];

        var manufacturer = GetManufacturer(wmi);
        var country = GetCountry(wmi[0]);
        var modelYear = GetModelYear(vin[9]);

        return new VinInfo(vin, wmi, vds, vis, manufacturer, country, modelYear);
    }

    private static string? GetManufacturer(string wmi) => wmi switch
    {
        // Nissan
        "JN1" => "Nissan (Japan)",
        "JN6" => "Nissan (Japan)",
        "1N4" => "Nissan (USA)",
        "3N1" => "Nissan (Mexico)",
        "5N1" => "Nissan (USA)",

        // Tesla
        "5YJ" => "Tesla",
        "7SA" => "Tesla",

        // Chevrolet/GM
        "1G1" => "Chevrolet",
        "1GC" => "Chevrolet",
        "2G1" => "Chevrolet (Canada)",

        // Ford
        "1FA" => "Ford",
        "1FM" => "Ford",
        "3FA" => "Ford (Mexico)",

        // Toyota
        "JTD" => "Toyota",
        "4T1" => "Toyota (USA)",
        "5TD" => "Toyota (USA)",

        // Honda
        "JHM" => "Honda",
        "1HG" => "Honda (USA)",
        "2HG" => "Honda (Canada)",

        // BMW
        "WBA" => "BMW",
        "WBS" => "BMW M",
        "WBY" => "BMW i",

        // Volkswagen
        "WVW" => "Volkswagen",
        "3VW" => "Volkswagen (Mexico)",

        // Audi
        "WAU" => "Audi",

        _ => null
    };

    private static string? GetCountry(char code) => code switch
    {
        '1' or '4' or '5' => "USA",
        '2' => "Canada",
        '3' => "Mexico",
        'J' => "Japan",
        'K' => "South Korea",
        'S' => "United Kingdom",
        'W' => "Germany",
        'Z' => "Italy",
        'V' => "France/Spain",
        _ => null
    };

    private static int? GetModelYear(char code)
    {
        // Year codes: A=2010, B=2011, ..., Y=2030 (excluding I, O, Q, U, Z)
        // Then cycles: 1=2001/2031, 2=2002/2032, etc.
        return code switch
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
            >= '1' and <= '9' => 2000 + (code - '0'),
            _ => null
        };
    }
}