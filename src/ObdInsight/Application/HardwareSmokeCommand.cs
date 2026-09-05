using System.Globalization;
using ObdInsight.Core.Communication.Slcan;
using ObdInsight.Simulation;
using ObdInsight.Transports.Serial;
using ObdInsight.Transports.WindowsBle;

namespace ObdInsight.Application;

internal sealed record SmokeOptions(string Mode, string? Device, string? Port, int Bitrate,
    TimeSpan Duration, TimeSpan Timeout, string Output)
{
    public static SmokeOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arg in args)
        {
            var parts = arg.Split('=', 2);
            if (parts.Length != 2 || parts[1].Length == 0 ||
                parts[0] is not ("--smoke" or "--device" or "--serial" or "--bitrate" or "--duration" or "--timeout" or "--output") ||
                !values.TryAdd(parts[0], parts[1])) throw new ArgumentException("Invalid or duplicate smoke option.");
        }
        var mode = values.GetValueOrDefault("--smoke");
        if (mode is not ("ble" or "slcan" or "simulation")) throw new ArgumentException("Select --smoke=ble|slcan|simulation.");
        var device = values.GetValueOrDefault("--device");
        var port = values.GetValueOrDefault("--serial");
        if ((mode == "ble") != (device is not null) || (mode == "slcan") != (port is not null) ||
            mode != "slcan" && values.ContainsKey("--bitrate")) throw new ArgumentException("Transport options do not match smoke mode.");
        if (device is not null && (device.Split(':').Length != 6 || device.Split(':').Any(p => p.Length != 2 || !byte.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))))
            throw new ArgumentException("BLE requires a colon-separated MAC address.");
        int Number(string key, int fallback) => values.TryGetValue(key, out var value) &&
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed :
            !values.ContainsKey(key) ? fallback : throw new ArgumentException("Expected an integer smoke option.");
        var duration = Number("--duration", 60);
        var timeout = Number("--timeout", duration + 180);
        var bitrate = Number("--bitrate", 500);
        _ = SlcanProtocol.BitrateCommand(bitrate);
        if (duration is < 1 or > 1800 || timeout <= duration || timeout > 3600)
            throw new ArgumentException("Duration must be 1..1800 seconds; timeout must exceed duration and be at most 3600.");
        return new(mode, device, port, bitrate, TimeSpan.FromSeconds(duration), TimeSpan.FromSeconds(timeout),
            values.GetValueOrDefault("--output") ?? Path.Combine(".local", "smoke", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl"));
    }
}

internal static class HardwareSmokeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = SmokeOptions.Parse(args); // Validate before opening files or hardware.
            var path = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(file);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                Console.WriteLine($"Smoke evidence: {path}");
                Console.WriteLine("Stationary test only. SLCAN is listen-only; BLE sends diagnostic requests. Ctrl+C joins shutdown.");
                var runner = new HardwareSmokeRunner(writer);
                return await runner.RunAsync(options, options.Mode switch
                {
                    "ble" => () => new BleElmTransport(options.Device!),
                    "slcan" => () => new SerialElmTransport(options.Port!),
                    _ => () => new SimulatedLeafAze0Transport()
                }, cancellation.Token);
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        catch (Exception ex)
        {
            // Deliberately omit messages: platform exceptions may include identifiers.
            Console.Error.WriteLine($"Smoke failed ({ex.GetType().Name}). Check options, output access, and hardware setup.");
            return 1;
        }
    }
}
