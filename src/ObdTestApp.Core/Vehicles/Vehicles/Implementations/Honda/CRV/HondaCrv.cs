using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Protocols;
using ObdTestApp.Core.Vehicles;

namespace ObdTestApp.Core.Vehicles.Implementations
{
    public static class HondaCrvGen5Contexts
    {
        public static IReadOnlyList<EcuContext> All { get; } =
        [
            Null
        ];

        public static IReadOnlyDictionary<string, EcuContext> ByName { get; } =
            All.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

        public static EcuContext Null => ReqResp("NULL", "000", "000");

        private static EcuContext ReqResp(string name, string tx, string rx) => new()
        {
            Name = name,
            TxHeader = tx,
            RxFilter = rx,
            FlowControlHeader = tx,
            FlowControlData = "300000",
            FlowControlMode = "1",
            EnableHeaders = true,
            EnableAutoFormatting = true,
            CommunicationMode = EcuCommunicationMode.RequestResponse
        };
    }

    public class HondaCrv : VehicleProfile
    {
        private const string Gen1Key = "CRV-Gen1-1997-2001";
        private const string Gen2Key = "CRV-Gen2-2002-2006";
        private const string Gen3Key = "CRV-Gen3-2007-2011";
        private const string Gen4Key = "CRV-Gen4-2012-2016";
        private const string Gen5Key = "CRV-Gen5-2017-2022";
        private const string Gen6Key = "CRV-Gen6-2023+";

        private static readonly VehicleVariant s_gen1 = new(
        new(Gen1Key),
        "1st Gen (1997–2001)",
        1997, 2001,
        "Gen1",
        new Dictionary<string, object?>
        {
            [VariantAttr.Engine] = "2.0L I4",
            [VariantAttr.DisplacementL] = 2.0,
            [VariantAttr.Induction] = "NA"
        });

        private static readonly VehicleVariant s_gen2 = new(
            new(Gen2Key),
            "2nd Gen (2002–2006)",
            2002, 2006,
            "Gen2",
            new Dictionary<string, object?>
            {
                [VariantAttr.Engine] = "2.4L I4",
                [VariantAttr.DisplacementL] = 2.4,
                [VariantAttr.Induction] = "NA"
            });

        private static readonly VehicleVariant s_gen3 = new(
            new(Gen3Key),
            "3rd Gen (2007–2011)",
            2007, 2011,
            "Gen3",
            new Dictionary<string, object?>
            {
                [VariantAttr.Engine] = "2.4L I4",
                [VariantAttr.DisplacementL] = 2.4,
                [VariantAttr.Induction] = "NA"
            });

        private static readonly VehicleVariant s_gen4 = new(
            new(Gen4Key),
            "4th Gen (2012–2016)",
            2012, 2016,
            "Gen4",
            new Dictionary<string, object?>
            {
                [VariantAttr.Engine] = "2.4L I4",
                [VariantAttr.DisplacementL] = 2.4,
                [VariantAttr.Induction] = "NA",
                [VariantAttr.Transmission] = "CVT (2015+ facelift)"
            });

        private static readonly VehicleVariant s_gen5 = new(
            new(Gen5Key),
            "5th Gen (2017–2022)",
            2017, 2022,
            "Gen5",
            new Dictionary<string, object?>
            {
                [VariantAttr.Engine] = "1.5L Turbo",
                [VariantAttr.DisplacementL] = 1.5,
                [VariantAttr.Induction] = "Turbo",
                [VariantAttr.Hybrid] = true // if you want this to represent the lineup; or split Hybrid as a separate variant
            });

        private static readonly VehicleVariant s_gen6 = new(
            new(Gen6Key),
            "6th Gen (2023–present)",
            2023, null,
            "Gen6",
            new Dictionary<string, object?>
            {
                [VariantAttr.Hybrid] = true
            });

        public override string Make => "Honda";

        public override string Model => "CR-V";

        public override IReadOnlyList<VehicleVariant> Variants { get; } =
            [s_gen1, s_gen2, s_gen3, s_gen4, s_gen5, s_gen6];

        public override IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session) =>
            variantId.Value switch
            {
                //Gen1Key => new HondaCrvGen1CommandSet(session),
                //Gen2Key => new HondaCrvGen2CommandSet(session),
                //Gen3Key => new HondaCrvGen3CommandSet(session),
                //Gen4Key => new HondaCrvGen4CommandSet(session),
                Gen5Key => new HondaCrvGen5CommandSet(session),
                //Gen6Key => new HondaCrvGen6CommandSet(session),
                _ => throw new NotSupportedException($"Unknown/unsupported CR-V variant: {variantId.Value}")
            };
    }

    public class HondaCrvGen5CommandSet : VehicleCommandSet
    {
        public HondaCrvGen5CommandSet(IElmSession session)
        {
            Add<IHvac>(new HondaCrvGen5Hvac(session, HondaCrvGen5Contexts.Null));
        }
    }

    internal class HondaCrvGen5Hvac : IHvac
    {
        private EcuContext _context;
        private IElmSession _session;

        public HondaCrvGen5Hvac(IElmSession session, EcuContext context)
        {
            _session = session;
            _context = context;
        }

        public ValueTask<HvacStatus> GetStatusAsync(CancellationToken ct = default)
        {
            if (_context.Name == HondaCrvGen5Contexts.Null.Name)
            {
                return ValueTask.FromResult(new HvacStatus
                {
                    ClimateControlOn = false,
                    AcOn = false,
                    RearDefrostOn = false,
                    InteriorIntakeTempC = null,
                    OutsideAmbientTempC = null,
                    EvaporatorTempC = null,
                    FanSpeed = null,
                    FanVoltageV = null,
                    AcPowerWatts = null,
                    HeaterPowerWatts = null,
                    ClimateSetpoint = null,
                    AmbientTempAc = null
                });
            }

            throw new NotImplementedException();
        }
    }
}
