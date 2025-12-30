namespace ObdInsight.Core.Transports.Ble;

/// <summary>
/// BLE-specific transport interface with device addressing.
/// </summary>
public interface IBleTransport : IObdTransport
{
    /// <summary>
    /// The MAC address or identifier of the connected BLE device
    /// </summary>
    string DeviceAddress { get; }

    /// <summary>
    /// The GATT service UUID used for communication
    /// </summary>
    Guid ServiceUuid { get; }

    /// <summary>
    /// Current BLE connection state
    /// </summary>
    BleConnectionState ConnectionState { get; }

    /// <summary>
    /// Connect to a specific BLE device by address
    /// </summary>
    /// <param name="deviceAddress">The device MAC address or identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if connection succeeded</returns>
    Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event raised when connection state changes
    /// </summary>
    event EventHandler<BleConnectionState>? ConnectionStateChanged;
}

/// <summary>
/// BLE connection states
/// </summary>
public enum BleConnectionState
{
    /// <summary>Not connected to any device</summary>
    Disconnected,

    /// <summary>Connection in progress</summary>
    Connecting,

    /// <summary>Connected and ready</summary>
    Connected,

    /// <summary>Disconnection in progress</summary>
    Disconnecting
}

/// <summary>
/// BLE device scanner interface - platform implementations provide this.
/// </summary>
public interface IBleScanner : IDisposable
{
    /// <summary>
    /// Whether a scan is currently in progress
    /// </summary>
    bool IsScanning { get; }

    /// <summary>
    /// Start scanning for BLE devices
    /// </summary>
    /// <param name="filter">Optional filter criteria</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task StartScanAsync(BleScanFilter? filter = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop scanning
    /// </summary>
    Task StopScanAsync();

    /// <summary>
    /// Event raised when a device is discovered
    /// </summary>
    event EventHandler<BleDeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <summary>
    /// Event raised when scan state changes
    /// </summary>
    event EventHandler<BleScanStateChangedEventArgs>? ScanStateChanged;
}

/// <summary>
/// Factory for creating platform-specific BLE transports.
/// </summary>
public interface IBleTransportFactory
{
    /// <summary>
    /// Create a transport for the given device profile
    /// </summary>
    /// <param name="profile">The BLE device profile to use</param>
    /// <returns>A new BLE transport instance</returns>
    IBleTransport CreateTransport(BleDeviceProfile profile);

    /// <summary>
    /// Create a scanner for discovering BLE devices
    /// </summary>
    /// <returns>A new BLE scanner instance</returns>
    IBleScanner CreateScanner();
}