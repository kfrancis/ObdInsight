using ObdInsight.Core.Communication.Bluetooth;

namespace ObdInsight.DevTools;

/// <summary>
/// Windows-specific BLE transport factory.
/// This implementation can be swapped with MAUI-specific providers later.
/// </summary>
public class WindowsBleTransportFactory
{
    public IBleScanner CreateScanner()
    {
        return new WindowsBleScanner();
    }

    public WindowsBleTransport CreateTransport(BleDeviceProfile profile)
    {
        return new WindowsBleTransport(profile);
    }
}