using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Frames;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{
    /// <summary>
    ///     IBatteryManagementSystem adapter that wraps the generated UDS diagnostics.
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
            // Missing I/O retains the legacy null result; received malformed data and
            // timeouts carry explicit evidence. Cancellation/programming errors propagate.
            Observed<LeafBmsDiagnostics.Group02Response?> response;
            try
            {
                response = await _diagnostics.QueryGroup02Async(ct);
            }
            catch (TimeoutException)
            {
                return new CellVoltageData([]) { Observation = new(Source: ObservationSource.DiagnosticQuery,
                    Quality: ObservationQuality.TimedOut, Query: "2102") };
            }
            catch (IOException)
            {
                return null;
            }

            if (response.Value is null || response.Value.CellVoltagesMv.Length == 0)
                return new CellVoltageData([]) { Observation = response.Observation };

            // Best-effort: shunt states (group 06) enrich the result but must not fail it.
            Observed<LeafBmsDiagnostics.Group06Response?>? shunts = null;
            try
            {
                shunts = await _diagnostics.QueryGroup06Async(ct);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                // Group unsupported / transient adapter noise — voltages alone are still valid.
            }

            return new CellVoltageData(response.Value.CellVoltagesMv, shunts?.Value?.GetBalancingCells()) { Observation = response.Observation };
        }

        public async ValueTask<BatteryStatus> GetStatusAsync(CancellationToken ct = default)
        {
            // Missing I/O yields an all-null status. Timeouts and invalid replies retain
            // evidence; cancellation and programming errors are not data absence.
            Observed<LeafBmsDiagnostics.Group01Response?>? response;
            try
            {
                response = await _diagnostics.QueryGroup01Async(ct);
            }
            catch (TimeoutException)
            {
                response = new(null, new(Source: ObservationSource.DiagnosticQuery,
                    Quality: ObservationQuality.TimedOut, Query: "2101"));
            }
            catch (IOException)
            {
                response = null;
            }

            if (response?.Value is null)
            {
                var missing = response?.Observation ?? new ObservationMetadata(Source: ObservationSource.DiagnosticQuery,
                    Quality: ObservationQuality.Missing, Query: "2101");
                return new BatteryStatus { SocObservation = missing, VoltageObservation = missing, CurrentObservation = missing };
            }

            // Best-effort: pack temperatures (group 04) enrich the status but must not fail it.
            Observed<LeafBmsDiagnostics.Group04Response?>? temps = null;
            try
            {
                temps = await _diagnostics.QueryGroup04Async(ct);
            }
            catch (TimeoutException)
            {
                temps = new(null, new(Source: ObservationSource.DiagnosticQuery,
                    Quality: ObservationQuality.TimedOut, Query: "2104"));
            }
            catch (IOException)
            {
                // Group unsupported / transient adapter noise — core status is still valid.
            }

            return new BatteryStatus
            {
                SocObservation = response.Observation,
                VoltageObservation = response.Observation,
                CurrentObservation = response.Observation,
                TemperatureObservation = temps?.Observation ?? default,
                SocPercent = response.Value.SocPercent,
                VoltageVolts = response.Value.VoltageVolts,
                CurrentAmps = response.Value.CurrentAmps,
                CapacityAh = response.Value.CapacityAh,
                // Group 01 supplies Nissan Hx, not SOH. Leave SOH unavailable until
                // a validated, source-specific SOH provider is connected.
                TemperatureC = temps?.Value?.AverageTempC,
                MinTemperatureC = temps?.Value?.MinTempC,
                MaxTemperatureC = temps?.Value?.MaxTempC
            };
        }

    }
}
