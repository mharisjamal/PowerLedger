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
