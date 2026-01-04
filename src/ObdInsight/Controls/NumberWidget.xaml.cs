using CommunityToolkit.Mvvm.ComponentModel;
using Maui.BindableProperty.Generator.Core;

namespace ObdInsight.Controls;

/// <summary>
/// Generic widget displaying a single numeric value with an optional title, unit, and icon.
/// Designed for flexible data binding to any numeric property.
/// </summary>
public partial class NumberWidget : ContentView
{
    public NumberWidget()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The numeric value to display.
    /// </summary>
    [AutoBindable(OnChanged = nameof(OnValueOrFormatChanged))]
    private readonly double? _value;

    /// <summary>
    /// The format string for the numeric value (e.g., "F0", "F2", "N2").
    /// </summary>
    [AutoBindable(DefaultValue = "F0", OnChanged = nameof(OnValueOrFormatChanged))]
    private readonly string _format = "F0";

    /// <summary>
    /// The widget title.
    /// </summary>
    [AutoBindable]
    private readonly string _title = string.Empty;

    /// <summary>
    /// The unit text (e.g., "km", "kW", "%").
    /// </summary>
    [AutoBindable]
    private readonly string _unit = string.Empty;

    /// <summary>
    /// Whether to show the unit text.
    /// </summary>
    [AutoBindable]
    private readonly bool _showUnit = true;

    /// <summary>
    /// The FontAwesome icon character.
    /// </summary>
    [AutoBindable]
    private readonly string _icon = string.Empty;

    /// <summary>
    /// Whether to show the icon.
    /// </summary>
    [AutoBindable]
    private readonly bool _showIcon;

    /// <summary>
    /// The placeholder text shown when value is null.
    /// </summary>
    [AutoBindable(DefaultValue = "--")]
    private readonly string _placeholder = "--";

    /// <summary>
    /// Gets the formatted display value.
    /// </summary>
    [AutoBindable(DefaultValue = "--")]
    private string _displayValue = "--";

    private void OnValueOrFormatChanged()
    {
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (!Value.HasValue)
        {
            DisplayValue = Placeholder;
        }
        else
        {
            DisplayValue = Value.Value.ToString(Format);
        }
    }
}