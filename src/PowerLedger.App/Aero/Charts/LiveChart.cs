using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PowerLedger.App.Aero;

/// <summary>
/// Last minute's scale (Aero look design §1), pure, on the prototype's 400 × 190 drawing: the readings of the last 60 s
/// from a minute ago at the left to now at the right, and watts on round steps that hold every reading and the day's
/// average with room above and below.
/// </summary>
/// <param name="Grid">The gridlines' watts, bottom up, the top one at <see cref="Hi"/>.</param>
internal sealed record LiveScale(double Lo, double Hi, double Step, IReadOnlyList<double> Grid)
{
    public const double X0 = 34, X1 = 392, Y0 = 18, Y1 = 166, Width = 400, Height = 190, Span = 60;

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
/// Last minute (Aero look design §1, the prototype's LiveChart on real readings): the last 60 s of <see
/// cref="LivePanel.Spark"/> as the accent line with its glow, the day's average as a faint ghost, a marker and a value pill
/// on the newest reading with the time under it, and a crosshair that reads any second under the pointer. A new reading
/// redraws the line once and slides it a second's width into place on a composited transform, so the scroll costs nothing
/// between readings and nothing runs at rest; under reduced motion it is simply redrawn.
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
    private readonly TranslateTransform _slide = new();
    private static readonly IEasingFunction SlideEase = ChartInk.Frozen(new PowerEase { Power = 4, EasingMode = EasingMode.EaseOut });
    private LiveScale _scale = LiveScale.For([], double.NaN);
    private double? _hoverAge;
    private DateTimeOffset _newestAt = DateTimeOffset.Now;

