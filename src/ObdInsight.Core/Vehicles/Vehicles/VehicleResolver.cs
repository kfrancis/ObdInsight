using ObdInsight.Core.Communication.Elm327;

namespace ObdInsight.Core.Vehicles;

public enum VehicleDetectionStatus
{
    /// <summary>VIN read, variant matched, command set built.</summary>
    Detected,

    /// <summary>No profile's VIN-read mechanism got an answer.</summary>
    VinUnreadable,

    /// <summary>VIN read, but no registered profile recognizes it.</summary>
    UnsupportedVehicle,

    /// <summary>VIN matched a known variant that has no command set yet.</summary>
    VariantUnsupported,
}

/// <summary>
/// Outcome of VIN-driven vehicle detection. Never signals failure by exception —
/// inspect <see cref="Status"/>; <see cref="Commands"/> is non-null only for
/// <see cref="VehicleDetectionStatus.Detected"/>.
/// </summary>
public sealed record VehicleDetectionResult
{
    public required VehicleDetectionStatus Status { get; init; }
    public string? Vin { get; init; }
    public IVehicleProfile? Profile { get; init; }
    public VehicleVariantId? VariantId { get; init; }
    public IVehicleCommandSet? Commands { get; init; }
}

/// <summary>
/// VIN-driven vehicle selection (roadmap B6): read the VIN via each profile's own
/// mechanism, match it against profile variant detection, and build the command set —
/// no hardcoded vehicle anywhere in the flow.
/// </summary>
public static class VehicleResolver
{
    /// <summary>
    /// Resolves the connected vehicle over an initialized session.
    /// </summary>
    /// <param name="session">An initialized, protocol-locked ELM session.</param>
    /// <param name="profiles">
    /// Profiles to consider; defaults to <see cref="VehicleProfileRegistry.AllProfiles"/>.
    /// Pass explicitly in DI/AOT scenarios (the registry's reflection scan is
    /// trim-hostile — roadmap B12).
    /// </param>
    public static async ValueTask<VehicleDetectionResult> ResolveAsync(
        IElmSession session,
        IReadOnlyList<IVehicleProfile>? profiles = null,
        CancellationToken ct = default)
    {
        profiles ??= VehicleProfileRegistry.AllProfiles;

        // 1. VIN: try each profile's read mechanism until one answers. The first
        //    successful read wins — a VIN is universal once obtained.
        string? vin = null;
        foreach (var profile in profiles)
        {
            try
            {
                vin = await profile.TryReadVinAsync(session, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                vin = null; // contract says don't throw, but stay defensive
            }

            if (!string.IsNullOrWhiteSpace(vin))
            {
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(vin))
        {
            return new VehicleDetectionResult { Status = VehicleDetectionStatus.VinUnreadable };
        }

        // 2. Match the VIN against every profile's variant detection.
        foreach (var profile in profiles)
        {
            var variantId = profile.DetectVariantFromVin(vin);
            if (variantId is null)
            {
                continue;
            }

            if (!profile.SupportsVariant(variantId.Value))
            {
                return new VehicleDetectionResult
                {
                    Status = VehicleDetectionStatus.VariantUnsupported,
                    Vin = vin,
                    Profile = profile,
                    VariantId = variantId,
                };
            }

            try
            {
                return new VehicleDetectionResult
                {
                    Status = VehicleDetectionStatus.Detected,
                    Vin = vin,
                    Profile = profile,
                    VariantId = variantId,
                    Commands = profile.GetCommands(variantId.Value, session),
                };
            }
            catch (NotSupportedException)
            {
                // Backstop for profiles whose SupportsVariant is out of sync.
                return new VehicleDetectionResult
                {
                    Status = VehicleDetectionStatus.VariantUnsupported,
                    Vin = vin,
                    Profile = profile,
                    VariantId = variantId,
                };
            }
        }

        return new VehicleDetectionResult
        {
            Status = VehicleDetectionStatus.UnsupportedVehicle,
            Vin = vin,
        };
    }
}
