using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObdInsight.Core.Transports.Ble;
using ObdInsight.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace ObdInsight.ViewModels;

/// <summary>
/// ViewModel for the BLE device scanning and selection page.
/// </summary>
public partial class DevicesViewModel : BaseViewModel, IDisposable
{
    /// <summary>
    /// Known OBD adapter name patterns
    /// </summary>
    private static readonly string[] KnownObdNamePatterns =
    [
        "VEEPEAK", "OBD", "ELM", "OBDII", "OBD2", "VLINK", "KONNWEI", "BAFX"
    ];

    /// <summary>
    /// Known OBD adapter service UUIDs for highlighting likely devices
    /// </summary>
    private static readonly HashSet<Guid> KnownObdServiceUuids = new(
    [
        BleDeviceProfile.VeepeakBle.ServiceUuid,
        BleDeviceProfile.VeepeakBleAlt.ServiceUuid,
        BleDeviceProfile.NordicUart.ServiceUuid,
        BleDeviceProfile.ObdLinkMx.ServiceUuid
    ]);

    private readonly IBleTransportFactory _bleTransportFactory;
    private readonly INavigationService _navigationService;
    private readonly IConnectedDeviceService _connectedDeviceService;
    private readonly object _scanLock = new();
    private bool _isStopping;
    private IBleScanner? _scanner;
    private CancellationTokenSource? _scanTimeoutCts;

    [ObservableProperty]
    private bool _isBluetoothAvailable;

