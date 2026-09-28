using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using PowerLedger.Contracts;
using MediaGeometry = System.Windows.Media.Geometry;

namespace PowerLedger.App.Aero;

/// <summary>One slice of the 3D pie: its part's place in the list, where it starts and ends round the disc (radians,
/// clockwise from the top), its middle, and how tall it stands when risen.</summary>
internal sealed record PieSlice(int Index, double A0, double A1, double Mid, double Height);

/// <summary>The 3D pie's slices (Aero look design §1), pure: shares scaled to a whole, clockwise from the top in the
/// parts' order, an empty share left out; the order they are painted in; and each one's rise in turn.</summary>
internal static class PieSlices
{
    public static IReadOnlyList<PieSlice> From(IReadOnlyList<double> shares)
    {
        var total = shares.Where(s => double.IsFinite(s) && s > 0).Sum();
        if (total <= 0) return [];
        var slices = new List<PieSlice>();
        var a = -Math.PI / 2;
        for (var i = 0; i < shares.Count; i++)
        {
            if (!double.IsFinite(shares[i]) || shares[i] <= 0) continue;
            var share = shares[i] / total;
            var end = a + share * Math.PI * 2;
            slices.Add(new PieSlice(i, a, end, (a + end) / 2, 12 + share * 100 * .5));
            a = end;
        }
        return slices;
    }

    /// <summary>Back to front on the tilted disc: the slice whose middle is furthest up first.</summary>
    public static IReadOnlyList<PieSlice> PaintOrder(IReadOnlyList<PieSlice> slices) => [.. slices.OrderBy(s => Math.Sin(s.Mid))];

    /// <summary>How far slice <paramref name="index"/> of <paramref name="count"/> has risen, 0 to 1, when the pie's rise
    /// is <paramref name="rise"/> of the way through: each starts <see cref="AeroMotion.PieStagger"/> after the one before
    /// and takes <see cref="AeroMotion.PieRise"/>.</summary>
    public static double Progress(double rise, int index, int count)
    {
        var total = AeroMotion.PieRise + Math.Max(0, count - 1) * AeroMotion.PieStagger;
        return Math.Clamp((rise * total - index * AeroMotion.PieStagger) / AeroMotion.PieRise, 0, 1);
    }

    /// <summary>The whole rise's length, for the animation that drives <see cref="Progress"/>.</summary>
    public static double RiseMs(int count) => AeroMotion.PieRise + Math.Max(0, count - 1) * AeroMotion.PieStagger;

    /// <summary>The prototype's spring-back curve for a slice's height.</summary>
    public static double Back(double t)
    {
        const double c = 1.5;
        return 1 + (c + 1) * Math.Pow(t - 1, 3) + c * Math.Pow(t - 1, 2);
    }
}

