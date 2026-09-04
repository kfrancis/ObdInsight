using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0;

public sealed class LeafAze0CommandSet : VehicleCommandSet
{
    /// <summary>
    ///     The full command set over an ELM327 session: broadcast capabilities as views over the
    ///     shared monitor, plus the UDS capabilities (BMS, VIN, DTC) and Steering through an
    ///     arbitrated session.
    /// </summary>
    public LeafAze0CommandSet(IElmSession session)
    {
        // One shared monitoring pass for all broadcast data (streaming design P2/P3).
        // Broadcast capabilities read the monitor's cache/streams; UDS capabilities (BMS, VIN)
        // and Steering (needs session activation + keep-alive — not yet monitor-native) get a
        // decorated session that transparently suspends the monitor around their work.
        Monitor = new CanMonitor(session, LeafAze0Contexts.SharedBroadcastMonitor)
        {
            // Cheap BLE adapters can't drink accept-all ATMA — rotate hardware filters instead.
            FilterRotation = LeafAze0Contexts.SharedBroadcastRotation
        };
        var arbitrated = new MonitorSuspendingElmSession(session, Monitor);

        AddBroadcastCapabilities();
        Add<IBatteryManagementSystem>(new LeafAze0Bms(arbitrated, LeafAze0Contexts.LbcBms));
        Add<IDiagnosticTroubleCodes>(new ObdDtcReader(arbitrated, ObdDtcReader.FunctionalContext));
        Add<ISteering>(new LeafAze0Steering(arbitrated, LeafAze0Contexts.SteeringBroadcast));
        Add<IVehicleIdentification>(new LeafAze0VehicleIdentification(arbitrated, LeafAze0Contexts.Ident));
    }

    /// <summary>
    ///     The broadcast-only command set over a raw CAN frame source (a CANable on a Leaf bus).
    ///     Every capability that decodes broadcast frames is present; the ones that need to
    ///     transmit (UDS: BMS, VIN, DTC; Steering's session activation) are absent, so
    ///     <see cref="VehicleCommandSet.TryGet{T}" /> reports them unsupported rather than
    ///     failing at call time.
    /// </summary>
    /// <remarks>
    ///     Which frames actually arrive depends on which bus the adapter is wired to. On the
    ///     OBD port's pins 6/14 (CAR-CAN) that is the HVAC/BCM/VCM/ABS set; on pins 12/13
    ///     (EV-CAN) it is the battery/inverter/charger set that stock ELM327 adapters can
    ///     never see. The capabilities for the other bus simply time out on cold cache, as they
    ///     do today on a stock adapter. See <c>docs/CANABLE_SUPPORT.md</c>.
    /// </remarks>
    public LeafAze0CommandSet(ICanFrameSource source)
    {
        Monitor = new CanMonitor(source);
        AddBroadcastCapabilities();
    }

    /// <summary>
    ///     The shared broadcast monitor. Owned by this command set's creator: stop/dispose it when
    ///     the session ends. Also usable directly for typed streams
    ///     (<c>Monitor.Subscribe&lt;BatteryFrame_1DB_AZE0&gt;()</c> etc.).
    /// </summary>
    public CanMonitor Monitor { get; }

    private void AddBroadcastCapabilities()
    {
        Add<IAntilockBrakingSystem>(new LeafAze0Abs(Monitor));
        Add<IBodyControl>(new LeafAze0BodyControl(Monitor));
        Add<IBrake>(new LeafAze0Brake(Monitor));
        Add<IHvac>(new LeafAze0Hvac(Monitor));
        Add<IMotorController>(new LeafAze0MotorController(Monitor));
        Add<IOnboardCharger>(new LeafAze0Charger(Monitor));
        Add<IVcm>(new LeafAze0Vcm(Monitor));
    }
}
