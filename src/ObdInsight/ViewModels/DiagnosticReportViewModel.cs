using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ObdInsight.Core.Adapters;
using ObdInsight.Core.Adapters.Elm327;
using ObdInsight.Core.Diagnostics;
using ObdInsight.Core.Transports.Ble;
using ObdInsight.Services;
using System.Collections.ObjectModel;

namespace ObdInsight.ViewModels;

/// <summary>
/// ViewModel for the diagnostic report generation page.
/// Provides detailed progress reporting during vehicle data collection.
/// </summary>
public partial class DiagnosticReportViewModel : BaseViewModel, IProgress<DiagnosticProgress>
{
    private readonly IConnectedDeviceService _connectedDeviceService;
    private readonly INavigationService _navigationService;

    private CancellationTokenSource? _cancellationTokenSource;
    private IObdAdapter? _adapter;
    private DiagnosticDataCollector? _collector;

    #region Observable Properties

    [ObservableProperty]
    private DiagnosticPhase _currentPhase = DiagnosticPhase.NotStarted;

    [ObservableProperty]
    private string _phaseDescription = "Ready to start";

    [ObservableProperty]
    private string _currentOperation = string.Empty;

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private double _phaseProgress;

    [ObservableProperty]
    private int _itemsCompleted;

    [ObservableProperty]
    private int _itemsTotal;

    [ObservableProperty]
    private string? _currentItem;

    [ObservableProperty]
    private string? _lastResponse;

    [ObservableProperty]
    private bool? _lastOperationSuccess;

