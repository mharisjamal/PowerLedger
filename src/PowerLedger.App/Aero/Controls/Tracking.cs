using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PowerLedger.App.Aero;

/// <summary>
/// The demo's letter-spacing, which WPF text can't draw, kept in a button's width instead: <see cref="SegPadding"/> is the
/// seg's 20 px each side plus half of what .03em between each of the label's letters adds (Content and FontSize bound),
/// so the white pill that slides behind the chosen span has the demo's size.
/// </summary>
public sealed class Tracking(double side, double em) : IMultiValueConverter
{
    /// <summary>The seg (History's Day, Week, Month, Year): 20 px each side, .03em between letters.</summary>
    public static readonly Tracking SegPadding = new(20, .03);

    /// <summary>The padding for a label of <paramref name="letters"/> letters at <paramref name="size"/>.</summary>
    public Thickness For(int letters, double size)
    {
        var each = side + letters * em * size / 2;
        return new Thickness(each, 0, each, 0);
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        => For(values.Length > 0 && values[0] is string text ? text.Length : 0, values.Length > 1 && values[1] is double size && double.IsFinite(size) ? size : 0);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
