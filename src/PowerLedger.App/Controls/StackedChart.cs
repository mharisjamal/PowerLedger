using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// The stacked chart for any range (spec §9): buckets stacked by band from rest at the bottom to CPU at the top, hatched
/// where the machine slept through most of a bucket, the time axis along the bottom, a dashed amber line at now, and a
/// dot at the peak.
/// </summary>
internal sealed class StackedChart : Instrument
{
    public static readonly DependencyProperty ModelProperty = Register(nameof(Model), ChartModel.Empty, typeof(StackedChart));

    private const double Left = 40;
    private const double RightInset = 14;
    private const double Top = 16;
    private const double AxisRoom = 26;

    public ChartModel Model { get => (ChartModel)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    internal override string Describe() => Model.Description;

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, 250);

    protected override void OnRender(DrawingContext dc)
    {
        var model = Model;
        var buckets = model.Buckets;
        var capacity = Math.Max(1, model.Capacity);
        var right = ActualWidth - RightInset;
        var bottom = ActualHeight - AxisRoom;
        double X(double at) => Left + (right - Left) * at / capacity;
        var (max, step) = Geometry.ChartScale(buckets.Count > 0 ? buckets.Max(b => b.Total) : 0, Charts.Floor(model.Unit));
        double Y(double value) => bottom - (bottom - Top) * Math.Clamp(value / max, 0, 1);
        var culture = CultureInfo.CurrentCulture;

        var grid = Line(LineBrush);
        for (var value = 0.0; value <= max + step / 1000; value += step)
        {
            dc.DrawLine(grid, new Point(Left, Y(value)), new Point(right, Y(value)));
            DrawText(dc, Format.Scale(value, step, culture), Left - 8, Y(value) - 7, LabelBrush, TextAlignment.Right);
        }
        for (var i = 0; i < model.Ticks.Count; i++)
        {
            var tick = model.Ticks[i];
            var x = X(tick.At);
            if (tick.At > 0 && tick.At < capacity) dc.DrawLine(grid, new Point(x, Top), new Point(x, bottom));
            var label = Text(tick.Label, 10, LabelBrush);
            var room = (i + 1 < model.Ticks.Count ? X(model.Ticks[i + 1].At) : right) - x;
            if (label.Width + 8 <= room) dc.DrawText(label, new Point(x + 4, bottom + 6));
        }
        if (buckets.Count == 0) return;

        DrawAsleep(dc, model, X, bottom);
        // Each band is filled down to zero, tallest first, so no two bands share an anti-aliased edge.
        var tops = Geometry.StackTops(buckets);
        Area(dc, tops.CpuTop, CpuBrush, X, Y);
        Area(dc, tops.GpuTop, GpuBrush, X, Y);
        Area(dc, tops.DisplayTop, DisplayBrush, X, Y);
        Area(dc, tops.RestTop, RestBrush, X, Y);

        if (model.NowAt is { } nowAt)
        {
            var nowX = X(Math.Clamp(nowAt, 0, capacity));
            dc.DrawLine(Line(AccentBrush, 1, new DashStyle([3, 3], 0)), new Point(nowX, Top), new Point(nowX, bottom));
            var now = Text("now", 10, AccentBrush);
            dc.DrawText(now, new Point(nowX + 5 + now.Width <= ActualWidth ? nowX + 5 : nowX - 5 - now.Width, Top - 2));
        }

        var peak = Enumerable.Range(0, buckets.Count).MaxBy(i => buckets[i].Total);
        if (buckets[peak].Total > 0)
        {
            var at = new Point(X(peak + 0.5), Y(buckets[peak].Total));
            dc.DrawEllipse(InkBrush, null, at, 2.5, 2.5);
            var label = Text($"peak {Format.Scale(buckets[peak].Total, step, culture)} {Charts.Symbol(model.Unit)}", 10, LabelBrush);
            dc.DrawText(label, new Point(at.X - 6 - label.Width >= Left ? at.X - 6 - label.Width : at.X + 6, at.Y - 18));
        }
    }

    /// <summary>Hatches each run of buckets the machine slept through most of, with how long it slept when there is room.</summary>
    private void DrawAsleep(DrawingContext dc, ChartModel model, Func<double, double> x, double bottom)
    {
        var buckets = model.Buckets;
        var most = model.Bucket.TotalSeconds / 2;
        var hatch = new DrawingBrush(new GeometryDrawing(null, Line(StrongLineBrush), new LineGeometry(new Point(0, 0), new Point(0, 6))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(45),
        };
        var i = 0;
        while (i < buckets.Count)
        {
            if (buckets[i].AsleepSeconds < most)
            {
                i++;
                continue;
            }
            var start = i;
            double seconds = 0;
            while (i < buckets.Count && buckets[i].AsleepSeconds >= most) seconds += buckets[i++].AsleepSeconds;
            var rect = new Rect(new Point(x(start), Top), new Point(x(i), bottom));
            dc.DrawRectangle(hatch, null, rect);
            var label = Text($"asleep · {Format.Duration(seconds / 3600)}", 10, LabelBrush);
            if (label.Width + 8 < rect.Width) dc.DrawText(label, new Point(rect.Left + (rect.Width - label.Width) / 2, Top + 6));
        }
    }

    /// <summary>A band's top as a line through the bucket centres, filled down to zero.</summary>
    private static void Area(DrawingContext dc, double[] top, Brush brush, Func<double, double> x, Func<double, double> y)
    {
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(x(0.5), y(0)), isFilled: true, isClosed: true);
            for (var i = 0; i < top.Length; i++) g.LineTo(new Point(x(i + 0.5), y(top[i])), isStroked: false, isSmoothJoin: false);
            g.LineTo(new Point(x(top.Length - 0.5), y(0)), isStroked: false, isSmoothJoin: false);
        }
        shape.Freeze();
        dc.DrawGeometry(brush, null, shape);
    }
}