/// <summary>
/// Where the power goes (Aero look design §1, the prototype's extruded pie on real shares): each part a slice in its
/// colour, rising in turn and lifting under the pointer. Everything moves through two animated properties, <see
/// cref="Rise"/> and <see cref="Lift"/>, so a frame is drawn only while one of them runs and nothing runs at rest.
/// </summary>
internal sealed class PieChart3D : FrameworkElement
{
    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(nameof(Parts), typeof(IReadOnlyList<DashboardPart>),
        typeof(PieChart3D), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((PieChart3D)d).OnParts()));

    public static readonly DependencyProperty RiseProperty = DependencyProperty.Register(nameof(Rise), typeof(double),
        typeof(PieChart3D), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LiftProperty = DependencyProperty.Register(nameof(Lift), typeof(double),
        typeof(PieChart3D), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoveredProperty = DependencyProperty.Register(nameof(Hovered), typeof(Part?),
        typeof(PieChart3D), new PropertyMetadata(null, (d, e) => ((PieChart3D)d).OnHovered((Part?)e.OldValue)));

    private const double Cx = 120, Cy = 104, R = 92, K = .5, Vw = 240, Vh = 190;
    private IReadOnlyList<PieSlice> _slices = [];
    private readonly Dictionary<int, (MediaGeometry Top, MediaGeometry? Wall, MediaGeometry S0, MediaGeometry S1)> _shapes = [];
    private Part? _leaving;

    public PieChart3D()
    {
        AutomationProperties.SetName(this, "Where the power goes");
    }

    /// <summary>The parts, CPU first, each with its share.</summary>
    public IReadOnlyList<DashboardPart>? Parts { get => (IReadOnlyList<DashboardPart>?)GetValue(PartsProperty); set => SetValue(PartsProperty, value); }

    /// <summary>How far the rise has gone, 0 to 1; the intro animates it.</summary>
    public double Rise { get => (double)GetValue(RiseProperty); set => SetValue(RiseProperty, value); }

    /// <summary>How far the hovered slice has lifted, 0 to 1 (and the one it left has come down).</summary>
    public double Lift { get => (double)GetValue(LiftProperty); set => SetValue(LiftProperty, value); }

    /// <summary>The part under the pointer, here or in the list beside the pie, which lifts; null for none.</summary>
    public Part? Hovered { get => (Part?)GetValue(HoveredProperty); set => SetValue(HoveredProperty, value); }

    /// <summary>The slices drawn, for a test.</summary>
    internal IReadOnlyList<PieSlice> Slices => _slices;

    /// <summary>A part's colour as the palette gives it: the accent for the CPU, then green, violet and blue, as the demo.</summary>
    public static string ColourKey(Part part) => part switch
    {
        Part.Cpu => "A.C.Accent",
        Part.Gpu => "A.C.Green",
        Part.Display => "A.C.Violet",
        _ => "A.C.Blue",
    };

    /// <summary>Starts the slices rising in turn, or sets them risen under reduced motion.</summary>
    public void PlayRise() => AeroMotion.Move(this, RiseProperty, 1, PieSlices.RiseMs(_slices.Count), null, from: 0);

    private void OnParts()
    {
        var parts = Parts ?? [];
        _slices = PieSlices.From([.. parts.Select(p => p.Share)]);
        AutomationProperties.SetHelpText(this, string.Join(", ", parts.Where(p => p.Share > 0)
            .Select(p => $"{p.Name} {Format.Percent(p.Share, CultureInfo.CurrentCulture)}")));
    }

    private void OnHovered(Part? was)
    {
        _leaving = was;
        AeroMotion.Move(this, LiftProperty, 1, AeroMotion.Hover, AeroMotion.Glide, from: 0);
    }

    private double LiftOf(Part part) => part == Hovered ? Lift : part == _leaving ? 1 - Lift : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        var parts = Parts ?? [];
        foreach (var slice in PieSlices.PaintOrder(_slices).Reverse())
        {
            if (!_shapes.TryGetValue(slice.Index, out var s)) continue;
            if (s.Top.FillContains(at) || (s.Wall?.FillContains(at) ?? false) || s.S0.FillContains(at) || s.S1.FillContains(at))
            {
                if (slice.Index < parts.Count) Hovered = parts[slice.Index].Part;
                return;
            }
        }
        Hovered = null;
    }

    protected override void OnMouseLeave(MouseEventArgs e) => Hovered = null;

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var k = Math.Min(ActualWidth / Vw, ActualHeight / Vh);
        var ox0 = (ActualWidth - Vw * k) / 2;
        var oy0 = (ActualHeight - Vh * k) / 2;
        Point M(double x, double y) => new(ox0 + x * k, oy0 + y * k);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        dc.DrawEllipse(ChartInk.Brush(Color.FromArgb(13, 255, 255, 255)), ChartInk.Pen(ChartInk.Brush(Color.FromArgb(61, 255, 255, 255)), 1),
            M(Cx, Cy + 3), (R + 16) * k, (R + 16) * K * k);
        dc.DrawEllipse(null, ChartInk.Pen(ChartInk.Brush(Color.FromArgb(26, 255, 255, 255)), 1), M(Cx, Cy + 3), (R + 6) * k, (R + 6) * K * k);
        _shapes.Clear();
        var parts = Parts ?? [];
        if (_slices.Count == 0)
        {
            var axis = ChartInk.Brush(this, "A.C.Text3", Color.FromArgb(0x70, 0xF3, 0xF4, 0xF6));
            ChartInk.At(dc, ChartInk.Text(this, Format.NoReading, 13, axis), ActualWidth / 2, ActualHeight / 2, 1);
            return;
        }
        var edge = ChartInk.Pen(ChartInk.Brush(Color.FromArgb(102, 255, 255, 255)), .8);
        foreach (var slice in PieSlices.PaintOrder(_slices))
        {
            var part = slice.Index < parts.Count ? parts[slice.Index].Part : Part.Rest;
            var colour = ChartInk.Colour(this, ColourKey(part), Colors.Gray);
            var progress = PieSlices.Progress(Rise, _slices.ToList().IndexOf(slice), _slices.Count);
            var lift = LiftOf(part);
            var h = progress <= 0 ? 0 : Math.Max(0, slice.Height * PieSlices.Back(progress) + lift * 8);
            var d = 7 * AeroMotion.EaseOutQuart(progress) + lift * 10;
            var ox = Math.Cos(slice.Mid) * d;
            var oy = Math.Sin(slice.Mid) * d * K;
            Point Pt(double angle, double r, double z) => M(Cx + ox + r * Math.Cos(angle), Cy + oy + r * Math.Sin(angle) * K - z);

            MediaGeometry Side(double angle) => Poly([Pt(0, 0, h), Pt(angle, R, h), Pt(angle, R, 0), Pt(0, 0, 0)]);
            var s0 = Side(slice.A0);
            var s1 = Side(slice.A1);
            MediaGeometry? wall = null;
            var (w0, w1) = (Math.Max(slice.A0, 0), Math.Min(slice.A1, Math.PI));
            if (w1 > w0)
            {
                var steps = Math.Max(3, (int)Math.Ceiling((w1 - w0) / .06));
                var up = Enumerable.Range(0, steps + 1).Select(s => Pt(w0 + (w1 - w0) * s / steps, R, h));
                var down = Enumerable.Range(0, steps + 1).Reverse().Select(s => Pt(w0 + (w1 - w0) * s / steps, R, 0));
                wall = Poly([.. up, .. down]);
            }
            var topSteps = Math.Max(4, (int)Math.Ceiling((slice.A1 - slice.A0) / .06));
            var top = Poly([Pt(0, 0, h), .. Enumerable.Range(0, topSteps + 1).Select(s => Pt(slice.A0 + (slice.A1 - slice.A0) * s / topSteps, R, h))]);
            _shapes[slice.Index] = (top, wall, s0, s1);

            var side = ChartInk.Brush(Shade(colour, .42));
            dc.DrawGeometry(side, null, s0);
            dc.DrawGeometry(side, null, s1);
            if (wall != null) dc.DrawGeometry(ChartInk.Brush(Shade(colour, .62)), null, wall);
            dc.PushOpacity(.94);
            dc.DrawGeometry(ChartInk.Brush(colour), null, top);
            dc.Pop();
            dc.DrawGeometry(null, edge, top);
        }
    }

    private static Color Shade(Color c, double f) => Color.FromRgb((byte)Math.Round(c.R * f), (byte)Math.Round(c.G * f), (byte)Math.Round(c.B * f));

    private static MediaGeometry Poly(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(points[0], true, true);
            c.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        return ChartInk.Frozen(geometry);
    }
}
