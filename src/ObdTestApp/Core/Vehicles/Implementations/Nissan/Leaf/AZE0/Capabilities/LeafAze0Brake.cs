using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Protocols;
using ObdTestApp.Core.Vehicles;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    internal class LeafAze0Brake : IBrake
    {
        private IElmSession _session;
        private EcuContext _brake;

        public LeafAze0Brake(IElmSession session, EcuContext brake)
        {
            _session = session;
            _brake = brake;
        }

        public ValueTask<BrakeStatus> GetStatusAsync(CancellationToken ct = default)
        {
            throw new NotImplementedException();
        }
    }
}