    public LiveChart()
    {
        Cursor = Cursors.Cross;
        _kids = new VisualCollection(this) { _static, _clip, _marker, _hover };
        _clip.Children.Add(_line);
        _line.Transform = _slide;
        _marker.Transform = _slide;   // the marker rides on the newest reading as it slides in
        AutomationProperties.SetName(this, "Last minute");
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>The last minute's readings, each with its age, as the live panel has them.</summary>
    public IReadOnlyList<SparkSample>? Samples { get => (IReadOnlyList<SparkSample>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    /// <summary>The day's average watts, drawn as the ghost line; NaN for none.</summary>
    public double Average { get => (double)GetValue(AverageProperty); set => SetValue(AverageProperty, value); }

    /// <summary>How much of the line is drawn, 0 to 1: the intro draws it in from the left.</summary>
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    /// <summary>The scale the chart is drawn on, for a test.</summary>
    internal LiveScale Scale => _scale;

    protected override int VisualChildrenCount => _kids.Count;

    protected override Visual GetVisualChild(int index) => _kids[index];

    private double Sx => ActualWidth / LiveScale.Width;

    private double Sy => ActualHeight / LiveScale.Height;

    private IReadOnlyList<SparkSample> Readings => Samples ?? [];

    /// <summary>A new reading: the time of the newest moves on, and the line slides a second's width into place.</summary>
    private void OnSamples()
    {
        _newestAt = DateTimeOffset.Now;
        Redraw();
        var step = (LiveScale.X(0) - LiveScale.X(1)) * Sx;
        if (Readings.Count > 1 && step > 0) AeroMotion.Move(_slide, TranslateTransform.XProperty, 0, 1000, SlideEase, from: step);
        else _slide.BeginAnimation(TranslateTransform.XProperty, null);
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
        double sx = Sx, sy = Sy;
        var accent = ChartInk.Colour(this, "A.C.Accent", Color.FromRgb(0xD3, 0xF0, 0x3F));
        var axis = ChartInk.Brush(this, "A.C.Text3", Color.FromArgb(0x70, 0xF3, 0xF4, 0xF6));
        var grid = ChartInk.Pen(ChartInk.Brush(this, "A.C.Grid", Color.FromArgb(0x0F, 255, 255, 255)), 1);
        using (var dc = _static.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
            foreach (var w in _scale.Grid)
            {
                var y = _scale.Y(w) * sy;
                dc.DrawLine(grid, new Point(LiveScale.X0 * sx, y), new Point(LiveScale.X1 * sx, y));
                ChartInk.At(dc, ChartInk.Text(this, Format.Scale(w, _scale.Step, CultureInfo.CurrentCulture), 11, axis), 0, y + 4);
            }
            if (double.IsFinite(Average) && readings.Count > 0)
            {
                var ghost = ChartInk.Pen(ChartInk.Brush(this, "A.C.Ghost", Color.FromArgb(0x33, 255, 255, 255)), 1.5, [4, 4]);
                var y = _scale.Y(Average) * sy;
                dc.DrawLine(ghost, new Point(LiveScale.X0 * sx, y), new Point(LiveScale.X1 * sx, y));
            }
            if (readings.Count == 0)
            {
                ChartInk.At(dc, ChartInk.Text(this, Format.NoReading, 13, axis), ActualWidth / 2, ActualHeight / 2, 1);
            }
        }
        var points = readings.OrderByDescending(s => s.AgeSeconds).Select(s => new Point(LiveScale.X(s.AgeSeconds) * sx, _scale.Y(s.Watts) * sy)).ToList();
        using (var dc = _line.RenderOpen())
        {
            if (points.Count > 1)
            {
                var curve = ChartInk.Smooth(points);
                foreach (var (alpha, width) in new[] { (14, 18.0), (24, 11.0), (38, 6.5) })
                    dc.DrawGeometry(null, ChartInk.Pen(ChartInk.Brush(Color.FromArgb((byte)alpha, accent.R, accent.G, accent.B)), width, round: true), curve);
                dc.DrawGeometry(null, ChartInk.Pen(ChartInk.Brush(accent), 3, round: true), curve);
            }
        }
        using (var dc = _marker.RenderOpen())
        {
            if (readings.Count > 0 && _hoverAge is null)
            {
                var newest = readings.OrderBy(s => s.AgeSeconds).First();
                Marker(dc, LiveScale.X(0) * sx, _scale.Y(newest.Watts) * sy, newest.Watts, _newestAt, accent);
            }
        }
        DrawHover(accent);
        UpdateReveal();
    }

    /// <summary>The marker, its value pill above and its time pill on the axis under it.</summary>
    private void Marker(DrawingContext dc, double x, double y, double watts, DateTimeOffset at, Color accent)
    {
        double sx = Sx, sy = Sy;
        var guide = ChartInk.Pen(ChartInk.Brush(this, "A.C.Guide", Color.FromArgb(0x47, 255, 255, 255)), 1, [3, 4]);
        dc.DrawLine(guide, new Point(x, LiveScale.Y0 * sy), new Point(x, LiveScale.Y1 * sy));
        dc.DrawEllipse(ChartInk.Brush(this, "A.C.MarkerFill", Color.FromRgb(0x1B, 0x1D, 0x20)), ChartInk.Pen(ChartInk.Brush(accent), 2.5), new Point(x, y), 5.5, 5.5);
        var value = ChartInk.Text(this, Format.WholeWatts(watts, CultureInfo.CurrentCulture) + " W", 11.5,
            ChartInk.Brush(this, "A.C.Ink", Color.FromRgb(0x16, 0x18, 0x1C)), semi: true);
        var lw = Math.Max(50, value.Width + 16);
        var px = Math.Min(x - lw / 2, LiveScale.X1 * sx - lw);
        ChartInk.Pill(dc, new Rect(px, y - 33, lw, 22), ChartInk.Brush(this, "A.C.Pill", Color.FromRgb(0xF4, 0xF4, 0xF0)));
        ChartInk.At(dc, value, px + lw / 2, y - 18, 1);
        var time = ChartInk.Text(this, at.ToLocalTime().ToString(CultureInfo.CurrentCulture.DateTimeFormat.LongTimePattern, CultureInfo.CurrentCulture), 10.5,
            ChartInk.Brush(this, "A.C.Text", Color.FromRgb(0xF3, 0xF4, 0xF6)));
        var tw = Math.Max(68, time.Width + 14);
        var tx = Math.Min(x - tw / 2, LiveScale.X1 * sx - tw);
        ChartInk.Pill(dc, new Rect(tx, LiveScale.Y1 * sy + 4, tw, 19), ChartInk.Brush(this, "A.C.TimePill", Color.FromArgb(0x40, 0, 0, 0)), guide);
        ChartInk.At(dc, time, tx + tw / 2, LiveScale.Y1 * sy + 17, 1);
    }

    private void DrawHover(Color accent)
    {
        using var dc = _hover.RenderOpen();
        if (_hoverAge is not { } age || Readings.Count == 0) return;
        var nearest = Readings.MinBy(s => Math.Abs(s.AgeSeconds - age));
        Marker(dc, LiveScale.X(nearest.AgeSeconds) * Sx, _scale.Y(nearest.Watts) * Sy, nearest.Watts, _newestAt.AddSeconds(-nearest.AgeSeconds), accent);
    }

    private void UpdateReveal()
    {
        if (ActualWidth <= 0) return;
        _clip.Clip = new RectangleGeometry(new Rect(-20, -40, Math.Max(0, (LiveScale.X0 + Reveal * (LiveScale.X1 + 12 - LiveScale.X0)) * Sx + 20), ActualHeight + 80));
        _marker.Opacity = Reveal < .98 || _hoverAge is not null ? 0 : 1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X / Math.Max(Sx, 1e-6);
        _hoverAge = Math.Clamp((LiveScale.X1 - x) / (LiveScale.X1 - LiveScale.X0) * LiveScale.Span, 0, LiveScale.Span);
        _marker.Opacity = 0;
        DrawHover(ChartInk.Colour(this, "A.C.Accent", Color.FromRgb(0xD3, 0xF0, 0x3F)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverAge = null;
        using (_hover.RenderOpen())
        {
        }
        UpdateReveal();
    }
}
