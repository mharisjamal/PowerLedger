using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// Today's chart (spec §9): five-minute slots stacked by band from rest at the bottom to CPU at the top, hatched where the
/// machine slept, a dashed amber line at now, and a dot at the day's peak.
/// </summary>
internal sealed class DayChart : Instrument
{
    public static readonly DependencyProperty SlotsProperty = Register<IReadOnlyList<DaySlot>>(nameof(Slots), [], typeof(DayChart));
    public static readonly DependencyProperty NowProperty = Register(nameof(Now), default(DateTimeOffset), typeof(DayChart));

    private const double Left = 36;
    private const double RightInset = 14;
    private const double Top = 16;
    private const double Bottom = 222;
    private const int SlotsPerDay = 288;

    public IReadOnlyList<DaySlot> Slots { get => (IReadOnlyList<DaySlot>)GetValue(SlotsProperty); set => SetValue(SlotsProperty, value); }

    public DateTimeOffset Now { get => (DateTimeOffset)GetValue(NowProperty); set => SetValue(NowProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, 250);

    protected override void OnRender(DrawingContext dc)
    {
        var slots = Slots;
        var right = ActualWidth - RightInset;
        double X(double slot) => Left + (right - Left) * slot / SlotsPerDay;
        var (max, step) = Geometry.ChartScale(slots.Count > 0 ? slots.Max(s => s.TotalW) : 0);
        double Y(double watts) => Bottom - (Bottom - Top) * Math.Clamp(watts / max, 0, 1);

        var grid = Line(LineBrush);
        for (var watts = 0.0; watts <= max + 1e-9; watts += step)
        {
            dc.DrawLine(grid, new Point(Left, Y(watts)), new Point(right, Y(watts)));
            DrawText(dc, Format.WholeWatts(watts, CultureInfo.CurrentCulture), Left - 8, Y(watts) - 7, LabelBrush, TextAlignment.Right);
        }
        for (var hour = 0; hour <= 24; hour += 6)
        {
            var align = hour == 0 ? TextAlignment.Left : hour == 24 ? TextAlignment.Right : TextAlignment.Center;
            DrawText(dc, $"{hour:00}:00", X(hour * 12), Bottom + 6, LabelBrush, align);
        }
        if (slots.Count == 0) return;

        DrawAsleep(dc, slots, X);
        var tops = Geometry.StackTops(slots);
        var zero = new double[slots.Count];
        Band(dc, zero, tops.RestTop, RestBrush, X, Y);
        Band(dc, tops.RestTop, tops.DisplayTop, DisplayBrush, X, Y);
        Band(dc, tops.DisplayTop, tops.GpuTop, GpuBrush, X, Y);
        Band(dc, tops.GpuTop, tops.CpuTop, CpuBrush, X, Y);

        var elapsed = Now == default ? slots.Count : (Now - slots[0].Start) / DaySlots.Length;
        var nowX = X(Math.Clamp(elapsed, 0, SlotsPerDay));
        dc.DrawLine(Line(AccentBrush, 1, new DashStyle([3, 3], 0)), new Point(nowX, Top), new Point(nowX, Bottom));
        DrawText(dc, "now", nowX + 5, Top - 2, AccentBrush);

        var peak = Enumerable.Range(0, slots.Count).MaxBy(i => slots[i].TotalW);
        if (slots[peak].TotalW > 0)
        {
            var at = new Point(X(peak + 0.5), Y(slots[peak].TotalW));
            dc.DrawEllipse(InkBrush, null, at, 2.5, 2.5);
            DrawText(dc, $"peak {Format.WholeWatts(slots[peak].TotalW, CultureInfo.CurrentCulture)} W", at.X - 6, at.Y - 18, LabelBrush, TextAlignment.Right);
        }
    }

    private void DrawAsleep(DrawingContext dc, IReadOnlyList<DaySlot> slots, Func<double, double> x)
    {
        var hatch = new DrawingBrush(new GeometryDrawing(null, Line(StrongLineBrush), new LineGeometry(new Point(0, 0), new Point(0, 6))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(45),
        };
        var i = 0;
        while (i < slots.Count)
        {
            if (slots[i].AsleepSeconds <= 0)
            {
                i++;
                continue;
            }
            var start = i;
            double seconds = 0;
            while (i < slots.Count && slots[i].AsleepSeconds > 0) seconds += slots[i++].AsleepSeconds;
            var rect = new Rect(new Point(x(start), Top), new Point(x(i), Bottom));
            dc.DrawRectangle(hatch, null, rect);
            var label = Text($"asleep · {Format.Duration(seconds / 3600)}", 10, LabelBrush);
            if (label.Width + 8 < rect.Width) dc.DrawText(label, new Point(rect.Left + (rect.Width - label.Width) / 2, Top + 6));
        }
    }

    private static void Band(DrawingContext dc, double[] lower, double[] upper, Brush brush, Func<double, double> x, Func<double, double> y)
    {
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(x(0), y(upper[0])), isFilled: true, isClosed: true);
            for (var i = 0; i < upper.Length; i++)
            {
                g.LineTo(new Point(x(i), y(upper[i])), isStroked: false, isSmoothJoin: false);
                g.LineTo(new Point(x(i + 1), y(upper[i])), isStroked: false, isSmoothJoin: false);
            }
            for (var i = lower.Length - 1; i >= 0; i--)
            {
                g.LineTo(new Point(x(i + 1), y(lower[i])), isStroked: false, isSmoothJoin: false);
                g.LineTo(new Point(x(i), y(lower[i])), isStroked: false, isSmoothJoin: false);
            }
        }
        shape.Freeze();
        dc.DrawGeometry(brush, null, shape);
    }
}
