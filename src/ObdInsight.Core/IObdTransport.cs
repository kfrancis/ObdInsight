namespace ObdInsight.Core
{
    /// <summary>
    /// Core transport interface for OBD communication (BLE, WiFi, Serial, etc.)
    /// </summary>
    public interface IObdTransport : IDisposable
    {
        string Name { get; }
        bool IsConnected { get; }

        Task<bool> ConnectAsync(CancellationToken cancellationToken = default);

        Task DisconnectAsync();

        Task WriteAsync(string data, CancellationToken cancellationToken = default);

        Task<string> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

        Task<string> ReadUntilAsync(string terminator, TimeSpan timeout, CancellationToken cancellationToken = default);

        // For diagnostics/logging
        event EventHandler<string>? DataReceived;

        event EventHandler<string>? DataSent;
    }

    /// <summary>
    /// BLE-specific transport with device addressing
    /// </summary>
    public interface IBleTransport : IObdTransport
    {
        string DeviceAddress { get; }
        Guid ServiceUuid { get; }
        BleConnectionState ConnectionState { get; }

        Task<bool> ConnectAsync(string deviceAddress, CancellationToken cancellationToken = default);

        event EventHandler<BleConnectionState>? ConnectionStateChanged;
    }

    /// <summary>
    /// BLE device scanner interface - platform implementations will provide this
    /// </summary>
    public interface IBleScanner : IDisposable
    {
        bool IsScanning { get; }

        Task StartScanAsync(BleScanFilter? filter = null, CancellationToken cancellationToken = default);

        Task StopScanAsync();

        event EventHandler<BleDeviceDiscoveredEventArgs>? DeviceDiscovered;

        event EventHandler<BleScanStateChangedEventArgs>? ScanStateChanged;
    }

    /// <summary>
    /// Factory for creating platform-specific BLE transports
    /// </summary>
    public interface IBleTransportFactory
    {
        IBleTransport CreateTransport(BleDeviceProfile profile);

        IBleScanner CreateScanner();
    }

    /// <summary>
    /// OBD adapter interface for protocol handling (ELM327, etc.)
    /// </summary>
    public interface IObdAdapter
    {
        string Name { get; }
        string[] SupportedDeviceNames { get; } // For auto-detection
        bool IsInitialized { get; }

        Task<bool> InitializeAsync(IObdTransport transport, CancellationToken cancellationToken = default);

        Task<ObdResponse> SendCommandAsync(ObdCommand command, CancellationToken cancellationToken = default);

        Task ResetAsync();
    }

    #region Supporting Types

    public enum BleConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Disconnecting
    }

    public record ObdCommand(string Command, TimeSpan? CustomTimeout = null)
    {
        public static ObdCommand Create(string command) => new(command);
        public static ObdCommand Create(string command, TimeSpan timeout) => new(command, timeout);
    }

    public record ObdResponse(bool Success, string? Value, string? RawResponse, string? Error)
    {
        public static ObdResponse Ok(string value, string rawResponse) => new(true, value, rawResponse, null);
        public static ObdResponse Fail(string error, string? rawResponse = null) => new(false, null, rawResponse, error);
    }

    public record BleDeviceInfo(
        string Name,
        string Address,
        int Rssi,
        IReadOnlyList<Guid> AdvertisedServices,
        IReadOnlyDictionary<string, byte[]>? ManufacturerData = null
    );

    public record BleDeviceProfile(
        string Name,
        Guid ServiceUuid,
        Guid WriteCharacteristicUuid,
        Guid NotifyCharacteristicUuid,
        bool WriteWithResponse = false,
        int MaxWriteSize = 20
    )
    {
        /// <summary>
        /// Veepeak OBDCheck BLE+ adapter
        /// Service: 0000FFF0, Write: 0000FFF2, Notify: 0000FFF1
        /// </summary>
        public static BleDeviceProfile VeepeakBle => new(
            Name: "Veepeak BLE+",
            ServiceUuid: Guid.Parse("0000FFF0-0000-1000-8000-00805F9B34FB"),
            WriteCharacteristicUuid: Guid.Parse("0000FFF2-0000-1000-8000-00805F9B34FB"),
            NotifyCharacteristicUuid: Guid.Parse("0000FFF1-0000-1000-8000-00805F9B34FB"),
            WriteWithResponse: false,
            MaxWriteSize: 20
        );

        /// <summary>
        /// Alternative Veepeak profile using the secondary service (0000FFE0)
        /// Some Veepeak variants may use this instead
        /// </summary>
        public static BleDeviceProfile VeepeakBleAlt => new(
            Name: "Veepeak BLE+ (Alt)",
            ServiceUuid: Guid.Parse("0000FFE0-0000-1000-8000-00805F9B34FB"),
            WriteCharacteristicUuid: Guid.Parse("0000FFE1-0000-1000-8000-00805F9B34FB"),
            NotifyCharacteristicUuid: Guid.Parse("0000FFE1-0000-1000-8000-00805F9B34FB"),
            WriteWithResponse: false,
            MaxWriteSize: 20
        );

        /// <summary>
        /// Nordic UART Service profile (alternative for some devices)
        /// </summary>
        public static BleDeviceProfile NordicUart => new(
            Name: "Nordic UART",
            ServiceUuid: Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E"),
            WriteCharacteristicUuid: Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E"),
            NotifyCharacteristicUuid: Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E"),
            WriteWithResponse: false,
            MaxWriteSize: 20
        );
    }

    public record BleScanFilter(
        IReadOnlyList<Guid>? ServiceUuids = null,
        IReadOnlyList<string>? DeviceNames = null,
        IReadOnlyList<string>? DeviceAddresses = null,
        int? MinRssi = null
    );

    public class BleDeviceDiscoveredEventArgs : EventArgs
    {
        public BleDeviceInfo Device { get; }

        public BleDeviceDiscoveredEventArgs(BleDeviceInfo device) => Device = device;
    }

    public class BleScanStateChangedEventArgs : EventArgs
    {
        public bool IsScanning { get; }

        public BleScanStateChangedEventArgs(bool isScanning) => IsScanning = isScanning;
    }

    #endregion Supporting Types
}