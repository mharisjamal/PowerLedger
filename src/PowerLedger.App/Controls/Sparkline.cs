using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The last minute (spec §9): an amber line over a faint amber fill, with round gridlines labelled in watts and a dot at the newest reading.</summary>
internal sealed class Sparkline : Instrument
{
    public static readonly DependencyProperty ValuesProperty = Register<IReadOnlyList<double>>(nameof(Values), [], typeof(Sparkline));

    private const double ControlHeight = 84;
    private const double Top = 10;
    private const double Bottom = 74;
    private const double Inset = 8;

    public IReadOnlyList<double> Values { get => (IReadOnlyList<double>)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, ControlHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double left = Inset, right = ActualWidth - Inset;
        var (low, high, grid) = Geometry.SparkRange(values);
        var gridPen = Line(LineBrush);
        foreach (var watts in grid)
        {
            var y = Bottom - (Bottom - Top) * (watts - low) / (high - low);
            dc.DrawLine(gridPen, new Point(left, y), new Point(right, y));
            DrawText(dc, Format.WholeWatts(watts, CultureInfo.CurrentCulture), left, y - 13, LabelBrush);   // left, clear of the newest-reading dot
        }
        if (values.Count == 0) return;

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
