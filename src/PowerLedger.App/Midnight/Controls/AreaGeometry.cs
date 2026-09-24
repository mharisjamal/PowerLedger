using System.Globalization;
using System.Windows;

namespace PowerLedger.App;

/// <summary>The area chart's numbers (plan O M1-2), as pure functions: where a bucket sits, which bucket is under the
/// pointer, the line and the area beneath it, the axis labels, the sleep runs, and the words in the hover tooltip.</summary>
internal static class AreaGeometry
{
    /// <summary>The x of a bucket's centre on a plot from <paramref name="left"/> to <paramref name="right"/> that holds <paramref name="capacity"/> buckets.</summary>
    public static double X(double bucket, int capacity, double left, double right)
        => left + (right - left) * bucket / Math.Max(1, capacity);

    /// <summary>The y of <paramref name="value"/> on a scale from 0 at <paramref name="bottom"/> to <paramref name="max"/> at <paramref name="top"/>, clamped to the plot.</summary>
    public static double Y(double value, double max, double top, double bottom)
        => max > 0 && double.IsFinite(value) ? bottom - (bottom - top) * Math.Clamp(value / max, 0, 1) : bottom;

    /// <summary>The bucket under <paramref name="x"/>: the slot the plot's width gives it, held within the buckets there are; null when there are none.</summary>
    public static int? BucketAt(double x, int count, int capacity, double left, double right)
    {
        if (count <= 0 || !(right > left)) return null;
        var slot = (int)Math.Floor((x - left) / (right - left) * Math.Max(1, capacity));
        return Math.Clamp(slot, 0, count - 1);
    }

    /// <summary>The line through the buckets' values, one point per bucket at its centre.</summary>
    public static IReadOnlyList<Point> Line(IReadOnlyList<double> values, int capacity, double max, double left, double right, double top, double bottom)
    {
        var points = new Point[values.Count];
        for (var i = 0; i < values.Count; i++) points[i] = new Point(X(i + 0.5, capacity, left, right), Y(values[i], max, top, bottom));
        return points;
    }

    /// <summary>The area under <paramref name="line"/>: its points, then down to the baseline and back under the first point.</summary>
    public static IReadOnlyList<Point> Area(IReadOnlyList<Point> line, double bottom)
    {
        if (line.Count == 0) return [];
        var points = new Point[line.Count + 2];
        for (var i = 0; i < line.Count; i++) points[i] = line[i];
        points[line.Count] = new Point(line[^1].X, bottom);
        points[line.Count + 1] = new Point(line[0].X, bottom);
        return points;
    }

