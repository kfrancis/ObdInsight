using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf;

public class NissanLeaf : VehicleProfile
{
    private static readonly VehicleVariant s_gen1 = new(
        new VehicleVariantId("ZE0-2010-2012"),
        "Gen1 (2010–2012) ZE0",
        2010, 2012,
        "ZE0",
        new Dictionary<string, object?>
        {
            [VariantAttr.PackKwh] = 24,
            [VariantAttr.Motor] = "EM61",
            [VariantAttr.Chemistry] = "LMO Canary",
            [VariantAttr.MaxChargeVolts] = 392
        });

    private static readonly VehicleVariant s_gen2 = new(
        new VehicleVariantId("AZE0-0-2013-2014"),
        "Gen2 (2013–2014) AZE0-0",
        2013, 2014,
        "AZE0-0",
        new Dictionary<string, object?>
        {
            [VariantAttr.PackKwh] = 24,
            [VariantAttr.Motor] = "EM57",
            [VariantAttr.Chemistry] = "LMO Wolf",
            [VariantAttr.MaxChargeVolts] = 396
        });

    private static readonly VehicleVariant s_gen2_5 = new(
        new VehicleVariantId("AZE0-1-2013-2014"),
        "Gen2.5 (2013–2014) AZE0-1",
        2013, 2014,
        "AZE0-1",
        new Dictionary<string, object?>
        {
            [VariantAttr.PackKwh] = 24,
            [VariantAttr.Motor] = "EM57",
            [VariantAttr.Chemistry] = "LMO Lizard",
            [VariantAttr.MaxChargeVolts] = 396
        });

    private static readonly VehicleVariant s_gen3 = new(
        new VehicleVariantId("AZE0-2-2016-2017"),
        "Gen3 (2016–2017) AZE0-2",
        2016, 2017,
        "AZE0-2",
        new Dictionary<string, object?>
        {
            [VariantAttr.PackKwh] = 30,
            [VariantAttr.Motor] = "EM57",
            [VariantAttr.Chemistry] = "NMC",
            [VariantAttr.MaxChargeVolts] = 396
        });

    public override string Make => "Nissan";
    public override string Model => "Leaf";

    public override IReadOnlyList<VehicleVariant> Variants { get; } =
        [s_gen1, s_gen2, s_gen2_5, s_gen3];