    [ObservableProperty]
    private bool _isCollecting;

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    private string? _reportFilePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCollectionCommand))]
    private bool _isDeviceConnected;

    [ObservableProperty]
    private string? _connectedDeviceName;

    [ObservableProperty]
    private string? _connectedDeviceAddress;

    // User vehicle info
    [ObservableProperty]
    private int _vehicleYear = DateTime.Now.Year;

    [ObservableProperty]
    private string _vehicleMake = string.Empty;

    [ObservableProperty]
    private string _vehicleModel = string.Empty;

    [ObservableProperty]
    private string? _vehicleTrim;

    [ObservableProperty]
    private string _engineType = "Gasoline";

    [ObservableProperty]
    private string _transmissionType = "Automatic";

    [ObservableProperty]
    private string? _additionalNotes;

    #endregion

    /// <summary>
    /// Log entries showing command/response traffic
    /// </summary>
    public ObservableCollection<DiagnosticLogEntry> LogEntries { get; } = [];

    /// <summary>
    /// Available engine types
    /// </summary>
    public IReadOnlyList<string> EngineTypes { get; } =
    [
        "Gasoline",
        "Diesel",
        "Hybrid",
        "Plug-in Hybrid (PHEV)",
        "Electric (BEV)",
        "Other/Unknown"
    ];

    /// <summary>
    /// Available transmission types
    /// </summary>
    public IReadOnlyList<string> TransmissionTypes { get; } =
    [
        "Automatic",
        "CVT",
        "Manual",
        "Dual-Clutch (DCT)",
        "Single-Speed (EV)",
        "Other/Unknown"
    ];

    public DiagnosticReportViewModel(
        IConnectedDeviceService connectedDeviceService,
        INavigationService navigationService)
    {
        ArgumentNullException.ThrowIfNull(connectedDeviceService);
        ArgumentNullException.ThrowIfNull(navigationService);

        _connectedDeviceService = connectedDeviceService;
        _navigationService = navigationService;
        Title = "Diagnostic Report";

        // Subscribe to connection changes
        _connectedDeviceService.ConnectionChanged += OnConnectionChanged;

        // Initialize from current connection state
        UpdateConnectionState();
    }

    private void OnConnectionChanged(object? sender, DeviceConnectionChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateConnectionState);
    }

    private void UpdateConnectionState()
    {
        IsDeviceConnected = _connectedDeviceService.IsConnected;
        ConnectedDeviceName = _connectedDeviceService.DeviceName;
        ConnectedDeviceAddress = _connectedDeviceService.DeviceAddress;
    }

    /// <summary>
    /// Starts the diagnostic data collection process
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartCollection))]
    private async Task StartCollectionAsync()
    {
        if (!_connectedDeviceService.IsConnected || _connectedDeviceService.Transport is null)
        {
            SetError("Please connect to a device first");
            return;
        }

        if (string.IsNullOrWhiteSpace(VehicleMake) || string.IsNullOrWhiteSpace(VehicleModel))
        {
            SetError("Please enter vehicle make and model");
            return;
        }

        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        IsCollecting = true;
        IsComplete = false;
        LogEntries.Clear();
        ClearError();

        BleAdapterInfo? bleInfo = null;
        ObdAdapterInfo? obdInfo = null;
        VehicleIdentification? vehicleId = null;
        SupportedPidsInfo? supportedPids = null;

        // Get the transport from the connected device service
        var transport = _connectedDeviceService.Transport;

        try
        {
            _collector = new DiagnosticDataCollector();

            // Subscribe to traffic for logging
            transport.DataSent += OnDataSent;
            transport.DataReceived += OnDataReceived;

            AddLogEntry("INFO", $"Using connected device: {ConnectedDeviceName} ({ConnectedDeviceAddress})");

            // Collect BLE info (basic - we'll enhance this later)
            bleInfo = new BleAdapterInfo
            {
                DeviceName = ConnectedDeviceName ?? "Unknown",
                MacAddress = ConnectedDeviceAddress ?? "Unknown",
                Services = [] // Platform-specific discovery would go here
            };
            _collector.AddNote($"Connected to {ConnectedDeviceName}");

            // Phase 1: Initialize adapter
            UpdatePhase(DiagnosticPhase.AdapterInit, "Initializing OBD adapter...");
            AddLogEntry("INFO", "Initializing ELM327 adapter...");

            _adapter = new Elm327Adapter();
            var initialized = await _adapter.InitializeAsync(transport, token);

            if (!initialized)
            {
                AddLogEntry("WARN", "Adapter initialization completed with warnings");
            }
            else
            {
                AddLogEntry("OK", "Adapter initialized successfully");
            }

            // Phase 2: Collect adapter info
            UpdatePhase(DiagnosticPhase.AdapterInfo, "Collecting adapter information...");
            obdInfo = await _collector.CollectObdAdapterInfoAsync(_adapter, this, token);

            // Phase 3: Collect vehicle ID
            UpdatePhase(DiagnosticPhase.VehicleId, "Reading vehicle identification...");
            vehicleId = await _collector.CollectVehicleIdAsync(_adapter, this, token);

            // Phase 4: Query supported PIDs
            UpdatePhase(DiagnosticPhase.SupportedPids, "Querying supported PIDs...");
            supportedPids = await _collector.CollectSupportedPidsAsync(_adapter, this, token);

            // Phase 5: Probe standard PIDs
            UpdatePhase(DiagnosticPhase.StandardPidProbe, "Probing standard PIDs...");
            await _collector.ProbeStandardPidsAsync(_adapter, supportedPids, this, token);

            // Phase 6: Probe extended PIDs
            UpdatePhase(DiagnosticPhase.ExtendedPidProbe, "Probing extended/EV PIDs...");
            await _collector.ProbeExtendedPidsAsync(_adapter, this, token);

            // Phase 7: Generate report
            UpdatePhase(DiagnosticPhase.GeneratingReport, "Generating report...");

            var userInfo = new UserVehicleInfo
            {
                Year = VehicleYear,
                Make = VehicleMake.Trim(),
                Model = VehicleModel.Trim(),
                Trim = string.IsNullOrWhiteSpace(VehicleTrim) ? null : VehicleTrim.Trim(),
                EngineType = EngineType,
                TransmissionType = TransmissionType,
                AdditionalNotes = string.IsNullOrWhiteSpace(AdditionalNotes) ? null : AdditionalNotes.Trim()
            };

            var report = _collector.BuildReport(userInfo, bleInfo, obdInfo, vehicleId, supportedPids);
            var markdown = MarkdownReportGenerator.Generate(report);

            // Save report
            var fileName = $"vehicle_report_{VehicleYear}_{VehicleMake}_{VehicleModel}_{DateTime.Now:yyyyMMdd_HHmmss}.md"
                .Replace(" ", "_")
                .Replace("/", "-");

            // Use app data directory for MAUI
            var directory = FileSystem.AppDataDirectory;
            ReportFilePath = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(ReportFilePath, markdown, token);

            AddLogEntry("OK", $"Report saved: {fileName}");

            // Complete
            UpdatePhase(DiagnosticPhase.Complete, "Collection complete!");
            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            UpdatePhase(DiagnosticPhase.Failed, "Collection cancelled");
            AddLogEntry("WARN", "Collection was cancelled by user");
        }
        catch (Exception ex)
        {
            UpdatePhase(DiagnosticPhase.Failed, $"Error: {ex.Message}");
            AddLogEntry("ERROR", ex.Message);
            SetError(ex.Message);

            _collector?.AddError("Collection", ex.Message, ex.ToString());
        }
        finally
        {
            // Unsubscribe from transport events (but don't dispose - it's managed by the service)
            transport.DataSent -= OnDataSent;
            transport.DataReceived -= OnDataReceived;

            _adapter = null;
            _collector = null;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            IsCollecting = false;
        }
    }

    private bool CanStartCollection() => !IsCollecting && IsDeviceConnected;

    /// <summary>
    /// Cancels the current collection
    /// </summary>
    [RelayCommand]
    private void CancelCollection()
    {
        if (!IsCollecting) return;
        _cancellationTokenSource?.Cancel();
        AddLogEntry("INFO", "Cancellation requested...");
    }

    /// <summary>
    /// Shares the generated report
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShareReport))]
    private async Task ShareReportAsync()
    {
        if (string.IsNullOrEmpty(ReportFilePath) || !File.Exists(ReportFilePath))
        {
            SetError("Report file not found");
            return;
        }

        await Share.RequestAsync(new ShareFileRequest
        {
            Title = "Share Diagnostic Report",
            File = new ShareFile(ReportFilePath)
        });
    }

    private bool CanShareReport() => IsComplete && !string.IsNullOrEmpty(ReportFilePath);

    /// <summary>
    /// Opens the report file
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanShareReport))]
    private async Task OpenReportAsync()
    {
        if (string.IsNullOrEmpty(ReportFilePath) || !File.Exists(ReportFilePath))
        {
            SetError("Report file not found");
            return;
        }

        await Launcher.OpenAsync(new OpenFileRequest
        {
            Title = "Open Diagnostic Report",
            File = new ReadOnlyFile(ReportFilePath)
        });
    }

    /// <summary>
    /// Navigate to device selection
    /// </summary>
    [RelayCommand]
    private async Task SelectDeviceAsync()
    {
        await _navigationService.NavigateToAsync("//devices");
    }

    #region IProgress<DiagnosticProgress> Implementation

    void IProgress<DiagnosticProgress>.Report(DiagnosticProgress value)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentPhase = value.Phase;
            PhaseDescription = GetPhaseDescription(value.Phase);
            CurrentOperation = value.Message;
            OverallProgress = value.OverallProgress;
            PhaseProgress = value.PhaseProgress;
            ItemsCompleted = value.ItemsCompleted;
            ItemsTotal = value.ItemsTotal;
            CurrentItem = value.CurrentItem;
            LastResponse = value.LastResponse;
            LastOperationSuccess = value.LastOperationSuccess;

            // Add log entry for significant events
            if (value.LastOperationSuccess.HasValue && !string.IsNullOrEmpty(value.CurrentItem))
            {
                var level = value.LastOperationSuccess.Value ? "OK" : "FAIL";
                var response = TruncateForLog(value.LastResponse);
                AddLogEntry(level, $"{value.CurrentItem}: {response}");
            }
        });
    }

    #endregion

    #region Private Helpers

    private void UpdatePhase(DiagnosticPhase phase, string description)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CurrentPhase = phase;
            PhaseDescription = description;
            CurrentOperation = description;

            AddLogEntry("PHASE", $"=== {GetPhaseDescription(phase)} ===");
        });
    }

    private static string GetPhaseDescription(DiagnosticPhase phase) => phase switch
    {
        DiagnosticPhase.NotStarted => "Ready to Start",
        DiagnosticPhase.BleDiscovery => "Discovering BLE Services",
        DiagnosticPhase.Connecting => "Connecting to Device",
        DiagnosticPhase.AdapterInit => "Initializing Adapter",
        DiagnosticPhase.AdapterInfo => "Collecting Adapter Info",
        DiagnosticPhase.VehicleId => "Reading Vehicle ID",
        DiagnosticPhase.SupportedPids => "Querying Supported PIDs",
        DiagnosticPhase.StandardPidProbe => "Probing Standard PIDs",
        DiagnosticPhase.ExtendedPidProbe => "Probing Extended PIDs",
        DiagnosticPhase.GeneratingReport => "Generating Report",
        DiagnosticPhase.Complete => "Complete",
        DiagnosticPhase.Failed => "Failed",
        _ => phase.ToString()
    };

    private void AddLogEntry(string level, string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LogEntries.Add(new DiagnosticLogEntry(DateTime.Now, level, message));

            // Keep log manageable - remove old entries if too many
            while (LogEntries.Count > 500)
            {
                LogEntries.RemoveAt(0);
            }
        });
    }

    private void OnDataSent(object? sender, string data)
    {
        var cleaned = data.Replace("\r", "\\r").Replace("\n", "\\n");
        AddLogEntry("TX", cleaned);
    }

    private void OnDataReceived(object? sender, string data)
    {
        var cleaned = data.Replace("\r", "\\r").Replace("\n", "\\n");
        AddLogEntry("RX", cleaned);
    }

    private static string TruncateForLog(string? value, int maxLength = 60)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";

        var cleaned = value.Replace("\r", "").Replace("\n", " ").Trim();
        if (cleaned.Length <= maxLength)
            return cleaned;

        return cleaned[..(maxLength - 3)] + "...";
    }

    #endregion
}

/// <summary>
/// Represents a log entry in the diagnostic collection log
/// </summary>
public record DiagnosticLogEntry(DateTime Timestamp, string Level, string Message)
{
    /// <summary>
    /// Formatted timestamp for display
    /// </summary>
    public string FormattedTime => Timestamp.ToString("HH:mm:ss.fff");

    /// <summary>
    /// Color for the level indicator
    /// </summary>
    public Color LevelColor => Level switch
    {
        "OK" => Colors.Green,
        "FAIL" or "ERROR" => Colors.Red,
        "WARN" => Colors.Orange,
        "TX" => Colors.Blue,
        "RX" => Colors.Teal,
        "PHASE" => Colors.Purple,
        _ => Colors.Gray
    };
}
