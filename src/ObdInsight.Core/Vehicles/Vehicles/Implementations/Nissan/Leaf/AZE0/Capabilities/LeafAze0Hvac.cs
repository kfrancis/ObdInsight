using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;
using ObdInsight.Core.Vehicles;
using ObdInsight.Core.Vehicles.Implementations.Nissan.AZE0;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf.AZE0.Capabilities
{

    internal sealed class LeafAze0Hvac : IHvac
    {
        readonly IElmSession _session;
        readonly EcuContext _context;

        public LeafAze0Hvac(IElmSession session, EcuContext context)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            if (_context.CommunicationMode != EcuCommunicationMode.PassiveMonitoring)
                throw new ArgumentException("HVAC status requires PassiveMonitoring context (0x54A-0x54F).", nameof(context));
        }

        /// <summary>
        /// Reads current HVAC status by monitoring Leaf AZE0 HVAC broadcast frames:
        /// - 0x54A: setpoint/ambient-ish fields (setpoint raw in byte4)
        /// - 0x54B: fan speed nibble (bits 36..39), vent modes (bytes2/3)
        /// - 0x54C: outside ambient temp, evap temp, A/C status bits, rear defrost, fan voltage
        /// - 0x54F: interior intake temp, A/C power, heater power, auto amp status bits
        ///
        /// These frames transmit about every 100ms.
        /// </summary>
        public async ValueTask<HvacStatus> GetStatusAsync(CancellationToken ct = default)
        {
            var timeout = TimeSpan.FromMilliseconds(400);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await _session.EnterMonitoringModeAsync(_context, ct);

            HvacFrame_54C_AZE0? status = null;
            HvacFrame_54B_AZE0? fan = null;
            HvacFrame_54F_AZE0? power = null;
            HvacFrame_54A_AZE0? ambient = null;

            try
            {
                await foreach (var frame in _session.MonitorFramesAsync(timeoutCts.Token))
                {
                    if (frame.Data.Length != 8)
                        continue;

                    // Use generated router for cleaner, type-safe frame parsing
                    if (CanFrameRouter.TryParseHvacFrame_54A_AZE0(frame.CanId, frame.Data.Span, out var parsed54A))
                    {
                        ambient = parsed54A;
                    }
                    else if (CanFrameRouter.TryParseHvacFrame_54B_AZE0(frame.CanId, frame.Data.Span, out var parsed54B))
                    {
                        fan = parsed54B;
                    }
                    else if (CanFrameRouter.TryParseHvacFrame_54C_AZE0(frame.CanId, frame.Data.Span, out var parsed54C))
                    {
                        status = parsed54C;
                    }
                    else if (CanFrameRouter.TryParseHvacFrame_54F_AZE0(frame.CanId, frame.Data.Span, out var parsed54F))
                    {
                        power = parsed54F;
                    }

                    // Exit early if we have all required frames
                    if (status != null && fan != null && power != null)
                        break;
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Timeout - return partial data
            }
            finally
            {
                await _session.ExitMonitoringModeAsync(ct);
            }

            // Map to generic interface
            return new HvacStatus
            {
                ClimateControlOn = status?.ClimateControlOn ?? false,
                AcOn = status?.AcOn ?? false,
                RearDefrostOn = status?.RearDefrostOn ?? false,
                OutsideAmbientTempC = status?.OutsideAmbientTemp,
                EvaporatorTempC = status?.EvaporatorTemp,
                FanVoltageV = status?.FanVoltage,
                FanSpeed = fan?.FanSpeed,
                InteriorIntakeTempC = power?.InteriorIntakeTemp,
                AcPowerWatts = power?.AcPowerWatts,
                HeaterPowerWatts = power?.HeaterPowerWatts,
                ClimateSetpoint = ambient?.ClimateControlSetpoint,
                AmbientTempAc = ambient?.AmbientTempAc
            };
        }
    }
}
