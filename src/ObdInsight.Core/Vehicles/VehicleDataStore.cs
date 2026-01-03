using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace ObdInsight.Core.Vehicles;

/// <summary>
/// Observable store implementation for vehicle data that widgets bind to.
/// Polls IVehicleObdService and updates properties when new data arrives.
/// </summary>
/// <remarks>
/// This class serves as the bridge between vehicle-specific OBD queries
/// and standardized widget bindings. It:
/// - Manages a polling loop that queries the vehicle service
/// - Translates VehicleDataResult values into typed properties
/// - Tracks data staleness for UI indicators
/// - Implements INotifyPropertyChanged for MAUI data binding
/// </remarks>
public class VehicleDataStore : IVehicleDataStore, IDisposable
{
    private readonly ILogger<VehicleDataStore>? _logger;
    private readonly object _lock = new();
    private readonly Dictionary<VehicleDataPoint, object?> _dataCache = [];
    private readonly Dictionary<VehicleDataPoint, string?> _unitCache = [];

    private IVehicleObdService? _vehicleService;
    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private bool _disposed;

    // Default staleness threshold
    private static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultPollingInterval = TimeSpan.FromSeconds(2);

    public VehicleDataStore(ILogger<VehicleDataStore>? logger = null)
    {
        _logger = logger;
        PollingInterval = DefaultPollingInterval;
        SupportedCategories = new HashSet<VehicleDataCategory>();
    }

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion

    #region EV Battery Data

    private double? _batterySoc;
    public double? BatterySoc
    {
        get => _batterySoc;
        private set => SetProperty(ref _batterySoc, value);
    }

    private double? _batterySoh;
    public double? BatterySoh
    {
        get => _batterySoh;
        private set => SetProperty(ref _batterySoh, value);
    }

    private double? _batteryVoltage;
    public double? BatteryVoltage
    {
        get => _batteryVoltage;
        private set => SetProperty(ref _batteryVoltage, value);
    }

    private double? _batteryCurrent;
    public double? BatteryCurrent
    {
        get => _batteryCurrent;
        private set => SetProperty(ref _batteryCurrent, value);
    }

    private double? _batteryTemp;
    public double? BatteryTemp
    {
        get => _batteryTemp;
        private set => SetProperty(ref _batteryTemp, value);
    }

    private double? _batteryCapacity;
    public double? BatteryCapacity
    {
        get => _batteryCapacity;
        private set => SetProperty(ref _batteryCapacity, value);
    }

    public double? PowerKw => BatteryVoltage.HasValue && BatteryCurrent.HasValue
        ? BatteryVoltage.Value * BatteryCurrent.Value / 1000.0
        : null;

    #endregion

    #region Range and Charging

    private double? _rangeRemaining;
    public double? RangeRemaining
    {
        get => _rangeRemaining;
        private set => SetProperty(ref _rangeRemaining, value);
    }

    private string? _chargingStatus;
    public string? ChargingStatus
    {
        get => _chargingStatus;
        private set => SetProperty(ref _chargingStatus, value);
    }

    private bool _isCharging;
    public bool IsCharging
    {
        get => _isCharging;
        private set => SetProperty(ref _isCharging, value);
    }

    private double? _chargePower;
    public double? ChargePower
    {
        get => _chargePower;
        private set => SetProperty(ref _chargePower, value);
    }

    private int? _timeToFullCharge;
    public int? TimeToFullCharge
    {
        get => _timeToFullCharge;
        private set => SetProperty(ref _timeToFullCharge, value);
    }

    #endregion

    #region Standard OBD Data

    private double? _speed;
    public double? Speed
    {
        get => _speed;
        private set => SetProperty(ref _speed, value);
    }

    private double? _odometer;
    public double? Odometer
    {
        get => _odometer;
        private set => SetProperty(ref _odometer, value);
    }

    private double? _ambientTemp;
    public double? AmbientTemp
    {
        get => _ambientTemp;
        private set => SetProperty(ref _ambientTemp, value);
    }

    private double? _cabinTemp;
    public double? CabinTemp
    {
        get => _cabinTemp;
        private set => SetProperty(ref _cabinTemp, value);
    }

