using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObdInsight.Core.Transports.Ble;
using ObdInsight.Services;
using System.Collections.ObjectModel;

namespace ObdInsight.ViewModels;

/// <summary>
/// ViewModel for the BLE device scanning and selection page.
/// </summary>
public partial class DevicesViewModel : BaseViewModel
{
    private readonly INavigationService _navigationService;
    private readonly IBleTransportFactory _bleTransportFactory;
    private IBleScanner? _scanner;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectToDeviceCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectToDeviceCommand))]
    private BleDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private bool _isBluetoothAvailable;

    [ObservableProperty]
    private bool _isBluetoothOn;

    public ObservableCollection<BleDeviceInfo> DiscoveredDevices { get; } = [];

    public DevicesViewModel(INavigationService navigationService, IBleTransportFactory bleTransportFactory)
    {
        ArgumentNullException.ThrowIfNull(navigationService);
        ArgumentNullException.ThrowIfNull(bleTransportFactory);

        _navigationService = navigationService;
        _bleTransportFactory = bleTransportFactory;
        Title = "Select Device";

        // Check Bluetooth status
        if (_bleTransportFactory is PluginBleTransportFactory pluginFactory)
        {
            IsBluetoothAvailable = pluginFactory.IsAvailable;
            IsBluetoothOn = pluginFactory.IsOn;
        }
        else
        {
            IsBluetoothAvailable = true;
            IsBluetoothOn = true;
        }
    }

    /// <summary>
    /// Starts scanning for BLE OBD devices.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task StartScanAsync()
    {
        if (!IsBluetoothAvailable)
        {
            SetError("Bluetooth is not available on this device.");
            return;
        }

        if (!IsBluetoothOn)
        {
            SetError("Please enable Bluetooth to scan for devices.");
            return;
        }

        await ExecuteBusyAsync(async () =>
        {
            DiscoveredDevices.Clear();
            _scanner = _bleTransportFactory.CreateScanner();
            _scanner.DeviceDiscovered += OnDeviceDiscovered;
            _scanner.ScanStateChanged += OnScanStateChanged;

            IsScanning = true;

            // Filter for common OBD adapter service UUIDs
            var filter = new BleScanFilter(
                ServiceUuids:
                [
                    BleDeviceProfile.VeepeakBle.ServiceUuid,
                    BleDeviceProfile.VeepeakBleAlt.ServiceUuid,
                    BleDeviceProfile.NordicUart.ServiceUuid
                ],
                MinRssi: -80
            );

            await _scanner.StartScanAsync(filter);

            // Auto-stop after 15 seconds
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (IsScanning)
            {
                await StopScanAsync();
            }
        });
    }

    private bool CanStartScan() => !IsScanning && !IsBusy && IsBluetoothAvailable && IsBluetoothOn;

    /// <summary>
    /// Stops the current BLE scan.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsScanning))]
    private async Task StopScanAsync()
    {
        if (_scanner is not null)
        {
            await _scanner.StopScanAsync();
            _scanner.DeviceDiscovered -= OnDeviceDiscovered;
            _scanner.ScanStateChanged -= OnScanStateChanged;
            _scanner.Dispose();
            _scanner = null;
        }
        IsScanning = false;
    }

    /// <summary>
    /// Connects to the selected BLE device.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectToDeviceAsync()
    {
        if (SelectedDevice is null) return;

        await ExecuteBusyAsync(async () =>
        {
            // Stop scanning if still active
            if (IsScanning)
            {
                await StopScanAsync();
            }

            // TODO: Implement actual connection logic with IBleTransport
            await Task.Delay(500); // Placeholder for connection

            // Navigate back to main page with connection info
            await _navigationService.NavigateToAsync("..", new Dictionary<string, object>
            {
                ["DeviceName"] = SelectedDevice.Name,
                ["DeviceAddress"] = SelectedDevice.Address
            });
        });
    }

    private bool CanConnect() => SelectedDevice is not null && !IsBusy && !IsScanning;

    /// <inheritdoc/>
    protected override void OnBusyChanged()
    {
        base.OnBusyChanged();
        StartScanCommand.NotifyCanExecuteChanged();
        ConnectToDeviceCommand.NotifyCanExecuteChanged();
    }

    private void OnDeviceDiscovered(object? sender, BleDeviceDiscoveredEventArgs e)
    {
        // Ensure we're on the UI thread
        MainThread.BeginInvokeOnMainThread(() =>
        {
            // Avoid duplicates
            if (!DiscoveredDevices.Any(d => d.Address == e.Device.Address))
            {
                DiscoveredDevices.Add(e.Device);
            }
        });
    }

    private void OnScanStateChanged(object? sender, BleScanStateChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsScanning = e.IsScanning;
        });
    }
}