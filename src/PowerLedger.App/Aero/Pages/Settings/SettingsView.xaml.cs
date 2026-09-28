using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace PowerLedger.App.Aero;

/// <summary>
/// Aero's Settings page (Aero look design §1 and §3, Plan S S1 and S2) over <see cref="SettingsViewModel"/>: the new Glass
/// and Overlay sections first, then every section the other looks have (tariff, machine, sampling, calibration,
/// preferences with the theme and the look, privacy, household, about), each a glass pane. Enter in a box of the
/// service's settings saves through <see cref="SettingsEntry"/>, as in the other looks.
/// </summary>
internal partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}

/// <summary>True when the two values are the same text, ignoring case: a preset's swatch is ticked while it is the
/// Colour style's tint.</summary>
internal sealed class SameText : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => values.Length == 2 && values[0] is string a && values[1] is string b && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [Binding.DoNothing, Binding.DoNothing];
}
