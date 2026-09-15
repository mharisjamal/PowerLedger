using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>The meter (spec §9): a tick scale from 0 to full scale, average and peak marks above it, and the amber needle at the live reading.</summary>
internal sealed class MeterScale : Instrument
{
    public static readonly DependencyProperty RangeProperty = Register(nameof(Range), MeterRange.For(0), typeof(MeterScale));
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0, typeof(MeterScale));
    public static readonly DependencyProperty AverageProperty = Register(nameof(Average), 0.0, typeof(MeterScale));
    public static readonly DependencyProperty PeakProperty = Register(nameof(Peak), 0.0, typeof(MeterScale));

    private const double Baseline = 28;
    private const double Inset = 10;

    public MeterRange Range { get => (MeterRange)GetValue(RangeProperty); set => SetValue(RangeProperty, value); }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public double Average { get => (double)GetValue(AverageProperty); set => SetValue(AverageProperty, value); }

    public double Peak { get => (double)GetValue(PeakProperty); set => SetValue(PeakProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, 52);

    protected override void OnRender(DrawingContext dc)
    {
        var range = Range;
        double left = Inset, right = ActualWidth - Inset;
        var tick = Line(StrongLineBrush);
        dc.DrawLine(tick, new Point(left, Baseline), new Point(right, Baseline));
        foreach (var (value, major) in range.Ticks())
        {
            var x = Geometry.ScaleX(value, range.Max, left, right);
            dc.DrawLine(tick, new Point(x, Baseline), new Point(x, Baseline + (major ? 11 : 5)));
            if (major) DrawText(dc, Format.WholeWatts(value, CultureInfo.CurrentCulture), x, Baseline + 13, LabelBrush, TextAlignment.Center);
        }

        Mark(dc, Average, "avg", range, left, right);
        Mark(dc, Peak, "peak", range, left, right);

        if (!double.IsFinite(Value)) return;   // no reading, no needle
        var needle = Geometry.ScaleX(Value, range.Max, left, right);
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(needle - 5, Baseline - 10), isFilled: true, isClosed: true);
            g.LineTo(new Point(needle + 5, Baseline - 10), isStroked: false, isSmoothJoin: false);
            g.LineTo(new Point(needle, Baseline - 1), isStroked: false, isSmoothJoin: false);
        }
        shape.Freeze();
        dc.DrawGeometry(AccentBrush, null, shape);
    }

    private void Mark(DrawingContext dc, double value, string label, MeterRange range, double left, double right)
    {
        if (!(value > 0)) return;
        var x = Geometry.ScaleX(value, range.Max, left, right);
        dc.DrawLine(Line(LabelBrush), new Point(x, Baseline - 7), new Point(x, Baseline));
        DrawText(dc, $"{label} {Format.WholeWatts(value, CultureInfo.CurrentCulture)}", x, 3, LabelBrush, TextAlignment.Center);
    }
}
