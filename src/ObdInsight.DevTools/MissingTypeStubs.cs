// Temporary stubs for types that don't exist in the new architecture
// These need to be refactored or removed - marked as obsolete

namespace ObdInsight.DevTools;

/// <summary>
/// STUB: Needs to be updated for new architecture.
/// </summary>
[Obsolete("Needs to be updated for new architecture")]
public class UserVehicleInfo
{
    public string? Make { get; set; }
    public string? Model { get; set; }
    public string? Year { get; set; }
    public string? Vin { get; set; }
}

/// <summary>
/// STUB: Needs to be updated for new architecture.
/// </summary>
[Obsolete("Needs to be updated for new architecture")]
public class VehicleObdService
{
}

/// <summary>
/// STUB: Recording/replay functionality needs to be updated for new architecture.
/// </summary>
[Obsolete("Needs to be updated for new architecture")]
public class RecordingTransportDecorator : IDisposable
{
    public RecordingTransportDecorator(object transport, object adapter)
    {
    }

    public void Dispose()
    {
    }

    public object GetSession()
    {
        return new TransportSession();
    }
}

/// <summary>
/// STUB: Recording/replay functionality needs to be updated for new architecture.
/// </summary>
[Obsolete("Needs to be updated for new architecture")]
public class TransportSession
{
}

/// <summary>
/// STUB: Tracing functionality needs to be updated for new architecture.
/// </summary>
[Obsolete("Needs to be updated for new architecture")]
public class TransportTracer
{
}
