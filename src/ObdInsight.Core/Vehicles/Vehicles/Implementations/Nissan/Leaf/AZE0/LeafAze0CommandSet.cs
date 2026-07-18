using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;

public sealed class LeafAze0CommandSet : VehicleCommandSet
{
    public LeafAze0CommandSet(IElmSession session)
    {
        // One shared monitoring pass for all broadcast data (streaming design P2/P3).
        // Migrated capabilities (HVAC, MotorController) read the monitor's cache/streams;
        // everything else gets a decorated session that transparently suspends the monitor
        // around queries and legacy enter/exit monitoring, so both models coexist.
        Monitor = new CanMonitor(session, LeafAze0Contexts.SharedBroadcastMonitor);
        var arbitrated = new MonitorSuspendingElmSession(session, Monitor);

        Add<IAntilockBrakingSystem>(new LeafAze0Abs(arbitrated, LeafAze0Contexts.AbsBroadcast));
        Add<IBatteryManagementSystem>(new LeafAze0Bms(arbitrated, LeafAze0Contexts.LbcBms));
        Add<IBodyControl>(new LeafAze0BodyControl(arbitrated, LeafAze0Contexts.BcmBroadcast));
        Add<IBrake>(new LeafAze0Brake(arbitrated, LeafAze0Contexts.BrakeBroadcast));
        Add<IHvac>(new LeafAze0Hvac(Monitor));
        Add<IMotorController>(new LeafAze0MotorController(Monitor));
        Add<IOnboardCharger>(new LeafAze0Charger(arbitrated, LeafAze0Contexts.ObcPdBroadcast));
        Add<ISteering>(new LeafAze0Steering(arbitrated, LeafAze0Contexts.SteeringBroadcast));
        Add<IVcm>(new LeafAze0Vcm(arbitrated, LeafAze0Contexts.VcmEvCanBroadcast, LeafAze0Contexts.VcmCarCanBroadcast));
        Add<IVehicleIdentification>(new LeafAze0VehicleIdentification(arbitrated, LeafAze0Contexts.Ident));
    }

    /// <summary>
    /// The shared broadcast monitor. Owned by this command set's creator: stop/dispose it when
    /// the session ends. Also usable directly for typed streams
    /// (<c>Monitor.Subscribe&lt;BatteryFrame_1DB_AZE0&gt;()</c> etc.).
    /// </summary>
    public CanMonitor Monitor { get; }
}
