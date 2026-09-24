using System.Globalization;
using System.Windows.Data;

namespace PowerLedger.App;

/// <summary>
/// A width less the fixed amount the converter parameter gives, never below nothing: the room a card leaves a piece of
/// text once the columns beside it are taken. It lets a switch's words wrap at the card's edge, which they otherwise
/// don't, since the switch lays its content out on one line. Before the card is laid out the text isn't held in.
/// </summary>
internal sealed class WidthLess : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var less = parameter is string text ? double.Parse(text, CultureInfo.InvariantCulture) : 0.0;
        return value is double width && width > 0 ? Math.Max(0, width - less) : double.PositiveInfinity;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
