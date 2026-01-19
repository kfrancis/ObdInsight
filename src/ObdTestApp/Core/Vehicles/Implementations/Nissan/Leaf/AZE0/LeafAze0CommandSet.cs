using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;

public sealed class LeafAze0CommandSet : VehicleCommandSet
{
    public LeafAze0CommandSet(IElmSession session)
    {
        Add<IAntilockBrakingSystem>(new LeafAze0Abs(session, LeafAze0Contexts.AbsBroadcast));
        Add<IBatteryManagementSystem>(new LeafAze0Bms(session, LeafAze0Contexts.LbcBms));
        Add<IBrake>(new LeafAze0Brake(session, LeafAze0Contexts.Brake));
        Add<IHvac>(new LeafAze0Hvac(session, LeafAze0Contexts.HvacBroadcast));
        Add<IMotorController>(new LeafAze0MotorController(session, LeafAze0Contexts.InvMcBroadcast));
        Add<IOnboardCharger>(new LeafAze0Charger(session, LeafAze0Contexts.ObcPdBroadcast));
        Add<IVcm>(new LeafAze0Vcm(session, LeafAze0Contexts.VcmBroadcast));
        Add<IVehicleIdentification>(new LeafAze0VehicleIdentification(session, LeafAze0Contexts.Ident));
    }
}




