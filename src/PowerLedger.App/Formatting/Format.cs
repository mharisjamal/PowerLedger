using System.Globalization;

namespace PowerLedger.App;

/// <summary>How every number on screen is written (spec §9). Each method takes the culture to write in; the App passes the
/// user's. Nothing negative or undefined reaches the screen: a dash stands in for a value that does not exist.</summary>
internal static class Format
{
    public const string Missing = "–";

    /// <summary>The live reading, one decimal: "34.2".</summary>
    public static string Watts(double watts, CultureInfo culture)
        => double.IsFinite(watts) ? Math.Max(0, watts).ToString("0.0", culture) : Missing;

    /// <summary>Averages, peaks, watt-hours and scale labels, whole: "41".</summary>
    public static string WholeWatts(double watts, CultureInfo culture)
        => double.IsFinite(watts) ? Math.Round(Math.Max(0, watts)).ToString("0", culture) : Missing;

    /// <summary>Energy to three significant figures: 0.284, 2.74, 27.4, 274.</summary>
    public static string Kwh(double kwh, CultureInfo culture)
    {
        if (!double.IsFinite(kwh)) return Missing;
        kwh = Math.Max(0, kwh);
        var pattern = kwh < 1 ? "0.000" : kwh < 10 ? "0.00" : kwh < 100 ? "0.0" : "0";
        return kwh.ToString(pattern, culture);
    }

    /// <summary>A length of time given in hours: "7h 05m", "45m", "0m".</summary>
    public static string Duration(double hours)
    {
        var minutes = double.IsFinite(hours) ? (long)Math.Round(Math.Max(0, hours) * 60) : 0;
        return minutes >= 60
            ? (minutes / 60).ToString(CultureInfo.InvariantCulture) + "h " + (minutes % 60).ToString("00", CultureInfo.InvariantCulture) + "m"
            : minutes.ToString(CultureInfo.InvariantCulture) + "m";
    }

    /// <summary>Kilograms, two decimals: "0.11".</summary>
    public static string Kg(double kg, CultureInfo culture)
        => double.IsFinite(kg) ? Math.Max(0, kg).ToString("0.00", culture) : Missing;

    /// <summary>A share of a whole, as a whole percent: "43%".</summary>
    public static string Percent(double share, CultureInfo culture)
        => double.IsFinite(share) ? Math.Round(Math.Clamp(share, 0, 1) * 100).ToString("0", culture) + "%" : Missing;

    /// <summary>A scale label, or a value read against the scale, with the decimals its step needs: "40", "2.5", "0.25".</summary>
    public static string Scale(double value, double step, CultureInfo culture)
        => double.IsFinite(value) ? Math.Max(0, value).ToString(step >= 1 ? "0" : step >= 0.1 ? "0.0" : "0.00", culture) : Missing;

    /// <summary>A size on disk or as sent, rounded up so a non-empty file never reads "0 KB": "41 KB".</summary>
    public static string Kb(long bytes, CultureInfo culture) => $"{Math.Ceiling(Math.Max(0, bytes) / 1024.0).ToString("N0", culture)} KB";

    /// <summary>How long ago, in the coarsest unit that fits (households design §2): "2 minutes", "3 hours", "5 days";
    /// under a minute, "a few seconds". Negative spans, from clock skew, read as "a few seconds".</summary>
    public static string Ago(TimeSpan since, CultureInfo culture)
    {
        var span = since < TimeSpan.Zero ? TimeSpan.Zero : since;
        if (span < TimeSpan.FromMinutes(1)) return "a few seconds";
        if (span < TimeSpan.FromHours(1)) return Plural((int)span.TotalMinutes, "minute", culture);
        if (span < TimeSpan.FromDays(1)) return Plural((int)span.TotalHours, "hour", culture);
        return Plural((int)span.TotalDays, "day", culture);
    }

    private static string Plural(int count, string unit, CultureInfo culture)
        => $"{count.ToString(culture)} {unit}{(count == 1 ? "" : "s")}";
}
