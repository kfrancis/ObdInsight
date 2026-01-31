namespace ObdInsight.Core.Communication.Bluetooth;

/// <summary>
/// Interface for platform-specific BLE scanning implementations.
/// </summary>
public interface IBleScanner : IDisposable
{
    /// <summary>
    /// Event raised when a device is discovered during scanning.
    /// </summary>
    event EventHandler<BleDeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <summary>
    /// Event raised when the scanning state changes.
    /// </summary>
    event EventHandler<BleScanStateChangedEventArgs>? ScanStateChanged;

    /// <summary>
    /// Gets whether the scanner is currently scanning.
    /// </summary>
    bool IsScanning { get; }

    /// <summary>
    /// Starts scanning for BLE devices with optional filtering.
    /// </summary>
    /// <param name="filter">Optional filter criteria for devices.</param>
    /// <param name="ct">Cancellation token.</param>
    Task StartScanAsync(BleScanFilter? filter = null, CancellationToken ct = default);

    /// <summary>
    /// Stops the current scan.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task StopScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all devices discovered in the current or most recent scan.
    /// </summary>
    IReadOnlyList<BleDeviceInfo> GetDiscoveredDevices();

    /// <summary>
    /// Clears the list of discovered devices.
    /// </summary>
    void ClearDiscoveredDevices();
}
