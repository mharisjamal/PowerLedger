using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerLedger.App.Aero;

/// <summary>
/// Last minute's scale (Aero look design §1; 0.10.9, the liquid glass mockup's 400 by 180 drawing), pure: the readings of
/// the last 60 s from a minute ago at the chart's left edge to now at its right, and watts on round steps that hold every
/// reading and the day's average with room above and below, the line in the drawing's middle band as the mockup's.
/// </summary>
/// <param name="Grid">The gridlines' watts, bottom up, the top one at <see cref="Hi"/>.</param>
internal sealed record LiveScale(double Lo, double Hi, double Step, IReadOnlyList<double> Grid)
{
    public const double X0 = 0, X1 = 400, Y0 = 36, Y1 = 140, Width = 400, Height = 180, Span = 60;

    /// <summary>A scale for <paramref name="watts"/> and the <paramref name="average"/> the ghost line marks; 0 to 100 W
    /// with nothing to hold, and never below nothing.</summary>
    public static LiveScale For(IReadOnlyList<double> watts, double average)
    {
        var values = watts.Where(double.IsFinite).ToList();
        if (double.IsFinite(average) && values.Count > 0) values.Add(average);
        if (values.Count == 0) return Build(0, 100, 25);
        double min = values.Min(), max = values.Max();
        var pad = Math.Max(Math.Max((max - min) * .15, max * .05), 1);
        var step = ChartInk.NiceStep(max - min + 2 * pad, 4);
        return Build(Math.Max(0, Math.Floor((min - pad) / step) * step), Math.Ceiling((max + pad) / step) * step, step);
    }

    private static LiveScale Build(double lo, double hi, double step)
    {
        var grid = new List<double>();
        for (var w = lo + step; w <= hi + step / 1000; w += step) grid.Add(Math.Round(w, 6));
        return new LiveScale(lo, hi, step, grid);
    }

    /// <summary>Where a reading <paramref name="ageSeconds"/> old sits across the drawing: now at the right, a minute ago
    /// (or older) at the left.</summary>
    public static double X(double ageSeconds) => X1 - Math.Clamp(ageSeconds, 0, Span) / Span * (X1 - X0);

    /// <summary>Where <paramref name="watts"/> sits up the drawing.</summary>
    public double Y(double watts) => Y1 - (watts - Lo) / (Hi - Lo) * (Y1 - Y0);
}

