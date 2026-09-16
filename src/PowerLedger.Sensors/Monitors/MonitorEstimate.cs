namespace PowerLedger.Sensors;

/// <summary>
/// A monitor's on-mode and sleep watts from its size and resolution, for one the list doesn't know (spec §5): the median of
/// the certified monitors in the same size class and resolution class when there are at least three, otherwise Energy
/// Star's allowance formula scaled to what a monitor typically draws. A monitor that doesn't give its size or resolution
/// takes the median of them all. Estimated this way from all the others, each certified monitor's figure lands within 9% of
/// what it measured for half of them and within 26% for nine in ten, and one in a hundred needs the formula.
/// </summary>
public static class MonitorEstimate
{
    /// <summary>The largest listing that counts as a monitor, in inches. The list's larger entries are signage, and one is a
    /// 24-inch monitor whose size was entered in centimetres; they would otherwise make up a 49-inch class. A screen bigger
    /// than this is estimated from the formula.</summary>
    internal const double LargestMonitor = 57;

    /// <summary>How many alike monitors a median needs.</summary>
    private const int FewestAlike = 3;

    /// <summary>The commonest certified monitor, which one that doesn't give its size is taken for when there are too few
    /// monitors to take a median of.</summary>
    private const double CommonestInches = 23.8;

    private const int CommonestWidth = 1920;

    private const int CommonestHeight = 1080;

    /// <summary>Size classes in inches; a monitor belongs to the nearest, or the smaller of two as near. Portable monitors
    /// (13 to 17 inches) have a class of their own: they draw about two-thirds of what a 19-inch desk monitor does.</summary>
    private static readonly double[] SizeClasses = [16, 19, 22, 24, 27, 32, 34, 38, 43, 49];

    /// <summary>Resolution classes by megapixels, each an upper bound: 720p up to about 1080p, 1080p and 1200p, 1440p and
    /// ultrawide 1080p, ultrawide 1440p, and 4K. Anything more is a class of its own.</summary>
    private static readonly double[] PixelClasses = [1.3, 2.4, 4.2, 5.1, 9];

    /// <summary>The estimate for a monitor of this size and resolution.</summary>
    /// <param name="inches">The diagonal, or 0 when the monitor doesn't give one.</param>
    /// <param name="width">The native width in pixels, or 0 when unknown. The resolution may be given either way round.</param>
    /// <param name="height">The native height in pixels, or 0 when unknown.</param>
    /// <param name="catalogue">The certified monitors to go by.</param>
    public static (double OnW, double SleepW) For(double inches, int width, int height, MonitorCatalogue catalogue)
        => For(inches, width, height, catalogue.Monitors);

    /// <summary>The same, from any set of certified monitors, so tests can leave one out.</summary>
    internal static (double OnW, double SleepW) For(double inches, int width, int height, IEnumerable<CatalogueMonitor> certified)
    {
        var known = double.IsFinite(inches) && inches > 0 && width > 0 && height > 0;
        if (known && inches > LargestMonitor) return (Formula(inches, width, height), MonitorCatalogue.DefaultSleepW);

        var alike = certified.Where(monitor => monitor.Inches <= LargestMonitor).ToList();
        if (known)
        {
            var size = SizeClass(inches);
            var pixels = PixelClass(width, height);
            alike = alike.FindAll(monitor => SizeClass(monitor.Inches) == size && PixelClass(monitor.Width, monitor.Height) == pixels);
        }
        if (alike.Count >= FewestAlike)
        {
            return (MonitorCatalogue.Median(alike.Select(monitor => monitor.OnW)), MonitorCatalogue.Median(alike.Select(monitor => monitor.SleepW)));
        }
        return known
            ? (Formula(inches, width, height), MonitorCatalogue.DefaultSleepW)
            : (Formula(CommonestInches, CommonestWidth, CommonestHeight), MonitorCatalogue.DefaultSleepW);
    }

    /// <summary>
    /// On-mode watts from ENERGY STAR Displays Version 8.0, §3.3.2, Table 1, "Calculation of Maximum TEC (E_TEC_MAX) for
    /// Monitors in kWh": the most energy a certified monitor of this screen area and resolution may use in a year. §3.3.1's
    /// Equation 1 counts a year as 35% on and 65% asleep, so a monitor at the limit that sleeps at a quarter of a watt draws
    /// about 0.326 × E − 0.46 W when on. Certified monitors typically come in about a tenth under that: 0.296 × E − 0.5,
    /// and never less than 3 W.
    /// </summary>
    internal static double Formula(double inches, int width, int height)
    {
        var aspect = (double)Math.Max(width, height) / Math.Min(width, height);
        var area = aspect * inches * inches / (1 + aspect * aspect);
        var megapixels = (double)width * height / 1_000_000;
        var kwh = 4.00 * megapixels + area switch
        {
            < 190 => 0.172 * area + 1.50,
            < 210 => 0.020 * area + 30.40,
            < 315 => 0.091 * area + 15.40,
            _ => 0.182 * area - 13.20,
        };
        return Math.Max(3, 0.296 * kwh - 0.5);
    }

    private static double SizeClass(double inches)
    {
        var nearest = SizeClasses[0];
        foreach (var size in SizeClasses)
        {
            if (Math.Abs(size - inches) < Math.Abs(nearest - inches)) nearest = size;
        }
        return nearest;
    }

    private static int PixelClass(int width, int height)
    {
        var megapixels = (double)width * height / 1_000_000;
        var index = Array.FindIndex(PixelClasses, bound => megapixels <= bound);
        return index < 0 ? PixelClasses.Length : index;
    }
}
