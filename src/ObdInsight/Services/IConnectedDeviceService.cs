using ObdInsight.Core.Transports.Ble;

namespace ObdInsight.Services;

/// <summary>
/// Service that manages the shared OBD device connection state across pages.
/// Holds the connected transport and provides connection info to all consumers.
/// </summary>
public interface IConnectedDeviceService
{
    /// <summary>
    /// The currently connected BLE transport, or null if not connected.
    /// </summary>
    IBleTransport? Transport { get; }

    /// <summary>
    /// Whether a device is currently connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Name of the connected device.
    /// </summary>
    string? DeviceName { get; }

    /// <summary>
    /// Address of the connected device.
    /// </summary>
    string? DeviceAddress { get; }

    /// <summary>
    /// The BLE profile used for the connection.
    /// </summary>
    BleDeviceProfile? DeviceProfile { get; }

    /// <summary>
    /// Sets the connected device and transport.
    /// </summary>
    /// <param name="transport">The connected BLE transport.</param>
    /// <param name="deviceName">Name of the device.</param>
    /// <param name="deviceAddress">Address of the device.</param>
    /// <param name="profile">The BLE profile used.</param>
    void SetConnectedDevice(IBleTransport transport, string deviceName, string deviceAddress, BleDeviceProfile profile);

    /// <summary>
    /// Disconnects the current device and clears connection state.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Event raised when connection state changes.
    /// </summary>
    event EventHandler<DeviceConnectionChangedEventArgs>? ConnectionChanged;
}

/// <summary>
/// Event args for device connection state changes.
/// </summary>
public class DeviceConnectionChangedEventArgs : EventArgs
{
    /// <summary>
    /// Whether a device is now connected.
    /// </summary>
    public required bool IsConnected { get; init; }

    /// <summary>
    /// Name of the device (null if disconnected).
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// Address of the device (null if disconnected).
    /// </summary>
    public string? DeviceAddress { get; init; }
}
