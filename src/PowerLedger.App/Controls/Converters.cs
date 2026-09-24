using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PowerLedger.App;

/// <summary>True shows, false collapses; <see cref="Invert"/> turns it round.</summary>
internal sealed class VisibleWhen : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Shows an element while its text has something in it, so an empty message takes no room.</summary>
internal sealed class VisibleWhenText : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Shows an element while a value equals the converter parameter: a wizard step's panel while that step is current.</summary>
internal sealed class VisibleWhenStep : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Equals(value, parameter) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Checks a radio button when its value is the one chosen, and chooses it when it is checked.</summary>
internal sealed class ValueIs : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value, parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}

/// <summary>Spec §9's labels are uppercase; the view model keeps them in sentence case.</summary>
internal sealed class UpperCase : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string)?.ToUpper(culture) ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A 0-to-1 share to the width of a bar's fill, out of the track width the converter parameter gives (households
/// design §2: a bar per PC).</summary>
internal sealed class ShareWidth : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var track = parameter is string text ? double.Parse(text, CultureInfo.InvariantCulture) : 120.0;
        return value is double share ? Math.Clamp(share, 0, 1) * track : 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
