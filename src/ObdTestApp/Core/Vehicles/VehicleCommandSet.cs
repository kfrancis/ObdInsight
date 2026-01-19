namespace ObdTestApp.Core.Vehicles;

public interface IVehicleCommandSet
{
    IReadOnlyCollection<Type> Capabilities { get; }

    bool TryGet<T>(out T capability) where T : class, IVehicleCapability;
}
public abstract class VehicleCommandSet : IVehicleCommandSet
{
    private readonly Dictionary<Type, IVehicleCapability> _caps = new();

    public IReadOnlyCollection<Type> Capabilities => _caps.Keys.ToArray();

    public bool TryGet<T>(out T capability) where T : class, IVehicleCapability
    {
        if (_caps.TryGetValue(typeof(T), out var cap) && cap is T t) { capability = t; return true; }
        capability = default!;
        return false;
    }

    protected void Add<T>(T cap) where T : class, IVehicleCapability => _caps[typeof(T)] = cap;
}
