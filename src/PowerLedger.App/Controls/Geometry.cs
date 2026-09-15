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

    /// <summary>The day chart's top and gridline step: round steps that clear the tallest slot, never under 20 W.</summary>
    public static (double Max, double Step) ChartScale(double tallest)
    {
        var need = double.IsFinite(tallest) ? Math.Max(tallest, 20) : 20;
        var step = NiceStep(need, 4);
        return (step * Math.Ceiling(need / step), step);
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

    /// <summary>The top of each band in each slot, stacked from rest at the bottom to CPU at the top (spec §9). A negative rest stays at zero.</summary>
    public static (double[] RestTop, double[] DisplayTop, double[] GpuTop, double[] CpuTop) StackTops(IReadOnlyList<DaySlot> slots)
    {
        var rest = new double[slots.Count];
        var display = new double[slots.Count];
        var gpu = new double[slots.Count];
        var cpu = new double[slots.Count];
        for (var i = 0; i < slots.Count; i++)
        {
            rest[i] = Math.Max(0, slots[i].RestW);
            display[i] = rest[i] + Math.Max(0, slots[i].DisplayW);
            gpu[i] = display[i] + Math.Max(0, slots[i].GpuW);
            cpu[i] = gpu[i] + Math.Max(0, slots[i].CpuW);
        }
        return (rest, display, gpu, cpu);
    }
}
