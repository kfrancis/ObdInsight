using System.ComponentModel;
using System.Runtime.CompilerServices;
using ObdInsight.Core.Vehicles;

namespace ObdInsight.Controls;

/// <summary>
/// Widget displaying estimated remaining range.
/// Supports both km and miles display with optional efficiency info.
/// </summary>
public partial class RangeWidget : ContentView, INotifyPropertyChanged
{
    /// <summary>
    /// Bindable property for the VehicleDataStore that provides data to this widget.
    /// </summary>
    public static readonly BindableProperty DataStoreProperty = BindableProperty.Create(
        nameof(DataStore),
        typeof(IVehicleDataStore),
        typeof(RangeWidget),
        null,
        propertyChanged: OnDataStoreChanged);

    /// <summary>
    /// Bindable property to control whether the widget auto-hides when data is unavailable.
    /// </summary>
    public static readonly BindableProperty AutoHideWhenUnavailableProperty = BindableProperty.Create(
        nameof(AutoHideWhenUnavailable),
        typeof(bool),
        typeof(RangeWidget),
        true);

    /// <summary>
    /// Bindable property for the unit system (km or mi).
    /// </summary>
    public static readonly BindableProperty UseMetricUnitsProperty = BindableProperty.Create(
        nameof(UseMetricUnits),
        typeof(bool),
        typeof(RangeWidget),
        true,
        propertyChanged: OnUnitSystemChanged);

    /// <summary>
    /// Bindable property for showing efficiency info.
    /// </summary>
    public static readonly BindableProperty ShowEfficiencyProperty = BindableProperty.Create(
        nameof(ShowEfficiency),
        typeof(bool),
        typeof(RangeWidget),
        false);

    /// <summary>
    /// Bindable property for the widget title/label.
    /// </summary>
    public static readonly BindableProperty WidgetTitleProperty = BindableProperty.Create(
        nameof(WidgetTitle),
        typeof(string),
        typeof(RangeWidget),
        "Range");

    private string _rangeDisplayValue = "--";
    private string _unitDisplay = "km";
    private string _efficiencyText = string.Empty;

    public RangeWidget()
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
    /// Gets or sets whether to use metric units (km vs miles).
    /// </summary>
    public bool UseMetricUnits
    {
        get => (bool)GetValue(UseMetricUnitsProperty);
        set => SetValue(UseMetricUnitsProperty, value);
    }

    /// <summary>
    /// Gets or sets whether to show the efficiency row.
    /// </summary>
    public bool ShowEfficiency
    {
        get => (bool)GetValue(ShowEfficiencyProperty);
        set => SetValue(ShowEfficiencyProperty, value);
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
    /// Gets the formatted range value for display.
    /// </summary>
    public string RangeDisplayValue
    {
        get => _rangeDisplayValue;
        private set => SetProperty(ref _rangeDisplayValue, value);
    }

    /// <summary>
    /// Gets the unit string (km or mi).
    /// </summary>
    public string UnitDisplay
    {
        get => _unitDisplay;
        private set => SetProperty(ref _unitDisplay, value);
    }

    /// <summary>
    /// Gets the efficiency text for display.
    /// </summary>
    public string EfficiencyText
    {
        get => _efficiencyText;
        private set => SetProperty(ref _efficiencyText, value);
    }

    private static void OnDataStoreChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is RangeWidget widget)
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

    private static void OnUnitSystemChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is RangeWidget widget)
        {
            widget.UpdateDisplay();
        }
    }

    private void OnDataStorePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IVehicleDataStore.RangeRemaining) or
                             nameof(IVehicleDataStore.IsConnected))
        {
            MainThread.BeginInvokeOnMainThread(UpdateDisplay);
        }
    }

    private void UpdateDisplay()
    {
        var rangeKm = DataStore?.RangeRemaining;

        if (!rangeKm.HasValue)
        {
            RangeDisplayValue = "--";
            UnitDisplay = UseMetricUnits ? "km" : "mi";
            EfficiencyText = string.Empty;
        }
        else if (UseMetricUnits)
        {
            RangeDisplayValue = rangeKm.Value.ToString("F0");
            UnitDisplay = "km";
        }
        else
        {
            var rangeMiles = rangeKm.Value * 0.621371;
            RangeDisplayValue = rangeMiles.ToString("F0");
            UnitDisplay = "mi";
        }

        // Update efficiency text if we have the data
        var consumption = DataStore?.GetValue(VehicleDataPoint.EnergyConsumption);
        if (consumption is double kwhPer100km && ShowEfficiency)
        {
            EfficiencyText = UseMetricUnits
                ? $"{kwhPer100km:F1} kWh/100km"
                : $"{kwhPer100km * 1.609:F1} kWh/100mi";
        }
        else
        {
            EfficiencyText = string.Empty;
        }

        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (AutoHideWhenUnavailable)
        {
            var isSupported = DataStore?.IsConnected == true &&
                              DataStore?.IsDataPointAvailable(VehicleDataPoint.RangeRemaining) == true;
            IsVisible = isSupported;
        }
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