    [ObservableProperty]
    private bool _isBluetoothOn;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectToDeviceCommand))]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectToDeviceCommand))]
    private DeviceListItem? _selectedDevice;

    /// <summary>
    /// Debug log messages for troubleshooting
    /// </summary>
    [ObservableProperty]
    private string _debugLog = string.Empty;

    public DevicesViewModel(
        INavigationService navigationService, 
        IBleTransportFactory bleTransportFactory,
        IConnectedDeviceService connectedDeviceService)
    {
        ArgumentNullException.ThrowIfNull(navigationService);
        ArgumentNullException.ThrowIfNull(bleTransportFactory);
        ArgumentNullException.ThrowIfNull(connectedDeviceService);

        _navigationService = navigationService;
        _bleTransportFactory = bleTransportFactory;
        _connectedDeviceService = connectedDeviceService;
        Title = "Select Device";

        Log("DevicesViewModel initialized");

        // Check Bluetooth status and subscribe to state changes
        if (_bleTransportFactory is PluginBleTransportFactory pluginFactory)
        {
            UpdateBluetoothStatus(pluginFactory);
            
            // Subscribe to Plugin.BLE state changes
            Plugin.BLE.CrossBluetoothLE.Current.StateChanged += OnBluetoothStateChanged;
            Log($"Bluetooth status: Available={IsBluetoothAvailable}, On={IsBluetoothOn}");
        }
        else
        {
            IsBluetoothAvailable = true;
            IsBluetoothOn = true;
            Log("Using non-Plugin factory, assuming Bluetooth available");
        }
    }

    private void OnBluetoothStateChanged(object? sender, Plugin.BLE.Abstractions.EventArgs.BluetoothStateChangedArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Log($"Bluetooth state changed: {e.OldState} -> {e.NewState}");
            if (_bleTransportFactory is PluginBleTransportFactory pluginFactory)
            {
                UpdateBluetoothStatus(pluginFactory);
            }
        });
    }

    private void UpdateBluetoothStatus(PluginBleTransportFactory pluginFactory)
    {
        var wasAvailable = IsBluetoothAvailable;
        var wasOn = IsBluetoothOn;

        IsBluetoothAvailable = pluginFactory.IsAvailable;
        IsBluetoothOn = pluginFactory.IsOn;

        Log($"UpdateBluetoothStatus: Available={IsBluetoothAvailable}, On={IsBluetoothOn}");

        // Notify command to re-evaluate CanExecute when state changes
        if (wasAvailable != IsBluetoothAvailable || wasOn != IsBluetoothOn)
        {
            StartScanCommand.NotifyCanExecuteChanged();
        }
    }

    public ObservableCollection<DeviceListItem> DiscoveredDevices { get; } = [];

    /// <inheritdoc/>
    protected override void OnBusyChanged()
    {
        base.OnBusyChanged();
        Log($"IsBusy changed to: {IsBusy}");
        StartScanCommand.NotifyCanExecuteChanged();
        ConnectToDeviceCommand.NotifyCanExecuteChanged();
    }

    private static bool IsLikelyObdAdapter(BleDeviceInfo device)
    {
        // Check if device advertises known OBD services
        if (device.AdvertisedServices.Any(s => KnownObdServiceUuids.Contains(s)))
            return true;

        // Check if device name matches known patterns
        var name = device.Name?.ToUpperInvariant() ?? string.Empty;
        return KnownObdNamePatterns.Any(pattern => name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private bool CanConnect()
    {
        var canConnect = SelectedDevice is not null && !IsBusy && !IsScanning;
        Log($"CanConnect check: SelectedDevice={SelectedDevice?.Device.Name ?? "null"}, IsBusy={IsBusy}, IsScanning={IsScanning}, Result={canConnect}");
        return canConnect;
    }

    private bool CanStartScan() => !IsScanning && !IsBusy && IsBluetoothAvailable && IsBluetoothOn;

    /// <summary>
    /// Cleans up scanner resources without triggering re-entry.
    /// </summary>
    private async Task CleanupScannerAsync()
    {
        var scanner = _scanner;
        _scanner = null;

        if (scanner is not null)
        {
            scanner.DeviceDiscovered -= OnDeviceDiscovered;
            scanner.ScanStateChanged -= OnScanStateChanged;

            try
            {
                await scanner.StopScanAsync();
            }
            catch
            {
                // Ignore errors during cleanup
            }

            scanner.Dispose();
        }

        IsScanning = false;
    }

    /// <summary>
    /// Connects to the selected BLE device.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectToDeviceAsync()
    {
        Log("ConnectToDeviceAsync called");
        
        if (SelectedDevice is null)
        {
            Log("ERROR: SelectedDevice is null, aborting connect");
            return;
        }

        Log($"Attempting to connect to: {SelectedDevice.Device.Name} ({SelectedDevice.Device.Address})");

        await ExecuteBusyAsync(async () =>
        {
            try
            {
                Log("Starting connection process...");
                
                // Stop scanning if still active
                if (IsScanning)
                {
                    Log("Stopping active scan before connecting...");
                    await StopScanAsync();
                }

                // Determine which BLE profile to use based on advertised services
                var profile = DetermineDeviceProfile(SelectedDevice.Device);
                Log($"Using BLE profile: {profile.Name} (Service: {profile.ServiceUuid})");

                // Create transport and attempt connection
                Log("Creating BLE transport...");
                var transport = _bleTransportFactory.CreateTransport(profile);
                
                Log($"Connecting to device address: {SelectedDevice.Device.Address}");
                ScanStatus = $"Connecting to {SelectedDevice.Device.Name}...";

                // Attempt connection with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                
                bool connected;
                try
                {
                    connected = await transport.ConnectAsync(SelectedDevice.Device.Address, cts.Token);
                    Log($"Connection result: {connected}");
                }
                catch (Exception ex)
                {
                    Log($"Connection exception: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }

                if (!connected)
                {
                    Log("Connection returned false - device may not support expected services");
                    SetError($"Failed to connect to {SelectedDevice.Device.Name}. Make sure the device is powered on and in range.");
                    transport.Dispose();
                    return;
                }

                Log("Connection successful! Storing in connected device service...");
                ScanStatus = $"Connected to {SelectedDevice.Device.Name}";

                // Store the connection in the shared service
                _connectedDeviceService.SetConnectedDevice(
                    transport,
                    SelectedDevice.Device.Name,
                    SelectedDevice.Device.Address,
                    profile);

                Log($"Navigating back to main page");
                
                try
                {
                    await _navigationService.NavigateToAsync("..");
                    Log("Navigation completed successfully");
                }
                catch (Exception navEx)
                {
                    Log($"Navigation exception: {navEx.GetType().Name}: {navEx.Message}");
                    throw;
                }
            }
            catch (OperationCanceledException)
            {
                Log("Connection timed out after 30 seconds");
                SetError("Connection timed out. Please try again.");
            }
            catch (Exception ex)
            {
                Log($"Connection failed with exception: {ex.GetType().Name}: {ex.Message}");
                Log($"Stack trace: {ex.StackTrace}");
                SetError($"Connection failed: {ex.Message}");
            }
        });

        Log("ConnectToDeviceAsync completed");
    }

    /// <summary>
    /// Determines the best BLE profile to use for a device based on its advertised services.
    /// </summary>
    private static BleDeviceProfile DetermineDeviceProfile(BleDeviceInfo device)
    {
        // Check advertised services to pick the right profile
        foreach (var serviceUuid in device.AdvertisedServices)
        {
            var matchingProfile = BleDeviceProfile.FindByServiceUuid(serviceUuid);
            if (matchingProfile is not null)
            {
                return matchingProfile;
            }
        }

        // Default to Veepeak profile for OBD-named devices
        var upperName = device.Name?.ToUpperInvariant() ?? string.Empty;
        
        if (upperName.Contains("VEEPEAK"))
            return BleDeviceProfile.VeepeakBle;
        
        if (upperName.Contains("OBDLINK") || upperName.Contains("OBD LINK"))
            return BleDeviceProfile.ObdLinkMx;

        // Default fallback
        return BleDeviceProfile.VeepeakBle;
    }

    private void OnDeviceDiscovered(object? sender, BleDeviceDiscoveredEventArgs e)
    {
        // Ensure we're on the UI thread
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Avoid duplicates
            if (DiscoveredDevices.Any(d => d.Device.Address == e.Device.Address))
                return;

            var isLikelyObd = IsLikelyObdAdapter(e.Device);
            var item = new DeviceListItem(e.Device, isLikelyObd);

            Log($"Device discovered: {e.Device.Name} ({e.Device.Address}) RSSI={e.Device.Rssi} OBD={isLikelyObd}");

            // Insert in sorted order: likely OBD first, then by signal strength (strongest first)
            var insertIndex = 0;
            foreach (var existing in DiscoveredDevices)
            {
                // Likely OBD adapters always come before non-OBD devices
                if (isLikelyObd && !existing.IsLikelyObdAdapter)
                    break;

                // Non-OBD devices go after all OBD devices
                if (!isLikelyObd && existing.IsLikelyObdAdapter)
                {
                    insertIndex++;
                    continue;
                }

                // Within the same category, sort by signal strength (higher RSSI = stronger)
                if (e.Device.Rssi > existing.Device.Rssi)
                    break;

                insertIndex++;
            }

            DiscoveredDevices.Insert(insertIndex, item);
            ScanStatus = $"Found {DiscoveredDevices.Count} device(s)";
        });
    }

    private void OnScanStateChanged(object? sender, BleScanStateChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Log($"Scan state changed: IsScanning={e.IsScanning}");
            IsScanning = e.IsScanning;
            if (!e.IsScanning)
            {
                ScanStatus = DiscoveredDevices.Count > 0
                    ? $"Scan complete - {DiscoveredDevices.Count} device(s) found"
                    : "Scan complete - No devices found";
            }
        });
    }

    /// <summary>
    /// Starts scanning for BLE devices (no filter - shows all devices).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        Log("StartScanAsync called");
        
        if (!IsBluetoothAvailable)
        {
            Log("ERROR: Bluetooth not available");
            SetError("Bluetooth is not available on this device.");
            return;
        }

        if (!IsBluetoothOn)
        {
            Log("ERROR: Bluetooth not enabled");
            SetError("Please enable Bluetooth to scan for devices.");
            return;
        }

        ClearError();
        DiscoveredDevices.Clear();
        ScanStatus = "Scanning for devices...";

        Log("Creating scanner...");
        _scanner = _bleTransportFactory.CreateScanner();
        _scanner.DeviceDiscovered += OnDeviceDiscovered;
        _scanner.ScanStateChanged += OnScanStateChanged;

        IsScanning = true;
        _isStopping = false;
        _scanTimeoutCts = new CancellationTokenSource();
        var timeoutToken = _scanTimeoutCts.Token;

        try
        {
            // No filter - show all BLE devices, let user choose
            // Only apply a minimum RSSI to filter out very weak signals
            var filter = new BleScanFilter(MinRssi: -90);

            Log("Starting BLE scan with RSSI filter: -90");
            await _scanner.StartScanAsync(filter);

            // Auto-stop after 15 seconds, but don't block the UI
            Log("Scan started, will auto-stop in 15 seconds");
            await Task.Delay(TimeSpan.FromSeconds(15), timeoutToken);

            // Timeout reached, stop scanning
            Log("Scan timeout reached, stopping...");
            await StopScanAsync();
        }
        catch (OperationCanceledException)
        {
            Log("Scan was cancelled (manual stop before timeout)");
            // Scan was stopped manually before timeout - that's expected
        }
        catch (ObjectDisposedException)
        {
            Log("CTS was disposed during stop");
            // CTS was disposed during stop - that's expected
        }
        catch (Exception ex)
        {
            Log($"Scan failed with exception: {ex.GetType().Name}: {ex.Message}");
            SetError($"Scan failed: {ex.Message}");
            await CleanupScannerAsync();
        }
    }

    /// <summary>
    /// Stops the current BLE scan.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private async Task StopScanAsync()
    {
        Log("StopScanAsync called");
        
        // Prevent re-entry
        lock (_scanLock)
        {
            if (_isStopping)
            {
                Log("Already stopping, ignoring duplicate call");
                return;
            }
            _isStopping = true;
        }

        try
        {
            // Cancel the timeout task first
            var cts = _scanTimeoutCts;
            _scanTimeoutCts = null;

            if (cts is not null)
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed
                }

                cts.Dispose();
            }

            await CleanupScannerAsync();

            ScanStatus = DiscoveredDevices.Count > 0
                ? $"Scan stopped - {DiscoveredDevices.Count} device(s) found"
                : "Scan stopped";
                
            Log($"Scan stopped. {DiscoveredDevices.Count} device(s) found");
        }
        finally
        {
            _isStopping = false;
        }
    }

    /// <summary>
    /// Logs a debug message (also writes to Debug output)
    /// </summary>
    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] {message}";
        
        Debug.WriteLine($"[DevicesVM] {logLine}");
        
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Keep last 50 lines to avoid memory issues
            // Access the backing field directly to avoid potential source generator timing issues
            var lines = _debugLog.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 50)
            {
                _debugLog = string.Join("\n", lines.Skip(lines.Length - 50)) + "\n" + logLine;
            }
            else
            {
                _debugLog = string.IsNullOrEmpty(_debugLog) ? logLine : _debugLog + "\n" + logLine;
            }
            OnPropertyChanged(nameof(DebugLog));
        });
    }

    /// <summary>
    /// Clean up resources and unsubscribe from events
    /// </summary>
    public void Dispose()
    {
        Log("Dispose called");
        
        // Unsubscribe from Bluetooth state changes
        if (_bleTransportFactory is PluginBleTransportFactory)
        {
            Plugin.BLE.CrossBluetoothLE.Current.StateChanged -= OnBluetoothStateChanged;
        }

        // Clean up scanner if still active
        CleanupScannerAsync().GetAwaiter().GetResult();
    }
}

/// <summary>
/// Wrapper for BLE device info with additional UI properties
/// </summary>
public partial class DeviceListItem : ObservableObject
{
    public DeviceListItem(BleDeviceInfo device, bool isLikelyObdAdapter)
    {
        Device = device;
        IsLikelyObdAdapter = isLikelyObdAdapter;
    }

    /// <summary>
    /// Display badge for likely OBD adapters
    /// </summary>
    public string Badge => IsLikelyObdAdapter ? "OBD" : string.Empty;

    /// <summary>
    /// Border color - highlight likely OBD adapters
    /// </summary>
    public Color BorderColor => IsLikelyObdAdapter ? Colors.Green : Colors.Transparent;

    public BleDeviceInfo Device { get; }

    public bool IsLikelyObdAdapter { get; }
}