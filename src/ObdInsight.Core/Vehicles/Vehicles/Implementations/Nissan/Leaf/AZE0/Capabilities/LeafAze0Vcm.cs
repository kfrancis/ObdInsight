using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    /// Composite VCM implementation that delegates to bus-specific helpers.
    /// Provides unified access to VCM data from both EV-CAN and CAR-CAN buses.
    /// </summary>
    internal sealed class LeafAze0Vcm : IVcm
    {
        readonly LeafAze0VcmEvCan _evCan;
        readonly LeafAze0VcmCarCan _carCan;

        public LeafAze0Vcm(IElmSession session, EcuContext evCanContext, EcuContext carCanContext)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (evCanContext == null) throw new ArgumentNullException(nameof(evCanContext));
            if (carCanContext == null) throw new ArgumentNullException(nameof(carCanContext));

            _evCan = new LeafAze0VcmEvCan(session, evCanContext);
            _carCan = new LeafAze0VcmCarCan(session, carCanContext);
        }

        /// <summary>
        /// Reads current gear position from EV-CAN broadcast frame 0x11A.
        /// </summary>
        public ValueTask<GearPosition> GetGearPositionAsync(CancellationToken ct = default)
            => _evCan.GetGearPositionAsync(ct);

        /// <summary>
        /// Gets comprehensive VCM status from CAR-CAN broadcast frame 0x510.
        /// </summary>
        public ValueTask<VcmStatus> GetStatusAsync(CancellationToken ct = default)
            => _carCan.GetStatusAsync(ct);
    }
}
