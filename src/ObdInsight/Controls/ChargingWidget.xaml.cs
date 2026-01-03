using System.ComponentModel;
using System.Runtime.CompilerServices;
using ObdInsight.Core.Vehicles;

namespace ObdInsight.Controls;

/// <summary>
/// Widget displaying charging status, power, and estimated time to full.
/// Shows visual indicator for charging state.
/// </summary>
public partial class ChargingWidget : ContentView, INotifyPropertyChanged
{
    /// <summary>
    /// Bindable property for the VehicleDataStore that provides data to this widget.
    /// </summary>
    public static readonly BindableProperty DataStoreProperty = BindableProperty.Create(
        nameof(DataStore),
        typeof(IVehicleDataStore),
        typeof(ChargingWidget),
        null,
        propertyChanged: OnDataStoreChanged);

    /// <summary>
    /// Bindable property to control whether the widget auto-hides when data is unavailable.
    /// </summary>
    public static readonly BindableProperty AutoHideWhenUnavailableProperty = BindableProperty.Create(
        nameof(AutoHideWhenUnavailable),
        typeof(bool),
        typeof(ChargingWidget),
        true);

    /// <summary>
    /// Bindable property for the widget title/label.
    /// </summary>
    public static readonly BindableProperty WidgetTitleProperty = BindableProperty.Create(
        nameof(WidgetTitle),
        typeof(string),
        typeof(ChargingWidget),
        "Charging");

    private string _statusText = "Not Charging";
    private string _chargePowerText = "--";
    private string _timeToFullText = "--";
    private bool _isActivelyCharging;
    private bool _showTimeToFull;
    private Color _statusColor = Color.FromArgb("#7C828E"); // Muted gray

    public ChargingWidget()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets or sets the VehicleDataStore that provides data to this widget.
    /// </summary>
    public IVehicleDataStore? DataStore
    {
        get => (IVehicleDataStore?)GetValue(DataStoreProperty);
        set => SetValue(DataStoreProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the widget should automatically hide when its required data is unavailable.
    /// </summary>
    public bool AutoHideWhenUnavailable
    {
        get => (bool)GetValue(AutoHideWhenUnavailableProperty);
        set => SetValue(AutoHideWhenUnavailableProperty, value);
    }

    /// <summary>
    /// Gets or sets the widget title/label.
    /// </summary>
    public string WidgetTitle
    {
        get => (string)GetValue(WidgetTitleProperty);
        set => SetValue(WidgetTitleProperty, value);
    }

    /// <summary>
    /// Gets the charging status text.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>
    /// Gets the charge power display text.
    /// </summary>
    public string ChargePowerText
    {
        get => _chargePowerText;
        private set => SetProperty(ref _chargePowerText, value);
    }

    /// <summary>
    /// Gets the time to full display text.
    /// </summary>
    public string TimeToFullText
    {
        get => _timeToFullText;
        private set => SetProperty(ref _timeToFullText, value);
    }

    /// <summary>
    /// Gets whether the vehicle is actively charging.
    /// </summary>
    public bool IsActivelyCharging
    {
        get => _isActivelyCharging;
        private set => SetProperty(ref _isActivelyCharging, value);
    }

    /// <summary>
    /// Gets whether to show the time to full row.
    /// </summary>
    public bool ShowTimeToFull
    {
        get => _showTimeToFull;
        private set => SetProperty(ref _showTimeToFull, value);
    }

    /// <summary>
    /// Gets the status indicator color.
    /// </summary>
    public Color StatusColor
    {
        get => _statusColor;
        private set => SetProperty(ref _statusColor, value);
    }

    private static void OnDataStoreChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is ChargingWidget widget)
        {
            // Unsubscribe from old store
            if (oldValue is IVehicleDataStore oldStore)
            {
                oldStore.PropertyChanged -= widget.OnDataStorePropertyChanged;
            }

            // Subscribe to new store
            if (newValue is IVehicleDataStore newStore)
            {
                newStore.PropertyChanged += widget.OnDataStorePropertyChanged;
            }

            widget.UpdateDisplay();
        }
    }

    private void OnDataStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IVehicleDataStore.ChargingStatus) or
                             nameof(IVehicleDataStore.IsCharging) or
                             nameof(IVehicleDataStore.ChargePower) or
                             nameof(IVehicleDataStore.TimeToFullCharge) or
                             nameof(IVehicleDataStore.IsConnected))
        {
            MainThread.BeginInvokeOnMainThread(UpdateDisplay);
        }
    }

    private void UpdateDisplay()
    {
        if (DataStore == null)
        {
            ResetToDefault();
            return;
        }

        // Update charging status
        var status = DataStore.ChargingStatus;
        StatusText = string.IsNullOrEmpty(status) ? "Not Charging" : status;
        IsActivelyCharging = DataStore.IsCharging;

        // Update status indicator color
        StatusColor = IsActivelyCharging
            ? Color.FromArgb("#3DDC84")  // Green when charging
            : Color.FromArgb("#7C828E"); // Muted gray when not charging

        // Update charge power
        var chargePower = DataStore.ChargePower;
        if (chargePower.HasValue && IsActivelyCharging)
        {
            ChargePowerText = $"{chargePower.Value:F1} kW";
        }
        else
        {
            ChargePowerText = "--";
        }

        // Update time to full
        var timeToFull = DataStore.TimeToFullCharge;
        if (timeToFull.HasValue && IsActivelyCharging)
        {
            ShowTimeToFull = true;
            TimeToFullText = FormatTimeToFull(timeToFull.Value);
        }
        else
        {
            ShowTimeToFull = false;
            TimeToFullText = "--";
        }

        UpdateVisibility();
    }

    private void ResetToDefault()
    {
        StatusText = "Not Charging";
        ChargePowerText = "--";
        TimeToFullText = "--";
        IsActivelyCharging = false;
        ShowTimeToFull = false;
        StatusColor = Color.FromArgb("#7C828E");
    }

    private void UpdateVisibility()
    {
        if (AutoHideWhenUnavailable)
        {
            var isSupported = DataStore?.IsConnected == true &&
                              DataStore?.IsDataPointAvailable(VehicleDataPoint.ChargingStatus) == true;
            IsVisible = isSupported;
        }
    }

    private static string FormatTimeToFull(int minutes)
    {
        if (minutes < 60)
            return $"{minutes} min";

        var hours = minutes / 60;
        var mins = minutes % 60;

        if (mins == 0)
            return hours == 1 ? "1 hour" : $"{hours} hours";

        return $"{hours}h {mins}m";
    }

    #region INotifyPropertyChanged

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