    private string? _vin;
    public string? Vin
    {
        get => _vin;
        private set => SetProperty(ref _vin, value);
    }

    #endregion

    #region ICE Vehicle Data

    private double? _engineRpm;
    public double? EngineRpm
    {
        get => _engineRpm;
        private set => SetProperty(ref _engineRpm, value);
    }

    private double? _coolantTemp;
    public double? CoolantTemp
    {
        get => _coolantTemp;
        private set => SetProperty(ref _coolantTemp, value);
    }

    private double? _fuelLevel;
    public double? FuelLevel
    {
        get => _fuelLevel;
        private set => SetProperty(ref _fuelLevel, value);
    }

    private double? _throttlePosition;
    public double? ThrottlePosition
    {
        get => _throttlePosition;
        private set => SetProperty(ref _throttlePosition, value);
    }

    private double? _engineLoad;
    public double? EngineLoad
    {
        get => _engineLoad;
        private set => SetProperty(ref _engineLoad, value);
    }

    #endregion

    #region Metadata

    private IVehicleProfile? _activeProfile;
    public IVehicleProfile? ActiveProfile
    {
        get => _activeProfile;
        private set
        {
            if (SetProperty(ref _activeProfile, value))
            {
                OnPropertyChanged(nameof(VehicleName));
                OnPropertyChanged(nameof(IsElectricVehicle));
                OnPropertyChanged(nameof(SupportedCategories));
            }
        }
    }

    public string? VehicleName => ActiveProfile?.Name;

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    public bool IsElectricVehicle => ActiveProfile?.IsElectric ?? false;

    private DateTimeOffset _lastUpdated;
    public DateTimeOffset LastUpdated
    {
        get => _lastUpdated;
        private set
        {
            if (SetProperty(ref _lastUpdated, value))
            {
                OnPropertyChanged(nameof(IsDataStale));
            }
        }
    }

    public bool IsDataStale => DateTimeOffset.UtcNow - LastUpdated > DefaultStaleThreshold;

    private TimeSpan _pollingInterval;
    public TimeSpan PollingInterval
    {
        get => _pollingInterval;
        set => SetProperty(ref _pollingInterval, value);
    }

    private IReadOnlySet<VehicleDataCategory> _supportedCategories;
    public IReadOnlySet<VehicleDataCategory> SupportedCategories
    {
        get => _supportedCategories;
        private set => SetProperty(ref _supportedCategories, value);
    }

    #endregion

    #region Query Support

    public bool IsDataPointAvailable(VehicleDataPoint dataPoint)
    {
        if (_vehicleService == null || ActiveProfile == null)
            return false;

        // Check if the profile has a command for this data point
        return ActiveProfile.GetCommand(dataPoint) != null ||
               ActiveProfile.CustomPids.Any(p => p.DataPoint == dataPoint);
    }

    public object? GetValue(VehicleDataPoint dataPoint)
    {
        lock (_lock)
        {
            return _dataCache.TryGetValue(dataPoint, out var value) ? value : null;
        }
    }

    public string? GetUnit(VehicleDataPoint dataPoint)
    {
        lock (_lock)
        {
            return _unitCache.TryGetValue(dataPoint, out var unit) ? unit : null;
        }
    }

    #endregion

    #region Control

    public void SetVehicleService(IVehicleObdService vehicleService)
    {
        ArgumentNullException.ThrowIfNull(vehicleService);

        _vehicleService = vehicleService;
        ActiveProfile = vehicleService.VehicleProfile;
        SupportedCategories = vehicleService.SupportedCategories;
        IsConnected = true;

        _logger?.LogInformation("Vehicle service set: {VehicleName}", VehicleName);
    }

    public async Task StartPollingAsync(CancellationToken cancellationToken = default)
    {
        if (_vehicleService == null)
        {
            _logger?.LogWarning("Cannot start polling: no vehicle service configured");
            return;
        }

        await StopPollingAsync();

        _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pollingTask = RunPollingLoopAsync(_pollingCts.Token);

        _logger?.LogInformation("Started polling with interval {Interval}", PollingInterval);
    }

