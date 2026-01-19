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

    public override IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session) =>
        variantId.Value switch
        {
            "AZE0-2-2016-2017" => new LeafAze0CommandSet(session),
            _ => throw new NotSupportedException($"Unknown/unsupported Leaf variant: {variantId.Value}")
        };
}
