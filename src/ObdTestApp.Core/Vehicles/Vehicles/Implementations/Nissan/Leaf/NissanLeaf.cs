using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf;

public class NissanLeaf : VehicleProfile
{
    private static readonly VehicleVariant s_gen1 = new(
        new("ZE0-2010-2012"),
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
        new("AZE0-0-2013-2014"),
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
        new("AZE0-1-2013-2014"),
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
        new("AZE0-2-2016-2017"),
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
    /// Returns a set of vehicle commands appropriate for the specified Nissan Leaf variant and ELM327 session.
    /// </summary>
    /// <remarks>Currently, only the "AZE0-2-2016-2017" variant is supported. To add support for additional
    /// variants, extend this method accordingly.</remarks>
    /// <param name="variantId">The identifier representing the Nissan Leaf vehicle variant for which commands are requested. Must correspond to
    /// a supported variant.</param>
    /// <param name="session">The ELM327 session used to communicate with the vehicle. Cannot be null.</param>
    /// <returns>An <see cref="IVehicleCommandSet"/> instance containing commands for the specified vehicle variant.</returns>
    /// <exception cref="NotSupportedException">Thrown if <paramref name="variantId"/> does not correspond to a supported Nissan Leaf variant.</exception>
    public override IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session) =>
        variantId.Value switch
        {
            "AZE0-2-2016-2017" => new LeafAze0CommandSet(session),
            _ => throw new NotSupportedException($"Unknown/unsupported Leaf variant: {variantId.Value}")
        };

    /// <summary>
    /// Identifies the Nissan Leaf vehicle variant based on the provided VIN, if possible.
    /// </summary>
    /// <remarks>If the VIN does not correspond to a Nissan Leaf or cannot be mapped to a known variant, the
    /// method returns <see langword="null"/>. For certain model years with multiple possible variants, additional VIN
    /// segments are used to distinguish between them.</remarks>
    /// <param name="vin">The vehicle identification number (VIN) to analyze. Must be a valid VIN corresponding to a Nissan Leaf.</param>
    /// <returns>A <see cref="VehicleVariantId"/> representing the detected vehicle variant if identification is successful;
    /// otherwise, <see langword="null"/>.</returns>
    public override VehicleVariantId? DetectVariantFromVin(string vin)
    {
        if (!IsValidVin(vin))
            return null;

        // Verify it's a Nissan Leaf VIN
        var wmi = GetWmi(vin);
        if (!IsNissanLeafWmi(wmi))
            return null;

        var modelYear = DecodeModelYear(vin[9]);
        if (modelYear == null)
            return null;

        var matchingVariants = GetVariantsByYear(modelYear.Value);

        if (matchingVariants.Count == 0)
            return null;

        if (matchingVariants.Count == 1)
            return matchingVariants[0].Id;

        // Multiple variants for same year (e.g., Gen2 vs Gen2.5 in 2013-2014)
        // Use VDS to distinguish if possible
        var vds = GetVds(vin);
        return DistinguishVariantByVds(vds, matchingVariants);
    }

    /// <summary>
    /// Determines whether the specified World Manufacturer Identifier (WMI) corresponds to a Nissan Leaf vehicle.
    /// </summary>
    /// <remarks>Recognized Nissan Leaf WMIs include "JN1" (Japan), "1N4" (USA), and "SJN" (UK). The
    /// comparison is case-sensitive.</remarks>
    /// <param name="wmi">The WMI code to evaluate. Must be a non-null, three-character string representing the vehicle manufacturer and
    /// country of origin.</param>
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
    /// Attempts to identify the vehicle variant based on the provided Vehicle Descriptor Section (VDS) and a list of
    /// candidate variants.
    /// </summary>
    /// <remarks>This method currently returns the identifier of the first candidate in the list. Accurate
    /// variant identification requires VIN decoding data and may be enhanced in future implementations.</remarks>
    /// <param name="vds">The Vehicle Descriptor Section (VDS) portion of the VIN, typically containing model and trim information. Must
    /// not be null.</param>
    /// <param name="candidates">A read-only list of possible vehicle variants to consider for matching. Must contain at least one element.</param>
    /// <returns>The identifier of the matched vehicle variant if a match is found; otherwise, null.</returns>
    private static VehicleVariantId? DistinguishVariantByVds(string vds, IReadOnlyList<VehicleVariant> candidates)
    {
        // VDS position 4-5 often contains model/trim information
        // This would need actual Nissan VIN decoding tables to be accurate
        // For now, return first candidate (can be enhanced later)

        // Example logic (requires VIN decoding data):
        // var modelCode = vds.Substring(0, 2);
        // return modelCode switch
        // {
        //     "AZ" => candidates.FirstOrDefault(c => c.PlatformCode.StartsWith("AZE0"))?.Id,
        //     _ => candidates[0].Id
        // };

        return candidates[0].Id;
    }
}