    public async Task StopPollingAsync()
    {
        if (_pollingCts != null)
        {
            await _pollingCts.CancelAsync();

            if (_pollingTask != null)
            {
                try
                {
                    await _pollingTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                }
            }

            _pollingCts.Dispose();
            _pollingCts = null;
            _pollingTask = null;

            _logger?.LogInformation("Stopped polling");
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_vehicleService == null || ActiveProfile == null)
        {
            _logger?.LogWarning("RefreshAsync called but vehicle service not configured. VehicleService={HasService}, ActiveProfile={HasProfile}",
                _vehicleService != null, ActiveProfile != null);
            return;
        }

        _logger?.LogDebug("RefreshAsync starting data fetch...");
        await FetchDataAsync(cancellationToken);
        _logger?.LogDebug("RefreshAsync completed");
    }

    public void Clear()
    {
        // Stop polling first
        StopPollingAsync().GetAwaiter().GetResult();

        // Clear all data
        lock (_lock)
        {
            _dataCache.Clear();
            _unitCache.Clear();
        }

        // Reset all properties
        BatterySoc = null;
        BatterySoh = null;
        BatteryVoltage = null;
        BatteryCurrent = null;
        BatteryTemp = null;
        BatteryCapacity = null;
        RangeRemaining = null;
        ChargingStatus = null;
        IsCharging = false;
        ChargePower = null;
        TimeToFullCharge = null;
        Speed = null;
        Odometer = null;
        AmbientTemp = null;
        CabinTemp = null;
        Vin = null;
        EngineRpm = null;
        CoolantTemp = null;
        FuelLevel = null;
        ThrottlePosition = null;
        EngineLoad = null;

        // Clear metadata
        _vehicleService = null;
        ActiveProfile = null;
        SupportedCategories = new HashSet<VehicleDataCategory>();
        IsConnected = false;
        LastUpdated = default;

        _logger?.LogInformation("Vehicle data store cleared");
    }

    #endregion

    #region Polling Loop

