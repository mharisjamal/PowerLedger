using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The Parts page's 7-day trend (Aero look design §1; Plan S, P2): each day's energy of one part, oldest first, as a small
/// line with a faint wash under it and a dot on the last day. The scale starts at nothing, not at the week's least, so a
/// steady part draws a steady line and a small change looks small. Drawn, not templated, so a page of parts costs one
/// visual each; it describes itself to a screen reader in words.
/// </summary>
internal sealed class PartTrend : Instrument
{
    public static readonly DependencyProperty ValuesProperty = Register<IReadOnlyList<double>>(nameof(Values), [], typeof(PartTrend));
    public static readonly DependencyProperty StrokeBrushProperty = Register<Brush>(nameof(StrokeBrush), Brushes.Gray, typeof(PartTrend));

    /// <summary>Room kept at the edges, so the line's ends and the dot are not clipped.</summary>
    public const double Pad = 3;

    /// <summary>How strong the wash under the line is.</summary>
    public const double WashOpacity = 0.16;

    /// <summary>Each day's energy in Wh, oldest first (<see cref="DashboardPart.Last7DaysWh"/>).</summary>
    public IReadOnlyList<double> Values { get => (IReadOnlyList<double>)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    /// <summary>The part's colour, as its dot in the pie and the table.</summary>
    public Brush StrokeBrush { get => (Brush)GetValue(StrokeBrushProperty); set => SetValue(StrokeBrushProperty, value); }

    /// <summary>Where each day sits in a box <paramref name="width"/> × <paramref name="height"/>, <paramref name="pad"/>
    /// in from its edges: evenly across, and up from the foot by its share of the most. What isn't a figure, or is below
    /// nothing, counts as nothing; fewer than two days, or no room, draw nothing.</summary>
    internal static IReadOnlyList<Point> Points(IReadOnlyList<double> wh, double width, double height, double pad)
    {
        if (wh.Count < 2 || !(width > 2 * pad) || !(height > 2 * pad)) return [];
        var values = wh.Select(Clean).ToList();
        var most = values.Max();
        var step = (width - 2 * pad) / (values.Count - 1);
        var foot = height - pad;
        var rise = height - 2 * pad;
        return [.. values.Select((v, i) => new Point(pad + i * step, most > 0 ? foot - v / most * rise : foot))];
    }

    /// <summary>The trend in a sentence: the least and most day and the last one, in kWh.</summary>
    internal static string Describe(IReadOnlyList<double> wh, CultureInfo culture)
    {
        if (wh.Count == 0) return "No days recorded yet.";
        var values = wh.Select(Clean).ToList();
        string Kwh(double v) => Format.Kwh(v / 1000, culture) + " kWh";
        if (values.Count == 1) return $"One day so far: {Kwh(values[0])}.";
        return $"Each day over the last {values.Count} days: {Format.Kwh(values.Min() / 1000, culture)} to {Kwh(values.Max())}, {Kwh(values[^1])} on the last.";
    }

    internal override string Describe() => Describe(Values, CultureInfo.CurrentCulture);

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 96 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 28 : availableSize.Height);

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points(Values, ActualWidth, ActualHeight, Pad);
        if (points.Count == 0) return;
        var line = new StreamGeometry();
        using (var context = line.Open())
        {
            context.BeginFigure(points[0], false, false);
            context.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        line.Freeze();
        var wash = new StreamGeometry();
        using (var context = wash.Open())
        {
            context.BeginFigure(new Point(points[0].X, ActualHeight - Pad), true, true);
            context.PolyLineTo([.. points, new Point(points[^1].X, ActualHeight - Pad)], false, false);
        }
        wash.Freeze();
        dc.PushOpacity(WashOpacity);
        dc.DrawGeometry(StrokeBrush, null, wash);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(StrokeBrush, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        dc.DrawEllipse(StrokeBrush, null, points[^1], 2.6, 2.6);
    }

    private static double Clean(double wh) => double.IsFinite(wh) ? Math.Max(0, wh) : 0;
}
