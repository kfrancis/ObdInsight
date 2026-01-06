#if NET9_0_WINDOWS10_0_19041_0
using ObdInsight.Core.Transports.Ble;

namespace ObdInsight.DevTools;

/// <summary>
/// Windows-specific BLE transport factory.
/// This implementation can be swapped with MAUI-specific providers later.
/// </summary>
public class WindowsBleTransportFactory : IBleTransportFactory
{
    public IBleScanner CreateScanner()
    {
        return new WindowsBleScanner();
    }

    public IBleTransport CreateTransport(BleDeviceProfile profile)
    {
        return new WindowsBleTransport(profile);
    }
}
#endif