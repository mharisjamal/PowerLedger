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
    /// The words for a hovered bucket, "12:00 PM · 92 W": the bucket's start in the zone, as a time of day within one
    /// day, as a day and time over a longer range, as a day alone when a bucket is a day; and the value in the unit. With no
    /// start to count from, the value alone.
    /// </summary>
    public static string HoverLabel(DateTimeOffset? from, TimeSpan bucket, int index, int capacity, double value, ChartUnit unit, TimeZoneInfo zone, CultureInfo culture)
    {
        var amount = $"{Format.WholeWatts(value, culture)} {Charts.Symbol(unit)}";
        if (from is not { } start) return amount;
        var at = TimeZoneInfo.ConvertTime(start + bucket * index, zone);
        var time = at.ToString(culture.DateTimeFormat.ShortTimePattern, culture);
        var day = at.ToString("ddd d MMM", culture);
        var when = bucket >= TimeSpan.FromDays(1) ? day : bucket * capacity > TimeSpan.FromDays(1) ? $"{day} {time}" : time;
        return $"{when} · {amount}";
    }
}
