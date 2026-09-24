using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// A KPI card's bar (plan O M1-2): a 6 px rounded track, the part done in a brush of the card's choosing, and the
/// remainder hatched at 45°, the way the reference draws what is left of a budget.
/// </summary>
internal sealed class HatchBar : Instrument
{
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0, typeof(HatchBar));
    public static readonly DependencyProperty FillBrushProperty = Register<Brush>(nameof(FillBrush), Brushes.Gray, typeof(HatchBar));
    public static readonly DependencyProperty HatchBrushProperty = Register<Brush>(nameof(HatchBrush), Brushes.Gray, typeof(HatchBar));
    public static readonly DependencyProperty TrackBrushProperty = Register<Brush>(nameof(TrackBrush), Brushes.Transparent, typeof(HatchBar));

    public const double BarHeight = 6;

    /// <summary>How much of the track is filled, 0 to 1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    public Brush HatchBrush { get => (Brush)GetValue(HatchBrushProperty); set => SetValue(HatchBrushProperty, value); }

    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    internal override string Describe() => $"{Format.Percent(Value, CultureInfo.CurrentCulture)} of the way.";

    /// <summary>The diagonal hatch the bars and the area chart share: hairlines 6 px apart, turned 45°.</summary>
    internal static DrawingBrush Hatch(Brush brush)
    {
        var hatch = new DrawingBrush(new GeometryDrawing(null, new Pen(brush, 1), new LineGeometry(new Point(0, 0), new Point(0, 6))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 6, 6), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 6, 6), ViewboxUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(45),
        };
        return hatch;
    }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, BarHeight);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (!(width > 0)) return;
        var track = new Rect(0, 0, width, BarHeight);
        dc.PushClip(new RectangleGeometry(track, BarHeight / 2, BarHeight / 2));
        dc.DrawRectangle(TrackBrush, null, track);
        dc.DrawRectangle(Hatch(HatchBrush), null, track);
        var fill = double.IsFinite(Value) ? Math.Clamp(Value, 0, 1) * width : 0;
        if (fill > 0) dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, 0, fill, BarHeight), BarHeight / 2, BarHeight / 2);
        dc.Pop();
    }
}
