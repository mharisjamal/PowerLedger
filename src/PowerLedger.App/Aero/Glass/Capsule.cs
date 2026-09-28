using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PowerLedger.App.Aero;

/// <summary>
/// A capsule's corner radius: half the element's height. WPF squeezes an oversized radius (the HTML's 999 px pill) into an
/// ellipse, so Aero's pills bind their radius here instead. With a CornerRadius as the second value (a control's Tag),
/// that radius wins when it is a real one (under 100), for rows that are rounded rectangles rather than capsules.
/// </summary>
public sealed class Capsule : IValueConverter, IMultiValueConverter
{
    public static readonly Capsule Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new CornerRadius(value is double height && height > 0 ? height / 2 : 0);

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length > 1 && values[1] is CornerRadius radius && radius.TopLeft < 100) return radius;
        return Convert(values[0], targetType, parameter, culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
