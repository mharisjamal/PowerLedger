using System.Windows;

namespace PowerLedger.App;

/// <summary>Every number the drawn controls need, as pure functions.</summary>
internal static class Geometry
{
    /// <summary>Where <paramref name="value"/> sits on a scale from 0 to <paramref name="max"/> drawn from left to right, clamped to the ends.</summary>
    public static double ScaleX(double value, double max, double left, double right)
        => max <= 0 || !double.IsFinite(value) ? left : left + (right - left) * Math.Clamp(value / max, 0, 1);

    /// <summary>A step of 1, 2 or 5 times a power of ten that gives about <paramref name="ticks"/> ticks over <paramref name="span"/>.</summary>
    public static double NiceStep(double span, int ticks)
    {
        if (!(span > 0) || ticks <= 0) return 1;
        var raw = span / ticks;
        var power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var fraction = raw / power;
        var nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
        return nice * power;
    }

    /// <summary>A chart's top and gridline step: round steps that clear the tallest bucket, never under <paramref name="floor"/>.</summary>
    public static (double Max, double Step) ChartScale(double tallest, double floor = 20)
    {
        var need = double.IsFinite(tallest) ? Math.Max(tallest, floor) : floor;
        var step = NiceStep(need, 4);
        return (step * Math.Ceiling(need / step - 1e-9), step);
    }

    /// <summary>The sparkline's range: the values with room above and below, and the round gridlines inside it.</summary>
    public static (double Low, double High, IReadOnlyList<double> Grid) SparkRange(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return (0, 10, [5]);
        var min = values.Min();
        var max = values.Max();
        var pad = Math.Max(2, (max - min) * 0.2);
        var low = Math.Max(0, min - pad);
        var high = max + pad;
        var step = NiceStep(high - low, 3);
        var grid = new List<double>();
        for (var line = Math.Ceiling(low / step) * step; line < high; line += step)
        {
            if (line > low) grid.Add(line);
        }
        return (low, high, grid);
    }

    /// <summary>The sparkline's points: the newest at the right edge, each older one placed by its age across
    /// <paramref name="spanSeconds"/>, so a minute with gaps in it is not stretched to fill the width.</summary>
    public static IReadOnlyList<Point> SparkPoints(
        IReadOnlyList<SparkSample> samples, double left, double right, double top, double bottom, double low, double high, double spanSeconds = 60)
    {
        var points = new Point[samples.Count];
        var height = high > low ? high - low : 1;
        for (var i = 0; i < samples.Count; i++)
        {
            var x = right - (right - left) * Math.Clamp(samples[i].AgeSeconds / spanSeconds, 0, 1);
            points[i] = new Point(x, bottom - (bottom - top) * Math.Clamp((samples[i].Watts - low) / height, 0, 1));
        }
        return points;
    }

    /// <summary>The top of each band in each bucket, stacked from rest at the bottom to CPU at the top (spec §9). A negative rest stays at zero.</summary>
    public static (double[] RestTop, double[] DisplayTop, double[] GpuTop, double[] CpuTop) StackTops(IReadOnlyList<ChartBucket> buckets)
    {
        var rest = new double[buckets.Count];
        var display = new double[buckets.Count];
        var gpu = new double[buckets.Count];
        var cpu = new double[buckets.Count];
        for (var i = 0; i < buckets.Count; i++)
        {
            rest[i] = Math.Max(0, buckets[i].Rest);
            display[i] = rest[i] + Math.Max(0, buckets[i].Display);
            gpu[i] = display[i] + Math.Max(0, buckets[i].Gpu);
            cpu[i] = gpu[i] + Math.Max(0, buckets[i].Cpu);
        }
        return (rest, display, gpu, cpu);
    }
}
