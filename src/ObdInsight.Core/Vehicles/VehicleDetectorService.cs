namespace ObdInsight.Core.Vehicles;

/// <summary>
/// Service for detecting vehicle type from VIN or ECU probing.
/// </summary>
/// <remarks>
/// The detector uses multiple strategies to identify a vehicle:
/// 1. VIN prefix matching (most reliable)
/// 2. Manufacturer-specific ECU probing
/// 3. PID fingerprinting (supported PID patterns)
/// 4. Fallback to generic OBD-II
///
/// Register vehicle profiles from ObdInsight.Drivers using VehicleProfileRegistry.RegisterAllProfiles()
/// </remarks>
public class VehicleDetectorService : IVehicleDetector
{
    private readonly List<IVehicleProfile> _profiles = [];
    private readonly StandardObdVehicleProfile _fallbackProfile = new();

    /// <summary>
    /// Creates a new vehicle detector with no pre-registered profiles.
    /// Use VehicleProfileRegistry.RegisterAllProfiles() to add profiles from Drivers.
    /// </summary>
    public VehicleDetectorService()
    {
        // Profiles are now registered externally via RegisterProfile()
        // This keeps Core independent of specific vehicle implementations
    }

    /// <inheritdoc />
    public IReadOnlyList<IVehicleProfile> RegisteredProfiles => _profiles;

    /// <inheritdoc />
    public void RegisterProfile(IVehicleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Avoid duplicates
        if (!_profiles.Any(p => p.Name == profile.Name && p.Manufacturer == profile.Manufacturer))
        {
            _profiles.Add(profile);
        }
    }

