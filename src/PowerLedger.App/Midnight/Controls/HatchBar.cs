using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App;

/// <summary>
/// A KPI's bar (plan O M1-2, drawn after the reference in 0.8.1): a dark 8 px track, the part done striped at 45° in the
/// accent over its soft tint, and a small upright marker at the value. With a <see cref="HatchBrush"/> the rest of the
/// track is hatched as well, which the chart's key uses to show what sleep looks like; without a
/// <see cref="MarkerBrush"/> there is no marker.
/// </summary>
internal sealed class HatchBar : Instrument
{
    public static readonly DependencyProperty ValueProperty = Register(nameof(Value), 0.0, typeof(HatchBar));
    public static readonly DependencyProperty FillBrushProperty = Register<Brush>(nameof(FillBrush), Brushes.Gray, typeof(HatchBar));
    public static readonly DependencyProperty SoftBrushProperty = Register<Brush?>(nameof(SoftBrush), null, typeof(HatchBar));
    public static readonly DependencyProperty HatchBrushProperty = Register<Brush?>(nameof(HatchBrush), null, typeof(HatchBar));
    public static readonly DependencyProperty TrackBrushProperty = Register<Brush>(nameof(TrackBrush), Brushes.Transparent, typeof(HatchBar));
    public static readonly DependencyProperty MarkerBrushProperty = Register<Brush?>(nameof(MarkerBrush), null, typeof(HatchBar));

    /// <summary>The bar's own height; the marker stands <see cref="MarkerReach"/> past it above and below.</summary>
    public const double BarHeight = 8;

    public const double MarkerReach = 3;

    public const double MarkerWidth = 2;

    private const double Corner = 2;

    /// <summary>How much of the track is filled, 0 to 1.</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>The stripes over the part done.</summary>
    public Brush FillBrush { get => (Brush)GetValue(FillBrushProperty); set => SetValue(FillBrushProperty, value); }

    /// <summary>The ground between the stripes; the track's when unset.</summary>
    public Brush? SoftBrush { get => (Brush?)GetValue(SoftBrushProperty); set => SetValue(SoftBrushProperty, value); }

    /// <summary>Hairlines over the rest of the track, as the chart hatches sleep; none when unset.</summary>
    public Brush? HatchBrush { get => (Brush?)GetValue(HatchBrushProperty); set => SetValue(HatchBrushProperty, value); }

    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    /// <summary>The upright mark at the value; none when unset.</summary>
    public Brush? MarkerBrush { get => (Brush?)GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }

    internal override string Describe() => $"{Format.Percent(Value, CultureInfo.CurrentCulture)} of the way.";

    /// <summary>The diagonal hatch the chart's sleep and its key share: hairlines 6 px apart, turned 45°.</summary>
    internal static DrawingBrush Hatch(Brush brush) => Diagonal(brush, 1, 0);

    /// <summary>The part done: stripes 3 px wide every 6 px, turned 45°.</summary>
    internal static DrawingBrush Stripes(Brush brush) => Diagonal(brush, 3, 1.5);

    /// <summary>Where along <paramref name="width"/> the fill ends: the value's share, nothing for a value that isn't one.</summary>
    internal static double FillWidth(double value, double width) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) * width : 0;

    /// <summary>The marker's box for a fill ending at <paramref name="fill"/>, kept inside the bar at either end.</summary>
    internal static Rect MarkerAt(double fill, double width)
    {
        var left = Math.Clamp(fill - MarkerWidth / 2, 0, Math.Max(0, width - MarkerWidth));
        return new Rect(left, 0, MarkerWidth, BarHeight + 2 * MarkerReach);
    }

    protected override Size MeasureOverride(Size availableSize) => Fixed(availableSize, BarHeight + 2 * MarkerReach);

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (!(width > 0)) return;
        var track = new Rect(0, MarkerReach, width, BarHeight);
        dc.PushClip(new RectangleGeometry(track, Corner, Corner));
        dc.DrawRectangle(TrackBrush, null, track);
        if (HatchBrush is { } hatch) dc.DrawRectangle(Hatch(hatch), null, track);
        var fill = FillWidth(Value, width);
        if (fill > 0)
        {
            var done = new Rect(0, MarkerReach, fill, BarHeight);
            if (SoftBrush is { } soft) dc.DrawRectangle(soft, null, done);
            dc.DrawRectangle(Stripes(FillBrush), null, done);
        }
        dc.Pop();
        if (MarkerBrush is { } marker) dc.DrawRoundedRectangle(marker, null, MarkerAt(fill, width), MarkerWidth / 2, MarkerWidth / 2);
    }

    /// <summary>Lines <paramref name="thickness"/> wide at <paramref name="at"/> across 6 px tiles, turned 45°.</summary>
    private static DrawingBrush Diagonal(Brush brush, double thickness, double at)
    {
        const double pitch = 6;
        var line = new GeometryDrawing(null, new Pen(brush, thickness), new LineGeometry(new Point(at, 0), new Point(at, pitch)));
        return new DrawingBrush(line)
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, pitch, pitch), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, pitch, pitch), ViewboxUnits = BrushMappingMode.Absolute, Transform = new RotateTransform(45),
        };
    }
}
