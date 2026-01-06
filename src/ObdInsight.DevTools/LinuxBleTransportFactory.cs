#if !WINDOWS
using ObdInsight.Core.Transports.Ble;

namespace ObdInsight.DevTools;

/// <summary>
/// Linux-specific BLE transport factory using Linux.Bluetooth library.
/// </summary>
public class LinuxBleTransportFactory : IBleTransportFactory
{
    public IBleScanner CreateScanner()
    {
        return new LinuxBleScanner();
    }

    public IBleTransport CreateTransport(BleDeviceProfile profile)
    {
        return new LinuxBleTransport(profile);
    }
}
#endif
