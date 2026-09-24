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
