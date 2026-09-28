using System.Globalization;

namespace PowerLedger.App;

/// <summary>How Aero's Dashboard words its figures (Aero look design §1), pure: the view binds these, the ViewModels keep
/// the numbers. No figure is negative or undefined on screen: a word stands in for one that doesn't exist.</summary>
internal static class DashboardFigures
{
    /// <summary>Power now as whole watts, or with <paramref name="cost"/> what an hour at this rate costs at the tariff:
    /// ("34", "W"), ("$0.006", "an hour"); watts again without a tariff, and "No reading" without a reading.</summary>
    public static (string Value, string Unit) PowerNow(double watts, bool cost, decimal? price, string? currency, CultureInfo culture)
    {
        if (!double.IsFinite(watts)) return (Format.NoReading, "");
        if (cost && price is { } rate && currency is not null)
            return (Money.Format(decimal.Round((decimal)Math.Max(0, watts) / 1000m * rate, 3), currency, culture, 3), "an hour");
        return (Format.WholeWatts(watts, culture), "W");
    }

    /// <summary>Today against yesterday to the same time, as a signed whole percentage: "+42%", "-8%"; "N/A" without yesterday.</summary>
    public static string Change(double? change, CultureInfo culture)
    {
        if (change is not { } c || !double.IsFinite(c)) return Format.Missing;
        var percent = Math.Round(c * 100);
        return (percent > 0 ? "+" : percent < 0 ? "-" : "") + Math.Abs(percent).ToString("0", culture) + "%";
    }

    /// <summary>This month's big figure: the cost so far, or the energy.</summary>
    public static string MonthBig(MonthLedger month, bool energy) => energy ? month.Energy + " kWh" : month.Cost;

    /// <summary>The line under it: the other figure so far and where the month is heading, as far as there is one.</summary>
    public static string MonthSub(MonthLedger month, bool energy)
    {
        var (soFar, heading) = energy ? (month.Cost, month.ProjectedEnergy) : (month.Energy + " kWh", month.Projected);
        if (soFar == Format.Missing) soFar = energy ? "No cost" : soFar;
        return heading is { Length: > 0 } && heading != Format.Missing && heading != "after a full day"
            ? $"{soFar} so far, on track for {heading}"
            : $"{soFar} so far";
    }

    /// <summary>"Day 8 of 30".</summary>
    public static string DayOf(int day, int days) => $"Day {day.ToString(CultureInfo.CurrentCulture)} of {days.ToString(CultureInfo.CurrentCulture)}";

    /// <summary>The Insights forecast's range for This month (design §4): "Likely $6.10 to $7.40 by the month's end", or
    /// "Needs a week of data" before it is ready; null before Insights have been read.</summary>
    public static string? Forecast(BillForecast? forecast, CultureInfo culture) => forecast switch
    {
        null => null,
        { Ready: false } => "Needs a week of data",
        { Currency: null } => "No tariff set, so no forecast",
        { } f => $"Likely {Money.Format(f.Low, f.Currency, culture)} to {Money.Format(f.High, f.Currency, culture)} by the month's end",
    };
}
