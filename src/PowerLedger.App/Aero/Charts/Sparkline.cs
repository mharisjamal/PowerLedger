using System.Windows;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// The overlay's last 30 seconds (Aero look design §5): a thin accent line under the watts, from
/// <see cref="NowViewModel.Live"/>'s samples, with a soft fill under it and its oldest seconds fading in from the left
/// rather than meeting a hard edge. Each reading is placed by its age, so a gap in the readings shows as one. It draws
/// into one retained visual when the samples change, once a second, and costs nothing between readings.
/// </summary>
internal sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples),
        typeof(IReadOnlyList<SparkSample>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondsProperty = DependencyProperty.Register(nameof(Seconds), typeof(double),
        typeof(Sparkline), new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The line's colour, the accent (<c>A.B.Accent</c>) as the overlay sets it.</summary>
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush),
        typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush FadeIn = Fade();

    public Sparkline()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        OpacityMask = FadeIn;
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
        var area = new StreamGeometry();
        using (var path = line.Open())
        {
            path.BeginFigure(points[0], isFilled: false, isClosed: false);
            path.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }
        using (var path = area.Open())
        {
            path.BeginFigure(new Point(points[0].X, RenderSize.Height), isFilled: true, isClosed: true);
            path.PolyLineTo([.. points, new Point(points[^1].X, RenderSize.Height)], isStroked: false, isSmoothJoin: false);
        }
        line.Freeze();
        area.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(70, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 90);
        fill.Freeze();
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(235, accent.R, accent.G, accent.B)), 1.5)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        drawing.DrawGeometry(fill, null, area);
        drawing.DrawGeometry(null, pen, line);
    }

    /// <summary>Clear at the left, whole from a third of the way in.</summary>
    private static Brush Fade()
    {
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, .35));
        mask.Freeze();
        return mask;
    }
}
