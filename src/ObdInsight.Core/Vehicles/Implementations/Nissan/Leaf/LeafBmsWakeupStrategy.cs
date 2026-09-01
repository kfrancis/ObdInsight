using System.Diagnostics;
using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Protocols;

namespace ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf;

/// <summary>
///     Nissan Leaf wakeup strategy: the Leaf does not respond to standard OBD-II Mode 01
///     broadcast queries — it uses EV-CAN. Probe the BMS (LBC) directly with a Mode 21 query
///     on 0x79B/0x7BB; a response both wakes the ECUs and confirms CAN 11-bit 500k (protocol 6).
///     Extracted from ElmSession so the generic session layer stays vehicle-agnostic.
/// </summary>
public sealed class LeafBmsWakeupStrategy : IEcuWakeupStrategy
{
    public string Name => "Nissan Leaf BMS";

    public async ValueTask<char?> TryWakeupAsync(ElmFramer framer, TimeSpan commandTimeout, CancellationToken ct)
    {
        try
        {
            // Configure for Nissan Leaf BMS communication: TX 0x79B, RX 0x7BB.
            Log("Trying Nissan Leaf BMS (79B/7BB)...");

            await framer.SendAndReadFrameAsync("AT SH 79B", commandTimeout, ct);
            await framer.SendAndReadFrameAsync("AT CRA 7BB", commandTimeout, ct);
            await framer.SendAndReadFrameAsync("AT FC SH 79B", commandTimeout, ct);
            await framer.SendAndReadFrameAsync("AT FC SD 30 00 00", commandTimeout, ct);
            await framer.SendAndReadFrameAsync("AT FC SM 1", commandTimeout, ct);

            // Send Mode 21 Group 01 query (BMS SOC, Capacity, etc.)
            Log("Sending Mode 21 Group 01 query (2101)...");
            var response = await framer.SendAndReadFrameAsync("2101", TimeSpan.FromSeconds(5), ct);
            var lines = ElmParsing.NormalizeLines(response);

            // Check if we got a valid response (should contain 7BB prefix)
            if (lines.Any(l => l.Contains("7BB") && l.Length > 10))
            {
                Log($"Nissan Leaf BMS responded! Response: {string.Join(", ", lines.Take(2))}");
                return '6'; // CAN 11-bit 500k confirmed
            }

            Log("No Nissan Leaf BMS response");
            return null;
        }
        catch (Exception ex)
        {
            Log($"Nissan Leaf BMS probe failed: {ex.Message}");
            return null;
        }
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[LeafBmsWakeupStrategy] {message}");
    }
}
