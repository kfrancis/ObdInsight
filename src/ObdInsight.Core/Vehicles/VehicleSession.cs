namespace ObdInsight.Core.Vehicles;

public interface IVehicleSession
{
    bool Supports<T>() where T : class, IVehicleCapability;
    bool TryGet<T>(out T cap) where T : class, IVehicleCapability;
}

public sealed class VehicleSession : IVehicleSession
{
    private readonly IVehicleCommandSet _commands;

    public VehicleSession(IVehicleCommandSet commands) => _commands = commands;

    public bool Supports<T>() where T : class, IVehicleCapability => _commands.TryGet<T>(out _);

    public bool TryGet<T>(out T cap) where T : class, IVehicleCapability => _commands.TryGet(out cap);
}