    /// <summary>
    /// <paramref name="line"/> as one smooth curve: a cubic Bézier from each point to the next, its control points a third
    /// of the way along on the tangents Fritsch and Butland's rule gives (level at a peak or a trough, a weighted harmonic
    /// mean of the slopes either side elsewhere, the end slopes at the ends). Monotone: each segment's control points stay
    /// between its ends, so the curve never swings above a peak or below the baseline, and it passes through every point,
    /// where the hover's dot sits.
    /// </summary>
    public static IReadOnlyList<(Point C1, Point C2, Point End)> Smooth(IReadOnlyList<Point> line)
    {
        var n = line.Count;
        if (n < 2) return [];
        var slopes = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            var h = line[i + 1].X - line[i].X;
            slopes[i] = h > 0 ? (line[i + 1].Y - line[i].Y) / h : 0;
        }
        var tangents = new double[n];
        tangents[0] = slopes[0];
        tangents[n - 1] = slopes[n - 2];
        for (var i = 1; i < n - 1; i++)
        {
            var (before, after) = (slopes[i - 1], slopes[i]);
            var (h0, h1) = (line[i].X - line[i - 1].X, line[i + 1].X - line[i].X);
            tangents[i] = before * after <= 0 ? 0 : 3 * (h0 + h1) / ((2 * h1 + h0) / before + (h1 + 2 * h0) / after);
        }
        var segments = new (Point, Point, Point)[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            var (from, to) = (line[i], line[i + 1]);
            var third = (to.X - from.X) / 3;
            var (low, high) = (Math.Min(from.Y, to.Y), Math.Max(from.Y, to.Y));
            var c1 = Math.Clamp(from.Y + tangents[i] * third, low, high);
            var c2 = Math.Clamp(to.Y - tangents[i + 1] * third, low, high);
            segments[i] = (new Point(from.X + third, c1), new Point(to.X - third, c2), to);
        }
        return segments;
    }

    /// <summary>The y axis: "0", then each step with the unit, "40 W", up to the top, with the decimals the step needs.</summary>
    public static IReadOnlyList<(double Value, string Label)> YLabels(double max, double step, string unit, CultureInfo culture)
    {
        var labels = new List<(double, string)>();
        if (!(step > 0) || !(max > 0)) return labels;
        var pattern = Math.Abs(step % 1) < 1e-9 ? "0" : Math.Abs(step * 10 % 1) < 1e-9 ? "0.0" : "0.00";
        for (var value = 0.0; value <= max + step / 1000; value += step)
        {
            labels.Add((value, value == 0 ? "0" : $"{value.ToString(pattern, culture)} {unit}"));
        }
        return labels;
    }

    /// <summary>Each run of buckets the machine slept through most of, as a start bucket and the bucket after its last.</summary>
    public static IReadOnlyList<(int Start, int End)> AsleepRuns(IReadOnlyList<ChartBucket> buckets, TimeSpan bucket)
    {
        var most = bucket.TotalSeconds / 2;
        var runs = new List<(int, int)>();
        var i = 0;
        while (i < buckets.Count)
        {
            if (buckets[i].AsleepSeconds < most)
            {
                i++;
                continue;
            }
            var start = i;
            while (i < buckets.Count && buckets[i].AsleepSeconds >= most) i++;
            runs.Add((start, i));
        }
        return runs;
    }

    /// <summary>
    /// The words for a hovered bucket, "12:00, 92 W", for a screen reader: <see cref="HoverWhen"/> and the value in the
    /// unit; with no start to count from, the value alone.
    /// </summary>
    public static string HoverLabel(DateTimeOffset? from, TimeSpan bucket, int index, int capacity, double value, ChartUnit unit, TimeZoneInfo zone, CultureInfo culture)
    {
        var amount = $"{Format.WholeWatts(value, culture)} {Charts.Symbol(unit)}";
        return HoverWhen(from, bucket, index, capacity, zone, culture) is { } when ? $"{when}, {amount}" : amount;
    }

    /// <summary>
    /// When a hovered bucket starts, in the zone: a time of day within one day, a day and time over a longer range, a day
    /// alone when a bucket is a day; null with no start to count from. The time is written as the axis under it writes its
    /// ticks (Charts: 24-hour), so the two never disagree; the day's name is the culture's.
    /// </summary>
    public static string? HoverWhen(DateTimeOffset? from, TimeSpan bucket, int index, int capacity, TimeZoneInfo zone, CultureInfo culture)
    {
        if (from is not { } start) return null;
        var at = TimeZoneInfo.ConvertTime(start + bucket * index, zone);
        var time = at.ToString("HH:mm", culture);
        var day = at.ToString("ddd d MMM", culture);
        return bucket >= TimeSpan.FromDays(1) ? day : bucket * capacity > TimeSpan.FromDays(1) ? $"{day} {time}" : time;
    }

    /// <summary>The tooltip's figure, named as the reference names its: "Power: 92 W", "Energy: 640 Wh".</summary>
    public static string HoverAmount(double value, ChartUnit unit, CultureInfo culture)
        => $"{(unit == ChartUnit.Watts ? "Power" : "Energy")}: {Format.WholeWatts(value, culture)} {Charts.Symbol(unit)}";
}
