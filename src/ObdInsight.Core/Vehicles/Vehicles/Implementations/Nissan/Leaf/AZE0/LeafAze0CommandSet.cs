using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;

public sealed class LeafAze0CommandSet : VehicleCommandSet
{
    public LeafAze0CommandSet(IElmSession session)
    {
        // One shared monitoring pass for all broadcast data (streaming design P2/P3).
        // Broadcast capabilities read the monitor's cache/streams; UDS capabilities (BMS, VIN)
        // and Steering (needs session activation + keep-alive — not yet monitor-native) get a
        // decorated session that transparently suspends the monitor around their work.
        Monitor = new CanMonitor(session, LeafAze0Contexts.SharedBroadcastMonitor)
        {
            // Cheap BLE adapters can't drink accept-all ATMA — rotate hardware filters instead.
            FilterRotation = LeafAze0Contexts.SharedBroadcastRotation,
        };
        var arbitrated = new MonitorSuspendingElmSession(session, Monitor);

        Add<IAntilockBrakingSystem>(new LeafAze0Abs(Monitor));
        Add<IBatteryManagementSystem>(new LeafAze0Bms(arbitrated, LeafAze0Contexts.LbcBms));
        Add<IBodyControl>(new LeafAze0BodyControl(Monitor));
        Add<IBrake>(new LeafAze0Brake(Monitor));
        Add<IHvac>(new LeafAze0Hvac(Monitor));
        Add<IMotorController>(new LeafAze0MotorController(Monitor));
        Add<IOnboardCharger>(new LeafAze0Charger(Monitor));
        Add<ISteering>(new LeafAze0Steering(arbitrated, LeafAze0Contexts.SteeringBroadcast));
        Add<IVcm>(new LeafAze0Vcm(Monitor));
        Add<IVehicleIdentification>(new LeafAze0VehicleIdentification(arbitrated, LeafAze0Contexts.Ident));
    }

    /// <summary>
    /// The shared broadcast monitor. Owned by this command set's creator: stop/dispose it when
    /// the session ends. Also usable directly for typed streams
    /// (<c>Monitor.Subscribe&lt;BatteryFrame_1DB_AZE0&gt;()</c> etc.).
    /// </summary>
    public CanMonitor Monitor { get; }
}
