using System.ComponentModel;
using System.Runtime.CompilerServices;
using ObdInsight.Core.Vehicles;

namespace ObdInsight.Controls;

/// <summary>
/// Widget displaying battery state of charge (SOC) and state of health (SOH).
/// Shows a progress bar for SOC and optional SOH percentage.
/// </summary>
public partial class BatteryWidget : ContentView, INotifyPropertyChanged
{
    /// <summary>
    /// Bindable property for the VehicleDataStore that provides data to this widget.
    /// </summary>
    public static readonly BindableProperty DataStoreProperty = BindableProperty.Create(
        nameof(DataStore),
        typeof(IVehicleDataStore),
        typeof(BatteryWidget),
        null,
        propertyChanged: OnDataStoreChanged);

    /// <summary>
    /// Bindable property to control whether the widget auto-hides when data is unavailable.
    /// </summary>
    public static readonly BindableProperty AutoHideWhenUnavailableProperty = BindableProperty.Create(
        nameof(AutoHideWhenUnavailable),
        typeof(bool),
        typeof(BatteryWidget),
        true);

    /// <summary>
    /// Bindable property for showing/hiding the SOH row.
    /// </summary>
    public static readonly BindableProperty ShowSohProperty = BindableProperty.Create(
        nameof(ShowSoh),
        typeof(bool),
        typeof(BatteryWidget),
        true);

    /// <summary>
    /// Bindable property for the widget title/label.
    /// </summary>
    public static readonly BindableProperty WidgetTitleProperty = BindableProperty.Create(
        nameof(WidgetTitle),
        typeof(string),
        typeof(BatteryWidget),
        "Battery");

    private string _socDisplayValue = "--";
    private string _sohDisplayValue = "--";

    public BatteryWidget()
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
    /// Gets or sets whether to show the SOH (State of Health) row.
    /// </summary>
    public bool ShowSoh
    {
        get => (bool)GetValue(ShowSohProperty);
        set => SetValue(ShowSohProperty, value);
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
    /// Gets the SOC display value.
    /// </summary>
    public string SocDisplayValue
    {
        get => _socDisplayValue;
        private set => SetProperty(ref _socDisplayValue, value);
    }

    /// <summary>
    /// Gets the SOH display value.
    /// </summary>
    public string SohDisplayValue
    {
        get => _sohDisplayValue;
        private set => SetProperty(ref _sohDisplayValue, value);
    }

    private static void OnDataStoreChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is BatteryWidget widget)
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
        if (e.PropertyName is nameof(IVehicleDataStore.BatterySoc) or
                             nameof(IVehicleDataStore.BatterySoh) or
                             nameof(IVehicleDataStore.IsConnected))
        {
            MainThread.BeginInvokeOnMainThread(UpdateDisplay);
        }
    }

    private void UpdateDisplay()
    {
        var soc = DataStore?.BatterySoc;
        var soh = DataStore?.BatterySoh;

        SocDisplayValue = soc.HasValue ? soc.Value.ToString("F0") : "--";
        SohDisplayValue = soh.HasValue ? $"{soh.Value:F0}%" : "--";

        UpdateProgressBar(soc ?? 0);
        UpdateVisibility();
    }

    private void UpdateProgressBar(double soc)
    {
        // Guard against being called before XAML is initialized
        if (SocProgressBar is null)
            return;

        var clampedSoc = Math.Clamp(soc, 0, 100);

        // Calculate width based on parent width and SOC percentage
        var parentWidth = SocProgressBar.Parent is View parent ? parent.Width : 100;
        if (parentWidth > 0 && !double.IsNaN(parentWidth))
        {
            SocProgressBar.WidthRequest = (parentWidth - 32) * (clampedSoc / 100.0); // Account for padding
        }

        // Update color based on SOC level
        SocProgressBar.BackgroundColor = soc switch
        {
            <= 10 => Color.FromArgb("#FF6B6B"),  // Danger - red
            <= 20 => Color.FromArgb("#FBBF24"),  // Warning - yellow
            _ => Color.FromArgb("#4FD1C5")       // Normal - teal accent
        };
    }

    private void UpdateVisibility()
    {
        if (AutoHideWhenUnavailable)
        {
            var isSupported = DataStore?.IsConnected == true &&
                              DataStore?.IsDataPointAvailable(VehicleDataPoint.BatteryStateOfCharge) == true;
            IsVisible = isSupported;
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        UpdateProgressBar(DataStore?.BatterySoc ?? 0);
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
