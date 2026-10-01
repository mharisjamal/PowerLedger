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

    /// <summary>The line under Power now's watts (the mockup's "Measured by the CPU and GPU sensors"): what the reading
    /// comes from, out of the live line's source ("Live · battery discharge · 14:32:07"); "Waiting for the service"
    /// before a reading.</summary>
    public static string MeasuredBy(LivePanel live)
    {
        var parts = live.Eyebrow.Split(" · ");
        if (!double.IsFinite(live.Watts) || parts.Length < 3) return "Waiting for the service";
        return parts[1] switch
        {
            "battery discharge" => "Measured by the battery",
            "UPS output" => "Measured by the UPS",
            "power supply reading" => "Measured by the power supply",
            "platform meter" => "Measured by the processor's own meter",
            "power meter reading" => "Measured by the PC's power meter",
            "management controller" => "Measured by the management controller",
            "calibrated model" => "From the sensors, calibrated on battery",
            _ => "Estimated from the CPU and GPU sensors",
        };
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

    /// <summary>A figure part of the way through the intro's count-up (the demo's <c>countUp</c>): the first number in
    /// <paramref name="shown"/> at <paramref name="k"/> of its value (0 to 1), with its decimals and grouping kept and the
    /// words round it as they are: "$0.47" at a half is "$0.24", "2.74 kWh" "1.37 kWh". Text without a number, or at 1,
    /// is left as it is.</summary>
    public static string Counted(string shown, double k, CultureInfo culture)
    {
        if (k >= 1 || string.IsNullOrEmpty(shown)) return shown;
        var start = shown.IndexOfAny(Digits);
        if (start < 0) return shown;
        var format = culture.NumberFormat;
        var point = format.NumberDecimalSeparator is [var p] ? p : '.';
        var groups = new[] { format.NumberGroupSeparator, format.CurrencyGroupSeparator }.Where(g => g.Length == 1).Select(g => g[0]).ToHashSet();
        var end = start;
        while (end < shown.Length && (char.IsAsciiDigit(shown[end])
               || end + 1 < shown.Length && char.IsAsciiDigit(shown[end + 1]) && (shown[end] == point || groups.Contains(shown[end]))))
            end++;
        var token = shown[start..end];
        var at = token.LastIndexOf(point);
        var decimals = at < 0 ? 0 : token.Length - at - 1;
        var group = token.FirstOrDefault(groups.Contains);
        if (!decimal.TryParse(new string([.. token.Where(char.IsAsciiDigit)]), NumberStyles.None, CultureInfo.InvariantCulture, out var units)) return shown;
        var scale = (decimal)Math.Pow(10, decimals);
        var counted = decimal.Round(units * (decimal)Math.Clamp(k, 0, 1), MidpointRounding.AwayFromZero);
        var integer = decimal.Truncate(counted / scale).ToString(CultureInfo.InvariantCulture);
        if (group != default)
            for (var i = integer.Length - 3; i > 0; i -= 3) integer = integer.Insert(i, group.ToString());
        var fraction = decimals == 0 ? "" : point + (counted % scale).ToString("0", CultureInfo.InvariantCulture).PadLeft(decimals, '0');
        return shown[..start] + integer + fraction + shown[end..];
    }

    private static readonly char[] Digits = ['0', '1', '2', '3', '4', '5', '6', '7', '8', '9'];

    /// <summary>"Day 8 of 30".</summary>
    public static string DayOf(int day, int days) => $"Day {day.ToString(CultureInfo.CurrentCulture)} of {days.ToString(CultureInfo.CurrentCulture)}";

    /// <summary>The Insights forecast's range for This month (design §4): "Likely $6.10 to $7.40 by the month's end", or
    /// "Needs a week of data" before it is ready; null before Insights have been read.</summary>
    public static string? Forecast(BillForecast? forecast, CultureInfo culture) => forecast switch
    {
        null => null,
        { Ready: false } => "Needs a week of data",
        { Currency: null } f => $"Likely {Format.Kwh(f.LowKwh, culture)} to {Format.Kwh(f.HighKwh, culture)} kWh by the month's end",
        { } f => $"Likely {Money.Format(f.Low, f.Currency, culture)} to {Money.Format(f.High, f.Currency, culture)} by the month's end",
    };
}
