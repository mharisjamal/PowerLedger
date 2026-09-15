using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The last minute (spec §9): an amber line over a faint amber fill and a dot at the newest reading, with round
/// gridlines labelled in a gutter on the right, clear of the line.</summary>
internal sealed class Sparkline : Instrument
{
    public static readonly DependencyProperty ValuesProperty = Register<IReadOnlyList<SparkSample>>(nameof(Values), [], typeof(Sparkline));

    private const double ControlHeight = 84;
    private const double Top = 10;
    private const double Bottom = 74;
    private const double Inset = 8;
    private const double Gutter = 30;

    public IReadOnlyList<SparkSample> Values { get => (IReadOnlyList<SparkSample>)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    internal override string Describe()
    {
        var values = Values;
        if (values.Count == 0) return "Last 60 seconds: no readings.";
        var culture = CultureInfo.CurrentCulture;
        return $"Last 60 seconds: between {Format.WholeWatts(values.Min(v => v.Watts), culture)} and {Format.WholeWatts(values.Max(v => v.Watts), culture)} W.";
    }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, ControlHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        if (values.Count == 0) return;
        double left = Inset, right = ActualWidth - Gutter;
        var (low, high, grid) = Geometry.SparkRange([.. values.Select(v => v.Watts)]);
        var gridPen = Line(LineBrush);
        foreach (var watts in grid)
        {
            var y = Bottom - (Bottom - Top) * (watts - low) / (high - low);
            dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
            DrawText(dc, Format.WholeWatts(watts, CultureInfo.CurrentCulture), ActualWidth, y - 7, LabelBrush, TextAlignment.Right);
        }

        var points = Geometry.SparkPoints(values, left, right, Top, Bottom, low, high);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], isFilled: false, isClosed: false);
            for (var i = 1; i < points.Count; i++) g.LineTo(points[i], isStroked: true, isSmoothJoin: true);
        }
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(points[0].X, Bottom), isFilled: true, isClosed: true);
            foreach (var point in points) g.LineTo(point, isStroked: false, isSmoothJoin: false);
            g.LineTo(new Point(points[^1].X, Bottom), isStroked: false, isSmoothJoin: false);
        }
        line.Freeze();
        area.Freeze();
        dc.DrawGeometry(AccentSoftBrush, null, area);
        dc.DrawGeometry(null, new Pen(AccentBrush, 1.6) { LineJoin = PenLineJoin.Round }, line);
        dc.DrawEllipse(AccentBrush, null, points[^1], 3.5, 3.5);
    }
}