/// <summary>
/// Last minute (Aero look design §1; 0.10.9, the liquid glass mockup's): the last 60 s of <see cref="LivePanel.Spark"/> as
/// the accent line with its glow (drop-shadow 0 0 5px at 70 %) over its fill (the accent at 38 % fading to nothing), and
/// its end dot on the newest reading (5.5 across, the navy inside, the accent's ring); no axis, as the mockup has none.
/// The pointer reads any second: a dot and its watts on a pill. A new reading
/// redraws the line once, in place: no slide, so a reading costs the compositor one frame, not a second of them (Plan U:
/// the 1 s slide on every 1 s reading kept the render thread drawing at 60 fps, a third to a half of a core).
/// </summary>
internal sealed class LiveChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples), typeof(IReadOnlyList<SparkSample>),
        typeof(LiveChart), new PropertyMetadata(null, (d, _) => ((LiveChart)d).OnSamples()));

    public static readonly DependencyProperty AverageProperty = DependencyProperty.Register(nameof(Average), typeof(double),
        typeof(LiveChart), new PropertyMetadata(double.NaN, (d, _) => ((LiveChart)d).Redraw()));

    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(nameof(Reveal), typeof(double),
        typeof(LiveChart), new PropertyMetadata(1.0, (d, _) => ((LiveChart)d).UpdateReveal()));

    private readonly VisualCollection _kids;
    private readonly DrawingVisual _static = new(), _line = new(), _marker = new(), _hover = new();
    private readonly ContainerVisual _clip = new();
    private object? _staticKey;
    private bool _hoverDrawn;
    private LiveScale _scale = LiveScale.For([], double.NaN);
    private double? _hoverAge;

    public LiveChart()
    {
        Cursor = Cursors.Cross;
        _kids = new VisualCollection(this) { _static, _clip, _marker, _hover };
        _clip.Children.Add(_line);
        AutomationProperties.SetName(this, "Last minute");
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>The last minute's readings, each with its age, as the live panel has them.</summary>
    public IReadOnlyList<SparkSample>? Samples { get => (IReadOnlyList<SparkSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    /// <summary>The day's average watts, which the scale holds so the line keeps its place; NaN for none.</summary>
    public double Average { get => (double)GetValue(AverageProperty); set => SetValue(AverageProperty, value); }

    /// <summary>How much of the line is drawn, 0 to 1: the intro draws it in from the left.</summary>
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    /// <summary>The scale the chart is drawn on, for a test.</summary>
    internal LiveScale Scale => _scale;

    protected override int VisualChildrenCount => _kids.Count;

    protected override Visual GetVisualChild(int index) => _kids[index];

    /// <summary>The drawing's 400 by 180 laid in the element as the mockup's SVG lays its view box (0.10.9's audit): one
    /// scale, the smaller of the two, and centred (preserveAspectRatio's xMidYMid meet).</summary>
    private double S => Math.Min(ActualWidth / LiveScale.Width, ActualHeight / LiveScale.Height);

    private double Ox => (ActualWidth - LiveScale.Width * S) / 2;

    private double Oy => (ActualHeight - LiveScale.Height * S) / 2;

    private Point Map(double x, double y) => new(Ox + x * S, Oy + y * S);

    private IReadOnlyList<SparkSample> Readings => Samples ?? [];

    /// <summary>A new reading: the line is redrawn where it now is.</summary>
    private void OnSamples()
    {
        Redraw();
        UpdateAutomation();
    }

    private void UpdateAutomation()
    {
        var newest = Readings.OrderBy(s => s.AgeSeconds).FirstOrDefault();
        AutomationProperties.SetHelpText(this, Readings.Count == 0 ? Format.NoReading
            : $"{Format.WholeWatts(newest.Watts, CultureInfo.CurrentCulture)} W now, {Format.WholeWatts(Readings.Max(s => s.Watts), CultureInfo.CurrentCulture)} W at the most in the last minute");
    }

    private void Redraw()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var readings = Readings;
        _scale = LiveScale.For([.. readings.Select(s => s.Watts)], Average);
        // The mockup's lit lime: the line #E8FF78, its fill and glow #E2FB66 (GlassMaterial.AccentShades for any accent).
        var accent = ChartInk.Colour(this, "A.C.AccentLine", Color.FromRgb(0xE8, 0xFF, 0x78));
        var lit = ChartInk.Colour(this, "A.C.AccentLineFill", Color.FromRgb(0xE2, 0xFB, 0x66));
        var axis = ChartInk.Brush(this, "A.C.Text3", Color.FromArgb(0x70, 0xF3, 0xF4, 0xF6));
        // What stays is drawn again only when it changes: a reading moves the line alone (Plan U).
        var key = (RenderSize, readings.Count == 0, axis.Color, CultureInfo.CurrentCulture.Name);
        if (!key.Equals(_staticKey))
        {
            _staticKey = key;
            DrawStatic(readings, axis);
        }
        var points = readings.OrderByDescending(s => s.AgeSeconds).Select(s => Map(LiveScale.X(s.AgeSeconds), _scale.Y(s.Watts))).ToList();
        var foot = Map(0, LiveScale.Height).Y;
        using (var dc = _line.RenderOpen())
        {
            if (points.Count > 1)
            {
                var line = Polyline(points, close: false);
                var fill = new LinearGradientBrush(Color.FromArgb(0x61, lit.R, lit.G, lit.B), Color.FromArgb(0, lit.R, lit.G, lit.B), 90);
                fill.Freeze();
                dc.DrawGeometry(fill, null, Polyline([.. points, new Point(points[^1].X, foot), new Point(points[0].X, foot)], close: true));
                foreach (var (alpha, width) in new[] { (0x1C, 12.0), (0x40, 7.0) })
                    dc.DrawGeometry(null, ChartInk.Pen(ChartInk.Brush(Color.FromArgb((byte)alpha, lit.R, lit.G, lit.B)), width, round: true), line);
                dc.DrawGeometry(null, ChartInk.Pen(ChartInk.Brush(accent), 2.4, round: true), line);
            }
        }
        using (var dc = _marker.RenderOpen())
        {
            if (readings.Count > 0 && _hoverAge is null)
            {
                var newest = readings.OrderBy(s => s.AgeSeconds).First();
                var at = Map(LiveScale.X(0), _scale.Y(newest.Watts));
                Marker(dc, at.X, at.Y, newest.Watts, accent, pill: false);
            }
        }
        if (_hoverAge is not null || _hoverDrawn) DrawHover(accent);
        UpdateReveal();
    }

    /// <summary>"No reading" while there is none; the mockup's chart has no axis, gridlines or ghost line.</summary>
    private void DrawStatic(IReadOnlyList<SparkSample> readings, Brush axis)
    {
        using var dc = _static.RenderOpen();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (readings.Count == 0) ChartInk.At(dc, ChartInk.Text(this, Format.NoReading, 13, axis), ActualWidth / 2, ActualHeight / 2, 1);
    }

    /// <summary>The mockup's end dot (5.5 across, navy inside, the accent's ring 2.5 wide); under the pointer, its watts
    /// on a pill above it too.</summary>
    private void Marker(DrawingContext dc, double x, double y, double watts, Color accent, bool pill)
    {
        dc.DrawEllipse(ChartInk.Brush(this, "A.C.MarkerFill", Color.FromRgb(0x0C, 0x16, 0x40)), ChartInk.Pen(ChartInk.Brush(accent), 2.5), new Point(x, y), 5.5, 5.5);
        if (!pill) return;
        var value = ChartInk.Text(this, Format.WholeWatts(watts, CultureInfo.CurrentCulture) + " W", 11.5,
            ChartInk.Brush(this, "A.C.Ink", Color.FromRgb(0x16, 0x18, 0x1C)), semi: true);
        var lw = Math.Max(50, value.Width + 16);
        var px = Math.Clamp(x - lw / 2, 0, Math.Max(0, Ox + LiveScale.X1 * S - lw));
        ChartInk.Pill(dc, new Rect(px, y - 33, lw, 22), ChartInk.Brush(this, "A.C.Pill", Color.FromRgb(0xF4, 0xF4, 0xF0)));
        ChartInk.At(dc, value, px + lw / 2, y - 18, 1);
    }

    /// <summary>The readings joined straight, as the mockup's path, closed under them for its fill.</summary>
    private static PathGeometry Polyline(IReadOnlyList<Point> points, bool close)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = close, IsFilled = close };
        figure.Segments.Add(new PolyLineSegment(points.Skip(1), true) { IsSmoothJoin = true });
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private void DrawHover(Color accent)
    {
        using var dc = _hover.RenderOpen();
        _hoverDrawn = false;
        if (_hoverAge is not { } age || Readings.Count == 0) return;
        _hoverDrawn = true;
        var nearest = Readings.MinBy(s => Math.Abs(s.AgeSeconds - age));
        var at = Map(LiveScale.X(nearest.AgeSeconds), _scale.Y(nearest.Watts));
        Marker(dc, at.X, at.Y, nearest.Watts, accent, pill: true);
    }

    private void UpdateReveal()
    {
        if (ActualWidth <= 0) return;
        var shown = new Rect(-20, -40, Math.Max(0, Ox + (LiveScale.X0 + Reveal * (LiveScale.X1 + 12 - LiveScale.X0)) * S + 20), ActualHeight + 80);
        if (_clip.Clip is not RectangleGeometry { Rect: var was } || was != shown) _clip.Clip = new RectangleGeometry(shown);
        _marker.Opacity = Reveal < .98 || _hoverAge is not null ? 0 : 1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = (e.GetPosition(this).X - Ox) / Math.Max(S, 1e-6);
        _hoverAge = Math.Clamp((LiveScale.X1 - x) / (LiveScale.X1 - LiveScale.X0) * LiveScale.Span, 0, LiveScale.Span);
        _marker.Opacity = 0;
        DrawHover(ChartInk.Colour(this, "A.C.Accent", Color.FromRgb(0xD9, 0xF2, 0x5A)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverAge = null;
        using (_hover.RenderOpen())
        {
        }
        _hoverDrawn = false;
        UpdateReveal();
    }
}