    /// <summary>
    ///     Returns a set of vehicle commands appropriate for the specified Nissan Leaf variant and ELM327 session.
    /// </summary>
    /// <remarks>
    ///     Currently, only the "AZE0-2-2016-2017" variant is supported. To add support for additional
    ///     variants, extend this method accordingly.
    /// </remarks>
    /// <param name="variantId">
    ///     The identifier representing the Nissan Leaf vehicle variant for which commands are requested. Must correspond to
    ///     a supported variant.
    /// </param>
    /// <param name="session">The ELM327 session used to communicate with the vehicle. Cannot be null.</param>
    /// <returns>An <see cref="IVehicleCommandSet" /> instance containing commands for the specified vehicle variant.</returns>
    /// <exception cref="NotSupportedException">
    ///     Thrown if <paramref name="variantId" /> does not correspond to a supported
    ///     Nissan Leaf variant.
    /// </exception>
    public override IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session) =>
        variantId.Value switch
        {
            "AZE0-2-2016-2017" => new LeafAze0CommandSet(session),
            _ => throw new NotSupportedException($"Unknown/unsupported Leaf variant: {variantId.Value}")
        };

    /// <summary>
    ///     Keep in sync with <see cref="GetCommands" /> — detection legitimately returns
    ///     ZE0/AZE0-0/AZE0-1 variants that have no command set yet, and callers
    ///     (VehicleResolver) must get a clean "variant unsupported" instead of a throw.
    /// </summary>
    public override bool SupportsVariant(VehicleVariantId variantId) =>
        variantId.Value == "AZE0-2-2016-2017";

    /// <summary>
    ///     Leaf VIN read: Mode 21 PID 81 on the charger/IDENT ECU (0x797/0x79A) — Leafs
    ///     don't answer standard OBD Mode 09. Same mechanism across Leaf generations.
    /// </summary>
    public override async ValueTask<string?> TryReadVinAsync(
        IElmSession session, CancellationToken ct = default)
    {
        try
        {
            var identification = new LeafAze0VehicleIdentification(session, LeafAze0Contexts.Ident);
            return await identification.GetVinAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // silent ECU / adapter error — not this vehicle, or not awake
        }
    }

    /// <summary>
    ///     Identifies the Nissan Leaf vehicle variant based on the provided VIN, if possible.
    /// </summary>
    /// <remarks>
    ///     If the VIN does not correspond to a Nissan Leaf or cannot be mapped to a known variant, the
    ///     method returns <see langword="null" />. For certain model years with multiple possible variants, additional VIN
    ///     segments are used to distinguish between them.
    /// </remarks>
    /// <param name="vin">
    ///     The vehicle identification number (VIN) to analyze. Must be a valid VIN corresponding to a Nissan
    ///     Leaf.
    /// </param>
    /// <returns>
    ///     A <see cref="VehicleVariantId" /> representing the detected vehicle variant if identification is successful;
    ///     otherwise, <see langword="null" />.
    /// </returns>
    public override VehicleVariantId? DetectVariantFromVin(string vin)
    {
        if (!IsValidVin(vin))
        {
            return null;
        }

        // Verify it's a Nissan Leaf VIN
        var wmi = GetWmi(vin);
        if (!IsNissanLeafWmi(wmi))
        {
            return null;
        }

        var modelYear = DecodeModelYear(vin[9]);
        if (modelYear == null)
        {
            return null;
        }

        var matchingVariants = GetVariantsByYear(modelYear.Value);

        if (matchingVariants.Count == 0)
        {
            return null;
        }

        if (matchingVariants.Count == 1)
        {
            return matchingVariants[0].Id;
        }

        // Multiple variants for same year (e.g., Gen2 vs Gen2.5 in 2013-2014)
        // Use VDS to distinguish if possible
        var vds = GetVds(vin);
        return DistinguishVariantByVds(vds, matchingVariants);
    }

    /// <summary>
    ///     Determines whether the specified World Manufacturer Identifier (WMI) corresponds to a Nissan Leaf vehicle.
    /// </summary>
    /// <remarks>
    ///     Recognized Nissan Leaf WMIs include "JN1" (Japan), "1N4" (USA), and "SJN" (UK). The
    ///     comparison is case-sensitive.
    /// </remarks>
    /// <param name="wmi">
    ///     The WMI code to evaluate. Must be a non-null, three-character string representing the vehicle manufacturer and
    ///     country of origin.
    /// </param>
    /// <returns>true if the WMI matches a known Nissan Leaf code; otherwise, false.</returns>
    private static bool IsNissanLeafWmi(string wmi)
    {
        // Common Nissan Leaf WMIs:
        // JN1 = Nissan Japan
        // 1N4 = Nissan USA
        // SJN = Nissan UK
        return wmi is "JN1" or "1N4" or "SJN";
    }

    /// <summary>
    ///     Attempts to identify the vehicle variant based on the provided Vehicle Descriptor Section (VDS) and a list of
    ///     candidate variants.
    /// </summary>
    /// <remarks>
    ///     This method currently returns the identifier of the first candidate in the list. Accurate
    ///     variant identification requires VIN decoding data and may be enhanced in future implementations.
    /// </remarks>
    /// <param name="vds">
    ///     The Vehicle Descriptor Section (VDS) portion of the VIN, typically containing model and trim information. Must
    ///     not be null.
    /// </param>
    /// <param name="candidates">
    ///     A read-only list of possible vehicle variants to consider for matching. Must contain at least
    ///     one element.
    /// </param>
    /// <returns>The identifier of the matched vehicle variant if a match is found; otherwise, null.</returns>
    private static VehicleVariantId? DistinguishVariantByVds(string vds, IReadOnlyList<VehicleVariant> candidates)
    {
        // The only multi-candidate years in the variant list are 2013-2014
        // (Gen2 AZE0-0 vs Gen2.5 AZE0-1). That mid-cycle split is a trim/feature
        // change Nissan did NOT encode in the VIN — both use the same "AZ0" VDS —
        // so it is not VIN-distinguishable. Deliberate decision: return the earlier
        // platform (AZE0-0) as the conservative baseline; runtime disambiguation of
        // pack size happens at the UDS layer anyway (Group 01 response length
        // 39 vs 41 vs 49 bytes selects the 24/30/40 kWh field layout).
        _ = vds;
        return candidates.OrderBy(c => c.PlatformCode, StringComparer.Ordinal).First().Id;
    }
}
