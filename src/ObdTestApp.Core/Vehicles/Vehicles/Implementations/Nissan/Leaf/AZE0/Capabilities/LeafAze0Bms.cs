using ObdTestApp.Core.Communication.Elm327;
using ObdTestApp.Core.Protocols;
using ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdTestApp.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    /// IBatteryManagementSystem adapter that wraps the generated UDS diagnostics.
    /// </summary>
    internal sealed class LeafAze0Bms : IBatteryManagementSystem
    {
        private readonly LeafBmsDiagnostics _diagnostics;

        public LeafAze0Bms(IElmSession session, EcuContext context)
        {
            _diagnostics = new LeafBmsDiagnostics(session, context);
        }

        public async ValueTask<CellVoltageData?> GetCellVoltagesAsync(CancellationToken ct = default)
        {
            // Use the generated query method
            var response = await _diagnostics.QueryGroup02Async(ct);

            if (response == null || response?.CellVoltagesMv?.Length == 0)
                return null;

            return new CellVoltageData
            {
                CellVoltagesMv = response!.CellVoltagesMv,
                MinVoltageMv = response.MinVoltageMv,
                MaxVoltageMv = response.MaxVoltageMv,
                AvgVoltageMv = response.AvgVoltageMv
            };
        }

        public async ValueTask<BatteryStatus> GetStatusAsync(CancellationToken ct = default)
        {
            // Use the generated query method
            var response = await _diagnostics.QueryGroup01Async(ct) ?? throw new InvalidOperationException("Failed to query BMS Group 01 data");

            return new BatteryStatus
            {
                SocPercent = response.SocPercent,
                VoltageVolts = response.VoltageVolts,
                CurrentAmps = response.CurrentAmps,
                CapacityAh = response.CapacityAh,
                HealthPercent = response.HealthPercent,
                TemperatureC = null
            };
        }

        /// <summary>
        /// Parses ISO-TP frames - exposed for LeafAze0Charger compatibility.
        /// </summary>
        internal static List<(int FrameType, int SeqOrLen, byte[] Data)> ParseIsoTpFrames(string[] lines) =>
            LeafBmsDiagnostics.ParseIsoTpFrames(lines);

        /// <summary>
        /// Reassembles ISO-TP payload - exposed for LeafAze0Charger compatibility.
        /// </summary>
        internal static byte[] ReassembleIsoTpPayload(List<(int FrameType, int SeqOrLen, byte[] Data)> frames) =>
            LeafBmsDiagnostics.ReassembleIsoTpPayload(frames);
    }
}