    private async Task RunPollingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await FetchDataAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during polling cycle");
            }

            try
            {
                await Task.Delay(PollingInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task FetchDataAsync(CancellationToken cancellationToken)
    {
        if (_vehicleService == null || ActiveProfile == null)
            return;

        var dataPointsToFetch = GetDataPointsToFetch();

        foreach (var dataPoint in dataPointsToFetch)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var result = await _vehicleService.GetDataAsync(dataPoint, cancellationToken);

                if (result.Success && result.Value != null)
                {
                    UpdateProperty(dataPoint, result.Value, result.Unit);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to fetch {DataPoint}", dataPoint);
            }
        }

        // Also try batch methods for comprehensive data
        if (IsElectricVehicle)
        {
            try
            {
                var batteryInfo = await _vehicleService.GetBatteryInfoAsync(cancellationToken);
                if (batteryInfo != null)
                {
                    UpdateFromBatteryInfo(batteryInfo);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to fetch battery info");
            }
        }

        LastUpdated = DateTimeOffset.UtcNow;
    }

    private IEnumerable<VehicleDataPoint> GetDataPointsToFetch()
    {
        if (ActiveProfile == null)
            yield break;

        // EV-specific data points
        if (IsElectricVehicle)
        {
            yield return VehicleDataPoint.BatteryStateOfCharge;
            yield return VehicleDataPoint.BatteryStateOfHealth;
            yield return VehicleDataPoint.BatteryVoltage;
            yield return VehicleDataPoint.BatteryCurrent;
            yield return VehicleDataPoint.BatteryTemp;
            yield return VehicleDataPoint.RangeRemaining;
            yield return VehicleDataPoint.ChargingStatus;
        }

        // Standard data points
        yield return VehicleDataPoint.Speed;
        yield return VehicleDataPoint.AmbientTemp;

        // ICE-specific data points (for hybrids or ICE vehicles)
        if (!IsElectricVehicle || SupportedCategories.Contains(VehicleDataCategory.Engine))
        {
            yield return VehicleDataPoint.Rpm;
            yield return VehicleDataPoint.CoolantTemp;
            yield return VehicleDataPoint.FuelLevel;
            yield return VehicleDataPoint.ThrottlePosition;
            yield return VehicleDataPoint.EngineLoad;
        }
    }

    private void UpdateProperty(VehicleDataPoint dataPoint, object value, string? unit)
    {
        lock (_lock)
        {
            _dataCache[dataPoint] = value;
            if (unit != null)
                _unitCache[dataPoint] = unit;
        }

        // Map to typed properties
        switch (dataPoint)
        {
            case VehicleDataPoint.BatteryStateOfCharge:
                BatterySoc = ConvertToDouble(value);
                break;
            case VehicleDataPoint.BatteryStateOfHealth:
                BatterySoh = ConvertToDouble(value);
                break;
            case VehicleDataPoint.BatteryVoltage:
                BatteryVoltage = ConvertToDouble(value);
                OnPropertyChanged(nameof(PowerKw));
                break;
            case VehicleDataPoint.BatteryCurrent:
                BatteryCurrent = ConvertToDouble(value);
                OnPropertyChanged(nameof(PowerKw));
                break;
            case VehicleDataPoint.BatteryTemp:
                BatteryTemp = ConvertToDouble(value);
                break;
            case VehicleDataPoint.BatteryCapacity:
                BatteryCapacity = ConvertToDouble(value);
                break;
            case VehicleDataPoint.RangeRemaining:
                RangeRemaining = ConvertToDouble(value);
                break;
            case VehicleDataPoint.ChargingStatus:
                ChargingStatus = value?.ToString();
                IsCharging = ChargingStatus?.Contains("Charging", StringComparison.OrdinalIgnoreCase) ?? false;
                break;
            case VehicleDataPoint.Speed:
                Speed = ConvertToDouble(value);
                break;
            case VehicleDataPoint.Odometer:
                Odometer = ConvertToDouble(value);
                break;
            case VehicleDataPoint.AmbientTemp:
                AmbientTemp = ConvertToDouble(value);
                break;
            case VehicleDataPoint.CabinTemp:
                CabinTemp = ConvertToDouble(value);
                break;
            case VehicleDataPoint.Vin:
                Vin = value?.ToString();
                break;
            case VehicleDataPoint.Rpm:
                EngineRpm = ConvertToDouble(value);
                break;
            case VehicleDataPoint.CoolantTemp:
                CoolantTemp = ConvertToDouble(value);
                break;
            case VehicleDataPoint.FuelLevel:
                FuelLevel = ConvertToDouble(value);
                break;
            case VehicleDataPoint.ThrottlePosition:
                ThrottlePosition = ConvertToDouble(value);
                break;
            case VehicleDataPoint.EngineLoad:
                EngineLoad = ConvertToDouble(value);
                break;
        }
    }

    private void UpdateFromBatteryInfo(BatteryInfo info)
    {
        BatterySoc = info.StateOfCharge;
        BatterySoh = info.StateOfHealth;
        BatteryVoltage = info.Voltage;
        BatteryCurrent = info.Current;
        BatteryTemp = info.Temperature;
        BatteryCapacity = info.Capacity;
        RangeRemaining = info.RangeRemaining;
        ChargingStatus = info.ChargingStatus;
        IsCharging = info.IsCharging;
        ChargePower = Math.Abs(info.PowerKw);

        OnPropertyChanged(nameof(PowerKw));

        lock (_lock)
        {
            _dataCache[VehicleDataPoint.BatteryStateOfCharge] = info.StateOfCharge;
            _dataCache[VehicleDataPoint.BatteryStateOfHealth] = info.StateOfHealth;
            _dataCache[VehicleDataPoint.BatteryVoltage] = info.Voltage;
            _dataCache[VehicleDataPoint.BatteryCurrent] = info.Current;
            _dataCache[VehicleDataPoint.BatteryTemp] = info.Temperature;
            _dataCache[VehicleDataPoint.BatteryCapacity] = info.Capacity;
            _dataCache[VehicleDataPoint.RangeRemaining] = info.RangeRemaining;
            _dataCache[VehicleDataPoint.ChargingStatus] = info.ChargingStatus;
        }
    }

    private static double? ConvertToDouble(object? value)
    {
        return value switch
        {
            null => null,
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal dec => (double)dec,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => null
        };
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            StopPollingAsync().GetAwaiter().GetResult();
            _pollingCts?.Dispose();
        }

        _disposed = true;
    }

    #endregion
}
