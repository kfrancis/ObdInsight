namespace ObdInsight.IntegrationTests;

/// <summary>
///     Conditionally skips USB-CAN adapter tests unless <c>CANABLE_PORT</c> is set (e.g.
///     <c>COM5</c>). Unlike <see cref="RequiresLeafHardwareAttribute" /> these need only the
///     adapter, not a car: they exercise the serial plumbing and the SLCAN handshake against real
///     firmware, which is exactly the part the replay transport cannot vouch for.
/// </summary>
public sealed class RequiresCanableAttribute : SkipAttribute
{
    public const string PortVariable = "CANABLE_PORT";

    public RequiresCanableAttribute()
        : base($"Requires a CANable-class USB-CAN adapter. Set {PortVariable} to its COM port to enable.")
    {
    }

    /// <summary>The configured port, or null when the adapter tests are not opted in.</summary>
    public static string? Port
    {
        get
        {
            var port = Environment.GetEnvironmentVariable(PortVariable);
            return string.IsNullOrWhiteSpace(port) ? null : port.Trim();
        }
    }

    public override Task<bool> ShouldSkip(TestRegisteredContext context) => Task.FromResult(Port is null);
}