    /// <inheritdoc />
    public IVehicleProfile? DetectFromVin(string vin)
    {
        if (string.IsNullOrWhiteSpace(vin))
            return null;

        var vinInfo = VinInfo.Parse(vin);
        if (vinInfo == null)
            return null;

        // Try each registered profile
        foreach (var profile in _profiles.OrderByDescending(p => p.VinPrefixes.Count))
        {
            if (profile.MatchesVin(vin))
            {
                return profile;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<VehicleDetectionResult> DetectFromEcuAsync(
        IObdAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        // Step 1: Try to get VIN
        var vinResult = await TryGetVinAsync(adapter, cancellationToken);
        if (vinResult.vin != null)
        {
            var vinProfile = DetectFromVin(vinResult.vin);
            if (vinProfile != null)
            {
                return new VehicleDetectionResult(
                    Profile: vinProfile,
                    Method: VehicleDetectionMethod.VinMatch,
                    Confidence: 0.95f,
                    DetectedVin: vinResult.vin,
                    Notes: $"Matched VIN prefix to {vinProfile.Manufacturer} {vinProfile.Model}"
                );
            }
        }

        // Step 2: Try manufacturer-specific probes
        foreach (var profile in _profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probeResult = await TryProbeProfileAsync(adapter, profile, cancellationToken);
            if (probeResult.matched)
            {
                return new VehicleDetectionResult(
                    Profile: profile,
                    Method: VehicleDetectionMethod.ManufacturerProbe,
                    Confidence: probeResult.confidence,
                    DetectedVin: vinResult.vin,
                    Notes: probeResult.notes
                );
            }
        }

        // Step 3: Try PID fingerprinting
        var pidProfile = await DetectFromPidFingerprintAsync(adapter, cancellationToken);
        if (pidProfile != null)
        {
            return new VehicleDetectionResult(
                Profile: pidProfile,
                Method: VehicleDetectionMethod.PidFingerprint,
                Confidence: 0.6f,
                DetectedVin: vinResult.vin,
                Notes: "Matched based on supported PID pattern"
            );
        }

        // Fall back to generic OBD-II
        return new VehicleDetectionResult(
            Profile: _fallbackProfile,
            Method: VehicleDetectionMethod.FallbackGeneric,
            Confidence: 1.0f,
            DetectedVin: vinResult.vin,
            Notes: "No specific vehicle profile matched; using standard OBD-II"
        );
    }

    private static async Task<(string? vin, bool success)> TryGetVinAsync(
        IObdAdapter adapter,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await adapter.SendCommandAsync(
                new ObdCommand("0902", TimeSpan.FromSeconds(10)),
                cancellationToken);

            if (!response.Success || string.IsNullOrEmpty(response.Value))
                return (null, false);

            var vin = ParseVinResponse(response.Value);
            return (vin, vin != null);
        }
        catch
        {
            return (null, false);
        }
    }

    private static async Task<(bool matched, float confidence, string? notes)> TryProbeProfileAsync(
        IObdAdapter adapter,
        IVehicleProfile profile,
        CancellationToken cancellationToken)
    {
        // For EV profiles, try to query battery data as a probe
        if (profile.IsElectric && profile.CustomPids.Count > 0)
        {
            // Get the first battery-related PID to probe
            var probePid = profile.CustomPids
                .FirstOrDefault(p => p.DataPoint == VehicleDataPoint.BatteryStateOfCharge);

            if (probePid != null)
            {
                try
                {
                    // First, run init commands for this profile
                    foreach (var initCmd in profile.GetInitializationCommands().Take(3))
                    {
                        await adapter.SendCommandAsync(initCmd, cancellationToken);
                    }

                    var response = await adapter.SendCommandAsync(
                        new ObdCommand(probePid.Command, TimeSpan.FromSeconds(5)),
                        cancellationToken);

                    if (response.Success && !string.IsNullOrEmpty(response.Value))
                    {
                        // Check for expected response header if specified
                        if (probePid.ExpectedHeader != null &&
                            response.Value.Contains(probePid.ExpectedHeader, StringComparison.OrdinalIgnoreCase))
                        {
                            return (true, 0.9f, $"Got valid response to {profile.Name} battery query");
                        }

                        // Got some response, moderately confident
                        if (!response.Value.Contains("NO DATA") && !response.Value.Contains("ERROR"))
                        {
                            return (true, 0.7f, $"Got response to {profile.Name} specific command");
                        }
                    }
                }
                catch
                {
                    // Probe failed, continue to next profile
                }
            }
        }

        return (false, 0f, null);
    }

    private async Task<IVehicleProfile?> DetectFromPidFingerprintAsync(
        IObdAdapter adapter,
        CancellationToken cancellationToken)
    {
        try
        {
            // Query supported PIDs
            var response = await adapter.SendCommandAsync(
                ObdCommand.Create("0100"),
                cancellationToken);

            if (!response.Success || string.IsNullOrEmpty(response.Value))
                return null;

            // Parse supported PIDs bitmap
            var supportedPids = ParseSupportedPidsBitmap(response.Value);

            // EV vehicles typically don't support fuel-related PIDs
            var hasFuelPids = supportedPids.Contains(0x2F); // Fuel level

            // Check for EV-specific patterns
            if (!hasFuelPids)
            {
                // Likely an EV - try to find best matching EV profile
                // For now, return null to fall through to generic
                // Future: More sophisticated fingerprinting
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseVinResponse(string response)
    {
        try
        {
            var hexData = response.Replace(" ", "").Replace("\n", "").Replace("\r", "");
            var vinBytes = new List<byte>();

            for (var i = 0; i < hexData.Length - 1; i += 2)
            {
                if (byte.TryParse(hexData.Substring(i, 2),
                    System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    if (b >= 0x20 && b <= 0x7E) // Printable ASCII
                        vinBytes.Add(b);
                }
            }

            var vin = System.Text.Encoding.ASCII.GetString(vinBytes.ToArray());
            return vin.Length >= 17 ? vin[..17] : null;
        }
        catch
        {
            return null;
        }
    }

    private static HashSet<byte> ParseSupportedPidsBitmap(string response)
    {
        var supported = new HashSet<byte>();
        var hexData = response.Replace(" ", "").Replace("\n", "").Replace("\r", "");

        // Skip header (4100)
        if (hexData.Length >= 12)
            hexData = hexData.Substring(4, 8);
        else
            return supported;

        if (uint.TryParse(hexData, System.Globalization.NumberStyles.HexNumber, null, out var bitmap))
        {
            for (byte i = 0; i < 32; i++)
            {
                if ((bitmap & (1u << (31 - i))) != 0)
                    supported.Add((byte)(i + 1));
            }
        }

        return supported;
    }
}