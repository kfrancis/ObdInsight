using ObdInsight.Core.Communication.Elm327;
using ObdInsight.Core.Vehicles.Implementations.Nissan.Leaf;

namespace ObdInsight.DevTools;

/// <summary>
///     Compatibility shim for DevTools - wraps ElmSession to provide old Elm327Adapter API.
///     This is temporary to get DevTools building - should be refactored to use ElmSession directly.
/// </summary>
public class Elm327Adapter
{
    private ElmFramer? _framer;

    public ElmSession? Session { get; private set; }

    public event EventHandler<Elm327LogEventArgs>? Log;

    public async Task<bool> InitializeAsync(IElmTransport transport, CancellationToken ct = default)
    {
        _framer = new ElmFramer(transport);
        // DevTools is Leaf-focused tooling — wire the Leaf wakeup probe.
        Session = new ElmSession(_framer, new LeafBmsWakeupStrategy());

        try
        {
            await Session.InitializeAndLockAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void SetTransport(IElmTransport transport, bool markAsInitialized = false)
    {
        _framer = new ElmFramer(transport);
        Session = new ElmSession(_framer, new LeafBmsWakeupStrategy());
    }
}

/// <summary>
///     Compatibility log event args
/// </summary>
public class Elm327LogEventArgs : EventArgs
{
    public Elm327LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
///     Compatibility log level enum
/// </summary>
public enum Elm327LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}
