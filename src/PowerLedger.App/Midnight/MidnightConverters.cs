using System.Globalization;
using System.Windows.Data;

namespace PowerLedger.App;

/// <summary>A page's title for Midnight's top bar and page header: what its sidebar item says (Midnight look design §1).</summary>
internal sealed class PageTitle : IValueConverter
{
    public static string Of(Page page) => page switch
    {
        Page.Now or Page.Dashboard => "Dashboard",
        Page.Breakdown => "History",
        Page.Report => "Report",
        Page.Household => "Household",
        _ => "Settings",
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Page page ? Of(page) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True while a width is at least the converter parameter, for a layout that changes shape below a width: the
/// Dashboard's cards go from three across to one under it (plan O M1-4).</summary>
internal sealed class AtLeast : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double width && parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var least) && width >= least;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>A figure's number ("Number") or its unit ("Unit"), split at its last space, so a KPI card can set the unit
/// smaller than the number it follows: "34.2 W" is 34.2 and W; words without a number ("No reading") have no unit.</summary>
internal sealed class FigurePart : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var figure = value as string ?? "";
        var space = figure.LastIndexOf(' ');
        var unit = parameter as string == "Unit";
        if (space < 0 || !figure.Any(char.IsDigit)) return unit ? "" : figure;
        return unit ? figure[(space + 1)..] : figure[..space];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
