using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ObdInsight.Core.Vehicles;
using ObdInsight.Telemetry;

namespace ObdInsight.Application;

// Only fixed phase/status values and exception categories escape this logger. Never invoke
// the formatter: exception messages, structured properties and future logs may contain VINs.
internal sealed class SmokeConnectionLogger : ILogger<VehicleConnection>
{
    private readonly ConcurrentQueue<SmokeEvidence> _pending = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public bool TryRead(out SmokeEvidence? evidence) => _pending.TryDequeue(out evidence);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (_pending.Count >= 128) return; // Diagnostic evidence must not grow without bound.
        var phase = eventId.Id switch
        {
            4100 => "transport-open", 4101 => "elm-initialize", 4102 => "vehicle-detect",
            4103 => "detection-outcome", 4104 => "attempt-failed", _ => null
        };
        if (phase is null) return;
        if (state is IEnumerable<KeyValuePair<string, object?>> fields)
        {
            foreach (var field in fields)
            {
                if (field.Key == "Phase" && field.Value is string value &&
                    value is "transport-open" or "elm-initialize" or "vehicle-detect" or "connected") phase += ":" + value;
                if (field.Key == "DetectionStatus" && field.Value is VehicleDetectionStatus status)
                    phase += ":" + status;
            }
        }
        var errors = new List<string>();
        for (var current = exception; current is not null && errors.Count < 8; current = current.InnerException)
            errors.Add($"{current.GetType().Name}:0x{current.HResult:X8}:{Reason(current)}");
        _pending.Enqueue(new("connection-diagnostic") { Detail = phase + (errors.Count == 0 ? "" : ":" + string.Join(" -> ", errors)) });
    }

    private static string Reason(Exception error)
    {
        // Classify known transport failure prefixes, never retain their arbitrary suffixes.
        var message = error.Message;
        if (message.StartsWith("Device not reachable.", StringComparison.Ordinal)) return "device-unreachable";
        if (message == "BLE device not found") return "device-not-found";
        if (message.StartsWith("Serial service not found.", StringComparison.Ordinal)) return "serial-service-unavailable";
        if (message == "Required characteristics not found") return "characteristics-unavailable";
        if (message == "Characteristic doesn't support notifications") return "notifications-unsupported";
        if (message.StartsWith("Failed to enable notifications", StringComparison.Ordinal)) return "notifications-enable-failed";
        if (message.StartsWith("Vehicle detection failed:", StringComparison.Ordinal)) return "vehicle-detection-failed";
        return "unclassified";
    }
}
