using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The overlay's last 30 seconds (Aero look design §5; 0.10.9, the kit's watts pill): a line 2 wide in the accent's lit
/// head (the kit's #EAFF7A), from <see cref="NowViewModel.Live"/>'s samples, with nothing under it. Each reading is placed
/// by its age, so a gap in the readings shows as one. It draws into one retained visual when the samples change, once a
/// second, and costs nothing between readings.
/// </summary>
internal sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IReadOnlyList<SparkSample>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondsProperty = DependencyProperty.Register(nameof(Seconds), typeof(double),
        typeof(Sparkline), new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The line's colour, the accent's lit head (<c>A.B.AccentHigh</c>) as the overlay sets it.</summary>
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush),
        typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Sparkline()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public IReadOnlyList<SparkSample>? Samples { get => (IReadOnlyList<SparkSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    public double Seconds { get => (double)GetValue(SecondsProperty); set => SetValue(SecondsProperty, value); }

    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>The line's points for the <paramref name="samples"/> no older than <paramref name="seconds"/>, oldest
    /// first, in <paramref name="size"/>: the newest at the right edge, one <paramref name="seconds"/> ago at the left;
    /// the lowest a pixel above the foot and the highest a pixel below the top, a flat line at mid height.</summary>
    public static IReadOnlyList<Point> Points(IReadOnlyList<SparkSample> samples, double seconds, Size size)
    {
        var shown = samples.Where(s => s.AgeSeconds <= seconds && double.IsFinite(s.Watts)).ToList();
        if (shown.Count == 0) return [];
        var low = shown.Min(s => s.Watts);
        var high = shown.Max(s => s.Watts);
        var (w, h) = (size.Width, size.Height);
        return [.. shown.Select(s => new Point(
            w * (1 - (s.AgeSeconds / seconds)),
            high > low ? h - 1 - ((s.Watts - low) / (high - low) * (h - 2)) : h / 2))];
    }

    protected override void OnRender(DrawingContext drawing)
    {
        if (Stroke is not SolidColorBrush { Color: var accent } || Samples is not { } samples) return;
        var points = Points(samples, Seconds, RenderSize);
        if (points.Count < 2) return;
        var line = new StreamGeometry();
        using (var path = line.Open())
        {
            path.BeginFigure(points[0], isFilled: false, isClosed: false);
            path.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        line.Freeze();
        // The kit's path: stroke-width 2, SVG's butt ends and mitred joins.
        var pen = new Pen(new SolidColorBrush(accent), 2) { LineJoin = PenLineJoin.Miter };
        pen.Freeze();
        drawing.DrawGeometry(null, pen, line);
    }
}
