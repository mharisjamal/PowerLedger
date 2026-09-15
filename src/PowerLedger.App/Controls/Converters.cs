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

/// <summary>Checks a rail button when the shell shows its page, and shows its page when it is checked.</summary>
internal sealed class PageIs : IValueConverter
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
