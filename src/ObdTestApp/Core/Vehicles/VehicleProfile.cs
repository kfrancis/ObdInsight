using ObdTestApp.Core.Communication.Elm327;

namespace ObdTestApp.Core.Vehicles;

public interface IVehicleProfile
{
    string Make { get; }
    string Model { get; }
    IReadOnlyList<VehicleVariant> Variants { get; }

    IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session);
}

public abstract class VehicleProfile : IVehicleProfile
{
    public abstract string Make { get; }
    public abstract string Model { get; }
    public abstract IReadOnlyList<VehicleVariant> Variants { get; }

    public abstract IVehicleCommandSet GetCommands(VehicleVariantId variantId, IElmSession session);
}
